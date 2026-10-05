using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neuterradise.App.Design.ProfileCards;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.MediaLayouts;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Profiles;
using Neuterradise.App.Media;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.Presentation;

/// <summary>
/// A Profile's committed appearance as the resolver needs it (the existing durable authority).
/// <see cref="Sources"/> carries the Cover/Banner source assets so a Customization session previews and
/// commits a source change together with its framing; null means the sources are not part of this state.
/// </summary>
public sealed record ProfilePresentationState(
    Guid ProfileId,
    long RowVersion,
    string? LayoutPresetId,
    ProfileAppearanceOverrides Overrides,
    ProfileMediaSources? Sources = null)
{
    public ProfileMediaPresentation Media => ProfileMediaPresentation.From(Overrides);
}

/// <summary>The Cover and Banner source assets of a Profile (the frame timestamp lives in the overrides).</summary>
public sealed record ProfileMediaSources(Guid? CoverMediaId, Guid? BannerMediaId, Guid? FigureMediaId = null);

/// <summary>Where a resolution happens: which surface, Profile and item are in view.</summary>
public sealed record PresentationContext(string? Surface = null, Guid? ProfileId = null, Guid? ItemId = null, ProfilePresentationState? Profile = null)
{
    public static PresentationContext Global { get; } = new();

    public static PresentationContext ForProfile(ProfilePresentationState profile, string surface = "profile") =>
        new(surface, profile.ProfileId, null, profile);
}

public sealed record ResolutionStep(ResolutionSource Source, DefinitionRef? Definition, string? StateJson);

public sealed record ResolvedSelection(
    string Slot,
    DefinitionRef? Definition,
    string? StateJson,
    ResolutionSource Source,
    IReadOnlyList<ResolutionStep> Chain)
{
    /// <summary>True when the value at <paramref name="scope"/> comes from a parent rather than an override there.</summary>
    public bool IsInheritedAt(ScopeKind scope) => !Chain.Any(step => step.Source == ToSource(scope) && (step.Definition is not null || step.StateJson is not null));

    public static ResolutionSource ToSource(ScopeKind scope) => scope switch
    {
        ScopeKind.Global => ResolutionSource.Global,
        ScopeKind.Surface => ResolutionSource.Surface,
        ScopeKind.Profile => ResolutionSource.Profile,
        _ => ResolutionSource.Item,
    };
}

public sealed class PresentationChangedEventArgs(IReadOnlyCollection<string> slots, bool packsChanged) : EventArgs
{
    public IReadOnlyCollection<string> Slots { get; } = slots;

    public bool PacksChanged { get; } = packsChanged;

    public bool Affects(string slot) => PacksChanged || Slots.Contains(slot);
}

public sealed record PresentationApplyResult(bool Succeeded, string? Message, long? ProfileRowVersion = null);

public sealed record PackRemovalResult(bool Removed, int BindingsReset, string? Message);

/// <summary>
/// The presentation authority for one Vault: registry (built-in + user packs through the same compiler),
/// generic bindings, slot-owned scope resolution, preview transactions and atomic Apply.
///
/// Profile-scoped Layout, Card, Frame, Cover and Banner stay on their existing durable authority
/// (<c>profile_appearance</c>). User pack layouts, cards, and frames use Profile-scope bindings.
/// </summary>
public sealed class PresentationRuntime
{
    private readonly CatalogDb _catalog;
    private readonly PresentationPackStore _packs;
    private readonly PresentationBindingStore _store;
    private readonly PresentationCompiler _compiler;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _packMutations = new(1, 1);
    private Dictionary<DefinitionRef, CompiledDefinition> _definitions = [];
    private Dictionary<string, IReadOnlyList<CompiledDefinition>> _byKind = new(StringComparer.Ordinal);
    private BindingSet _committed = BindingSet.Empty;

    public PresentationRuntime(CatalogDb catalog, PresentationPackStore packs)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _store = new PresentationBindingStore(catalog);
        _compiler = new PresentationCompiler(packs);
    }

    public event EventHandler<PresentationChangedEventArgs>? Changed;

    public BindingSet Committed
    {
        get { lock (_sync) return _committed; }
    }

    public IReadOnlyList<InstalledPack> Packs => _packs.Packs;

    public PresentationPackStore PackStore => _packs;

    public IPresentationAssetStore Assets => _packs;

    public int CompiledPlanCount => _compiler.CachedCount;

    /// <summary>Loads packs, compiles definitions, and loads bindings.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default, string? legacyConfigurationTheme = null)
    {
        await _packs.RecoverPendingAsync(await _store.ReadRecordedPackHashesAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await Task.Run(() => _packs.LoadAll(), cancellationToken).ConfigureAwait(false);
        foreach (var pack in _packs.Packs)
        {
            try
            {
                await _store.RecordPackAsync(pack, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception))
            {
                Trace.TraceWarning("Presentation pack registry could not record {0}: {1}", pack.PackId, exception.GetType().Name);
            }
        }

        RebuildCatalog();
        await _store.MigrateLegacyThemeAsync(legacyConfigurationTheme, id =>
        {
            var reference = BuiltInPresentationCatalog.Normalize(DefinitionRef.BuiltIn("builtin.neuterradise.theme." + id.Trim().ToLowerInvariant()));
            return IsUsable(reference, DefinitionKinds.Theme, PresentationSlots.Theme) ? reference : null;
        }, cancellationToken).ConfigureAwait(false);
        // Global motion presets were retired. Remove any durable selection from that retired slot
        // before loading bindings so motion can no longer survive as a hidden user preference.
        await _store.DeleteRetiredSlotAsync("appearance.motion", cancellationToken).ConfigureAwait(false);
        await _store.DeleteRetiredSlotAsync("appearance.icons", cancellationToken).ConfigureAwait(false);
        // The current Profile media contract uses split presentation defaults. Remove retired
        // unified-layout bindings so hidden state cannot override that contract.
        await _store.DeleteRetiredSlotAsync("profile.media.layout", cancellationToken).ConfigureAwait(false);
        await _store.DeleteRetiredSlotAsync("profile.media.tile", cancellationToken).ConfigureAwait(false);
        await _store.DeleteRetiredSlotAsync("profile.media.border", cancellationToken).ConfigureAwait(false);
        await _store.DeleteRetiredSlotAsync("profile.media.info", cancellationToken).ConfigureAwait(false);
        // Gallery density/details was merged into Gallery Layout. Remove the retired state binding so
        // stale card-size/detail toggles cannot survive as hidden presentation state.
        await _store.DeleteRetiredSlotAsync("gallery.density", cancellationToken).ConfigureAwait(false);
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var replacements = loaded.All
            .Where(b => b.Definition is { } reference && BuiltInPresentationCatalog.Normalize(reference) != reference)
            .Select(b => new BindingChange(b.ScopeKind, b.ScopeId, b.Slot, BuiltInPresentationCatalog.Normalize(b.Definition!.Value), b.StateJson))
            .Where(change => PresentationSlots.TryGet(change.Slot, out var descriptor)
                && IsUsable(change.Definition!.Value, descriptor.Kind, change.Slot))
            .ToArray();
        if (replacements.Length > 0)
        {
            loaded = await _store.ApplyAsync(replacements, cancellationToken).ConfigureAwait(false);
        }
        lock (_sync)
        {
            _committed = loaded;
        }

    }

    // ---------------------------------------------------------------- registry

    public IReadOnlyList<CompiledDefinition> DefinitionsOf(string kind)
    {
        lock (_sync)
        {
            return _byKind.TryGetValue(kind, out var list) ? list : [];
        }
    }

    public CompiledDefinition? Find(DefinitionRef? reference)
    {
        if (reference is not { } value)
        {
            return null;
        }

        lock (_sync)
        {
            return _definitions.TryGetValue(value, out var compiled) ? compiled : null;
        }
    }

    private void RebuildCatalog()
    {
        var definitions = new Dictionary<DefinitionRef, CompiledDefinition>();
        foreach (var pack in _packs.Packs.Where(p => p.IsUsable))
        {
            foreach (var definition in pack.Manifest.Definitions)
            {
                var compiled = _compiler.Compile(pack, definition);
                if (compiled is null)
                {
                    Trace.TraceWarning("Presentation definition {0} failed to compile and falls back.", definition.Id);
                    continue;
                }

                definitions[compiled.Ref] = compiled;
            }
        }

        var byKind = definitions.Values
            .GroupBy(d => d.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CompiledDefinition>)[.. g.OrderBy(d => d.Origin).ThenBy(d => SortIndex(d))], StringComparer.Ordinal);
        lock (_sync)
        {
            _definitions = definitions;
            _byKind = byKind;
        }
    }

    private int SortIndex(CompiledDefinition definition)
    {
        if (definition.Origin != PackOrigin.BuiltIn)
        {
            return 0;
        }

        var manifest = _packs.Packs.First(p => p.Origin == PackOrigin.BuiltIn).Manifest;
        for (var i = 0; i < manifest.Definitions.Count; i++)
        {
            if (manifest.Definitions[i].Id == definition.Ref.DefinitionId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // ---------------------------------------------------------------- resolution

    public ResolvedSelection Resolve(string slot, PresentationContext context, BindingSet? bindings = null)
    {
        var descriptor = PresentationSlots.Get(slot);
        var set = bindings ?? Committed;
        var chain = new List<ResolutionStep> { new(ResolutionSource.BuiltInDefault, descriptor.Default, null) };

        void Add(ResolutionSource source, PresentationBinding? binding)
        {
            if (binding is not null)
            {
                chain.Add(new ResolutionStep(source, binding.Definition, binding.StateJson));
            }
        }

        if (descriptor.AllowsScope(ScopeKind.Global))
        {
            Add(ResolutionSource.Global, set.Get(ScopeKind.Global, ScopeIds.Global, slot));
        }

        if (descriptor.AllowsScope(ScopeKind.Surface) && context.Surface is { } surface)
        {
            Add(ResolutionSource.Surface, set.Get(ScopeKind.Surface, surface, slot));
        }

        if (descriptor.AllowsScope(ScopeKind.Profile) && context.ProfileId is { } profileId)
        {
            var binding = set.Get(ScopeKind.Profile, ScopeIds.Of(profileId), slot);
            if (binding is not null)
            {
                Add(ResolutionSource.Profile, binding);
            }
            else if (BridgedProfileDefinition(slot, context.Profile) is { } bridged)
            {
                chain.Add(new ResolutionStep(ResolutionSource.Profile, bridged, null));
            }
        }

        if (descriptor.AllowsScope(ScopeKind.Item) && context.ItemId is { } itemId)
        {
            Add(ResolutionSource.Item, set.Get(ScopeKind.Item, ScopeIds.Of(itemId), slot));
        }

        // Definition: the most specific step whose definition is usable and of the slot's kind.
        DefinitionRef? definition = null;
        var source = ResolutionSource.BuiltInDefault;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Definition is { } requested && BuiltInPresentationCatalog.Normalize(requested) is var candidate && IsUsable(candidate, descriptor.Kind, slot))
            {
                definition = candidate;
                source = chain[i].Source;
                break;
            }
        }

        if (descriptor.Kind is not null && definition is null)
        {
            definition = DefinitionsOf(descriptor.Kind).FirstOrDefault(d => SupportsSlot(d, slot))?.Ref;
        }

        // State: shallow merge from least to most specific, so a delta only overrides what it names.
        string? state = null;
        foreach (var step in chain.Where(s => s.StateJson is not null))
        {
            state = MergeState(state, step.StateJson!);
            if (descriptor.IsStateOnly)
            {
                source = step.Source;
            }
        }

        return new ResolvedSelection(slot, definition, state, source, chain);
    }

    /// <summary>Resolves a slot to a compiled definition. Never returns null for a definition slot.</summary>
    public CompiledDefinition ResolveCompiled(string slot, PresentationContext context, BindingSet? bindings = null)
    {
        var descriptor = PresentationSlots.Get(slot);
        var resolved = Resolve(slot, context, bindings);
        return Find(resolved.Definition) is { } resolvedDefinition && SupportsSlot(resolvedDefinition, slot)
            ? resolvedDefinition
            : Find(descriptor.Default) is { } defaultDefinition && SupportsSlot(defaultDefinition, slot)
                ? defaultDefinition
                : DefinitionsOf(descriptor.Kind ?? string.Empty).FirstOrDefault(d => SupportsSlot(d, slot))
                    ?? throw new InvalidOperationException($"No usable definition exists for slot '{slot}'.");
    }

    public T ResolvePlan<T>(string slot, PresentationContext context, BindingSet? bindings = null) where T : class =>
        ResolveCompiled(slot, context, bindings).PlanAs<T>();

    public ProfileMediaPresentation ResolveMedia(ProfilePresentationState profile) => profile.Media;

    private bool IsUsable(DefinitionRef reference, string? kind, string? slot = null) =>
        Find(reference) is { } compiled && (kind is null || compiled.Kind == kind)
            && (slot is null || SupportsSlot(compiled, slot));

    public static bool SupportsSlot(CompiledDefinition definition, string slot)
    {
        if (definition.Plan is EffectPlan effect)
        {
            return effect.Supports(slot);
        }

        if (definition.Kind == DefinitionKinds.Backdrop)
        {
            if (definition.Tags.Contains("profile-only", StringComparer.Ordinal))
            {
                return slot == PresentationSlots.ProfileBackdrop;
            }

            if (definition.Tags.Contains("home-only", StringComparer.Ordinal))
            {
                return slot == PresentationSlots.HomeBackdrop;
            }
        }

        return true;
    }

    private static string MergeState(string? baseJson, string overlayJson)
    {
        if (baseJson is null)
        {
            return overlayJson;
        }

        try
        {
            var target = JsonNode.Parse(baseJson) as JsonObject ?? [];
            if (JsonNode.Parse(overlayJson) is JsonObject overlay)
            {
                foreach (var (key, value) in overlay)
                {
                    target[key] = value?.DeepClone();
                }
            }

            return target.ToJsonString();
        }
        catch (JsonException)
        {
            return overlayJson;
        }
    }

    // ---------------------------------------------------------------- Profile bridge

    public static DefinitionRef? BridgedProfileDefinition(string slot, ProfilePresentationState? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return slot switch
        {
            PresentationSlots.ProfileLayout when profile.LayoutPresetId is { } preset => DefinitionRef.BuiltIn($"builtin.neuterradise.profile.{preset}"),
            PresentationSlots.ProfileCard when profile.Overrides.ProfileCardVariantId is { } variant => CardRefForVariant(variant),
            PresentationSlots.ProfileMediaLayout => DefinitionRef.BuiltIn($"builtin.neuterradise.media-layout.{MediaLayoutCatalog.Normalize(profile.Overrides.MediaLayoutId)}"),
            PresentationSlots.ProfileFrame when profile.Overrides.CoverFrameId is null or CoverFrameCatalog.NoneFrameId =>
                DefinitionRef.BuiltIn(profile.Overrides.CoverShape?.ToLowerInvariant() switch
                {
                    "circle" => "builtin.neuterradise.frame.none-circle",
                    "flower" => "builtin.neuterradise.frame.none-flower",
                    _ => "builtin.neuterradise.frame.none-rounded",
                }),
            PresentationSlots.ProfileFrame when profile.Overrides.CoverFrameId is { } frame => DefinitionRef.BuiltIn($"builtin.neuterradise.frame.{frame}"),
            _ => null,
        };
    }

    public static DefinitionRef CardRefForVariant(string variantId) =>
        DefinitionRef.BuiltIn(variantId == "hero-card" ? "builtin.neuterradise.card.hero" : $"builtin.neuterradise.card.{variantId}");

    /// <summary>The legacy field value a built-in definition maps to, or null when only a binding can express it.</summary>
    public static string? LegacyValueFor(string slot, DefinitionRef definition)
    {
        if (definition.PackId != PresentationContract.BuiltInPackId)
        {
            return null;
        }

        var id = definition.DefinitionId;
        // New data-authored definitions are represented by generic bindings. Legacy fields can
        // only encode the compatibility catalogs and must never normalize a new selection away.
        if (BuiltInPresentationCatalog.IsPrimary(definition))
        {
            return null;
        }

        return slot switch
        {
            PresentationSlots.ProfileLayout when id.StartsWith("builtin.neuterradise.profile.", StringComparison.Ordinal) => id["builtin.neuterradise.profile.".Length..],
            PresentationSlots.ProfileCard when id == "builtin.neuterradise.card.hero" => "hero-card",
            PresentationSlots.ProfileCard when id.StartsWith("builtin.neuterradise.card.", StringComparison.Ordinal) => id["builtin.neuterradise.card.".Length..],
            PresentationSlots.ProfileMediaLayout when id.StartsWith("builtin.neuterradise.media-layout.", StringComparison.Ordinal) => id["builtin.neuterradise.media-layout.".Length..],
            PresentationSlots.ProfileFrame when id.StartsWith("builtin.neuterradise.frame.", StringComparison.Ordinal) => id["builtin.neuterradise.frame.".Length..],
            PresentationSlots.Theme when id.StartsWith("builtin.neuterradise.theme.", StringComparison.Ordinal) => id["builtin.neuterradise.theme.".Length..],
            _ => null,
        };
    }

    public static bool IsBridgedProfileSlot(string slot) =>
        slot is PresentationSlots.ProfileLayout or PresentationSlots.ProfileCard or PresentationSlots.ProfileMediaLayout or PresentationSlots.ProfileFrame;

    // ---------------------------------------------------------------- preview / apply

    public PreviewSession BeginPreview(PresentationContext context) => new(this, context, Committed);

    internal async Task<PresentationApplyResult> CommitAsync(PreviewSession session, CancellationToken cancellationToken)
    {
        await _packMutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _packMutations.Release();
        }
    }

    private async Task<PresentationApplyResult> CommitCoreAsync(PreviewSession session, CancellationToken cancellationToken)
    {
        var changes = session.Working.DiffFrom(session.Baseline);
        foreach (var change in changes)
        {
            if (change.Definition is { } reference
                && (!PresentationSlots.TryGet(change.Slot, out var slot) || !IsUsable(reference, slot.Kind, change.Slot)))
                return new PresentationApplyResult(false, "A selected presentation component changed or was removed. Choose an available component before saving.");
        }
        var changedSlots = changes.Select(c => c.Slot).ToHashSet(StringComparer.Ordinal);
        long? newRowVersion = null;
        ProfilePresentationState? previous = session.OriginalProfile;
        ApplyProfilePresentationRequest? profileRequest = null;
        ProfilePresentationWriteResult? profileWrite = null;
        var profileOperations = new ProfileAppearanceOperations(_catalog);
        try
        {
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);

            if (session.ProfileWorking is { } profile && previous is not null && session.ProfileChanged)
            {
                profileRequest = new ApplyProfilePresentationRequest(
                    profile.ProfileId,
                    previous.RowVersion,
                    ApplyLayout: profile.LayoutPresetId != previous.LayoutPresetId,
                    profile.LayoutPresetId,
                    profile.Overrides,
                    SourceChangeBetween(previous, profile));
                profileWrite = await profileOperations.ApplyPresentationInTransactionAsync(
                    profileRequest, transaction, cancellationToken).ConfigureAwait(false);
                var result = profileWrite.Result;
                if (!result.IsSuccess || result.Value is null)
                {
                    return new PresentationApplyResult(false, result.Error?.UserMessage ?? "The Profile appearance could not be saved.");
                }

                newRowVersion = result.Value.RowVersion;
                var mediaLayoutOnly = profile.LayoutPresetId == previous.LayoutPresetId
                    && profile.Sources == previous.Sources
                    && profile.Overrides with { MediaLayoutId = previous.Overrides.MediaLayoutId } == previous.Overrides;
                if (mediaLayoutOnly && profile.Overrides.MediaLayoutId != previous.Overrides.MediaLayoutId)
                {
                    changedSlots.Add(PresentationSlots.ProfileMediaLayout);
                }
                else
                {
                    changedSlots.UnionWith([
                        PresentationSlots.ProfileCover,
                        PresentationSlots.ProfileBanner,
                        PresentationSlots.ProfileLayout,
                        PresentationSlots.ProfileCard,
                        PresentationSlots.ProfileMediaLayout,
                        PresentationSlots.ProfileFrame]);
                }
            }

            await _store.ApplyInTransactionAsync(transaction, changes, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _committed = _committed.Apply(changes);
            }
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return new PresentationApplyResult(false, "Your changes could not be saved: " + exception.Message);
        }

        Changed?.Invoke(this, new PresentationChangedEventArgs(changedSlots, packsChanged: false));
        return new PresentationApplyResult(true, null, newRowVersion);
    }

    /// <summary>The Cover/Banner source edits that turn <paramref name="from"/> into <paramref name="to"/>.</summary>
    internal static ProfileMediaSourceChange? SourceChangeBetween(ProfilePresentationState from, ProfilePresentationState to)
    {
        if (from.Sources is not { } before || to.Sources is not { } after)
        {
            return null;
        }

        var coverChanged = before.CoverMediaId != after.CoverMediaId
            || from.Overrides.CoverVideoTimestampMilliseconds != to.Overrides.CoverVideoTimestampMilliseconds;
        var bannerChanged = before.BannerMediaId != after.BannerMediaId;
        var figureChanged = before.FigureMediaId != after.FigureMediaId;
        return coverChanged || bannerChanged || figureChanged
            ? new ProfileMediaSourceChange(
                coverChanged,
                after.CoverMediaId,
                bannerChanged,
                after.BannerMediaId, FigureChanged: figureChanged, FigureMediaId: after.FigureMediaId)
            : null;
    }

    // ---------------------------------------------------------------- packs

    public async Task<PackInstallResult> InstallPackAsync(string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        await _packMutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _packs.InstallWithPublicationAsync(sourcePath, replaceExisting,
                _store.RecordPackAsync, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Pack is { } pack)
            {
                _compiler.Invalidate(pack.PackId);
                RebuildCatalog();
                Changed?.Invoke(this, new PresentationChangedEventArgs([], packsChanged: true));
            }

            return result;
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception))
        {
            Trace.TraceWarning("Presentation pack publication failed: {0}", exception.GetType().Name);
            return new PackInstallResult(false, null, [], "The pack could not be recorded. The previous pack was preserved.");
        }
        finally
        {
            _packMutations.Release();
        }
    }

    public Task ExportPackAsync(string packId, string destinationFile, CancellationToken cancellationToken = default) =>
        _packs.ExportAsync(packId, destinationFile, cancellationToken);

    public int CountUsages(string packId) => Committed.UsingPack(packId).Count();

    /// <summary>
    /// Removes a user pack through an atomic same-volume rename followed by one catalog transaction.
    /// If staging or the catalog write fails, the pack is restored before any failure is reported.
    /// </summary>
    public async Task<PackRemovalResult> RemovePackAsync(string packId, CancellationToken cancellationToken = default)
    {
        await _packMutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemovePackCoreAsync(packId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _packMutations.Release();
        }
    }

    private async Task<PackRemovalResult> RemovePackCoreAsync(string packId, CancellationToken cancellationToken)
    {
        if (!_packs.TryGetPack(packId, out var pack) || pack is null || pack.Origin != PackOrigin.User)
        {
            return new PackRemovalResult(false, 0, "Only installed user packs can be removed.");
        }

        var usages = Committed.UsingPack(packId).Select(b => BindingChange.Reset(b.ScopeKind, b.ScopeId, b.Slot)).ToList();
        var stage = await _packs.StageRemovalAsync(packId, cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return new PackRemovalResult(false, 0, "The pack folder could not be staged for removal.");
        }

        try
        {
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
            await _store.ApplyInTransactionAsync(transaction, usages, cancellationToken).ConfigureAwait(false);
            await PresentationBindingStore.ForgetPackInTransactionAsync(transaction, packId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            var restored = _packs.RestoreRemoval(stage);
            return new PackRemovalResult(
                false,
                0,
                restored
                    ? "The pack could not be removed; its folder and bindings were preserved."
                    : "The pack removal failed and its folder could not be restored; presentation consistency requires repair. " + exception.Message);
        }

        lock (_sync)
        {
            _committed = _committed.Apply(usages);
        }
        _packs.CompleteRemoval(stage);
        _compiler.Invalidate(packId);
        RebuildCatalog();
        Changed?.Invoke(this, new PresentationChangedEventArgs(usages.Select(u => u.Slot).ToHashSet(), packsChanged: true));
        return new PackRemovalResult(true, usages.Count, null);
    }
}

public static class ScopeIds
{
    public const string Global = "*";

    public static string Of(Guid id) => id.ToString("D");
}

/// <summary>
/// An in-memory preview transaction (document 01 Â§21.3): committed state â†’ preview copy â†’ live render.
/// Nothing reaches SQLite until <see cref="ApplyAsync"/>; <see cref="Cancel"/> simply drops the copy.
/// </summary>
public sealed class PreviewSession
{
    private readonly PresentationRuntime _runtime;

    internal PreviewSession(PresentationRuntime runtime, PresentationContext context, BindingSet committed)
    {
        _runtime = runtime;
        Context = context;
        Baseline = committed;
        Working = committed;
        OriginalProfile = context.Profile;
        ProfileWorking = context.Profile;
    }

    public event EventHandler<string>? Changed;

    public PresentationContext Context { get; private set; }

    public BindingSet Baseline { get; }

    public BindingSet Working { get; private set; }

    public ProfilePresentationState? OriginalProfile { get; private set; }

    public ProfilePresentationState? ProfileWorking { get; private set; }

    public bool ProfileChanged => ProfileWorking is not null && OriginalProfile is not null
        && (ProfileWorking.LayoutPresetId != OriginalProfile.LayoutPresetId
            || ProfileWorking.Overrides != OriginalProfile.Overrides
            || ProfileWorking.Sources != OriginalProfile.Sources);

    public bool HasChanges => ProfileChanged || Working.DiffFrom(Baseline).Count > 0;

    public bool IsClosed { get; private set; }

    /// <summary>The context with the in-preview Profile state, for live rendering.</summary>
    public PresentationContext LiveContext => Context with { Profile = ProfileWorking };

    public ResolvedSelection Resolve(string slot) => _runtime.Resolve(slot, ContextFor(slot), Working);

    public CompiledDefinition ResolveCompiled(string slot) => _runtime.ResolveCompiled(slot, ContextFor(slot), Working);

    /// <summary>The live context for one slot; surface-owned slots are resolved against their fixed surface.</summary>
    public PresentationContext ContextFor(string slot) =>
        PresentationSlots.Get(slot).Surface is { } surface ? LiveContext with { Surface = surface } : LiveContext;

    public ProfileMediaPresentation? ResolveMedia() =>
        ProfileWorking is null ? null : _runtime.ResolveMedia(ProfileWorking);

    public string ScopeIdFor(ScopeKind scope, string slot) => scope switch
    {
        ScopeKind.Global => ScopeIds.Global,
        ScopeKind.Surface => PresentationSlots.Get(slot).Surface ?? Context.Surface ?? throw new InvalidOperationException("This slot has no surface."),
        ScopeKind.Profile => ScopeIds.Of(Context.ProfileId ?? throw new InvalidOperationException("This preview has no Profile.")),
        _ => ScopeIds.Of(Context.ItemId ?? throw new InvalidOperationException("This preview has no item.")),
    };

    /// <summary>Selects a definition (and optional state) for a slot at a scope.</summary>
    public void Select(string slot, ScopeKind scope, DefinitionRef? definition, string? stateJson = null)
    {
        EnsureOpen();
        var descriptor = PresentationSlots.Get(slot);
        if (!descriptor.AllowsScope(scope)) throw new ArgumentException("The selection scope is unsupported.");
        if (definition is { } selected && (_runtime.Find(selected) is not { } compiled || compiled.Kind != descriptor.Kind
            || !PresentationRuntime.SupportsSlot(compiled, slot)))
            throw new ArgumentException("The definition does not support this presentation slot.");
        if (scope == ScopeKind.Profile && PresentationRuntime.IsBridgedProfileSlot(slot) && ProfileWorking is { } profile && definition is { } reference
            && PresentationRuntime.LegacyValueFor(slot, reference) is { } legacy)
        {
            // Built-in choices keep living on the Profile's existing durable appearance record.
            ProfileWorking = slot switch
            {
                PresentationSlots.ProfileLayout => profile with { LayoutPresetId = legacy },
                PresentationSlots.ProfileCard => profile with { Overrides = profile.Overrides with { ProfileCardVariantId = legacy } },
                PresentationSlots.ProfileMediaLayout => profile with { Overrides = profile.Overrides with { MediaLayoutId = MediaLayoutCatalog.Normalize(legacy) } },
                _ => profile with { Overrides = FrameAppearance(profile.Overrides, CoverFrameCatalog.TryGetFrame(legacy, out var legacyFrame) ? legacyFrame : CoverFrameCatalog.None) },
            };
            Working = Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot));
        }
        else
        {
            Working = Working.Apply(new BindingChange(scope, ScopeIdFor(scope, slot), slot, definition, stateJson));
            if (scope == ScopeKind.Profile && slot == PresentationSlots.ProfileFrame && ProfileWorking is { } profileState && definition is not null)
            {
                var plan = ResolveCompiled(slot).PlanAs<FramePlan>();
                ProfileWorking = profileState with { Overrides = FrameAppearance(profileState.Overrides, plan.Frame, plan.OverlayAssetPath is not null) };
            }
        }

        Changed?.Invoke(this, slot);
    }

    private static ProfileAppearanceOverrides FrameAppearance(ProfileAppearanceOverrides current, CoverFrameDefinition frame, bool hasOverlay = false)
    {
        var animation = frame.SupportsAnimation && !hasOverlay
            ? (frame.Family == CoverFrameFamily.Holographic
                ? CoverFrameAnimation.Shimmer
                : CoverFrameAnimation.Glow)
            : CoverFrameAnimation.None;
        return current with
        {
            CoverFrameId = CoverFrameCatalog.TryGetFrame(frame.Id, out _) ? frame.Id : null,
            CoverShape = CoverFrameCatalog.ClosestSupportedShape(frame,
                Enum.TryParse<CoverShape>(current.CoverShape, true, out var shape) ? shape : CoverFrameCatalog.FallbackShape).ToString(),
            CoverFrameScale = null,
            CoverFrameIntensity = null,
            CoverFrameTint = null,
            CoverFrameAnimation = animation == CoverFrameAnimation.None ? null : animation.ToString(),
            CoverShadow = frame.Family != CoverFrameFamily.None,
        };
    }

    /// <summary>Stores a state-only value such as Gallery density.</summary>
    public void SetState(string slot, ScopeKind scope, string? stateJson)
    {
        EnsureOpen();
        Working = stateJson is null
            ? Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot))
            : Working.Apply(new BindingChange(scope, ScopeIdFor(scope, slot), slot, null, stateJson));
        Changed?.Invoke(this, slot);
    }

    /// <summary>Restores this slot to its built-in value or its committed Profile media state.</summary>
    public void ResetToBuiltIn(string slot, ScopeKind scope)
    {
        EnsureOpen();
        Working = Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot));
        if (scope == ScopeKind.Profile && slot == PresentationSlots.ProfileLayout && ProfileWorking is { } layoutProfile)
        {
            ProfileWorking = layoutProfile with
            {
                Overrides = layoutProfile.Overrides with
                {
                    ShowRecentMedia = ProfileAppearanceOverrides.Default.ShowRecentMedia,
                },
            };
        }
        if (scope == ScopeKind.Profile && PresentationRuntime.IsBridgedProfileSlot(slot))
        {
            Select(slot, scope, PresentationSlots.Get(slot).Default);
            return;
        }
        if (scope == ScopeKind.Profile && ProfileWorking is { } profile)
        {
            ProfileWorking = slot switch
            {
                // Cover/Banner are Profile-owned with no parent scope: resetting discards this session's edits,
                // restoring the committed source together with its framing (and, for the Banner, playback).
                PresentationSlots.ProfileCover when OriginalProfile is { } original => RestoreCover(profile, original),
                PresentationSlots.ProfileBanner when OriginalProfile is { } original => RestoreBanner(profile, original),
                _ => profile,
            };
        }

        Changed?.Invoke(this, slot);
    }

    /// <summary>
    /// Previews a new Cover source (image, or a video frame at <paramref name="frameTimestampMilliseconds"/>).
    /// Nothing is written until Apply, where the source commits in the same transaction as its framing.
    /// </summary>
    public void SelectCoverSource(Guid? assetId, bool isVideoFrame, long? frameTimestampMilliseconds, double? cropX = null, double? cropY = null)
    {
        EnsureOpen();
        var profile = ProfileWorking ?? throw new InvalidOperationException("This preview is not bound to a Profile.");
        var sources = profile.Sources ?? new ProfileMediaSources(null, null);
        ProfileWorking = profile with
        {
            Sources = sources with { CoverMediaId = assetId },
            Overrides = profile.Overrides with
            {
                CoverSourceKind = assetId is null ? null : (isVideoFrame ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image).ToString(),
                CoverVideoTimestampMilliseconds = assetId is not null && isVideoFrame ? frameTimestampMilliseconds : null,
                CropX = cropX ?? profile.Overrides.CropX,
                CropY = cropY ?? profile.Overrides.CropY,
            },
        };
        Changed?.Invoke(this, PresentationSlots.ProfileCover);
    }

    /// <summary>
    /// Previews a new Banner selection. The selected VIDEO's prepared HOVER asset is the bytes
    /// authority; focus and zoom remain in the Profile appearance draft.
    /// </summary>
    public void SelectBannerSource(Guid? mediaId, double? focusX = null, double? focusY = null)
    {
        EnsureOpen();
        var profile = ProfileWorking ?? throw new InvalidOperationException("This preview is not bound to a Profile.");
        var sources = profile.Sources ?? new ProfileMediaSources(null, null);
        ProfileWorking = profile with
        {
            Sources = sources with { BannerMediaId = mediaId },
            Overrides = profile.Overrides with
            {
                BannerFocusX = focusX ?? profile.Overrides.BannerFocusX,
                BannerFocusY = focusY ?? profile.Overrides.BannerFocusY,
            },
        };
        Changed?.Invoke(this, PresentationSlots.ProfileBanner);
    }

    public void SelectFigureSource(Guid? mediaId)
    {
        EnsureOpen();
        if (mediaId == Guid.Empty) throw new ArgumentException("Figure media identifier cannot be empty.",nameof(mediaId));
        var profile = ProfileWorking ?? throw new InvalidOperationException("This preview is not bound to a Profile.");
        ProfileWorking = profile with {Sources = (profile.Sources ?? new ProfileMediaSources(null,null)) with {FigureMediaId = mediaId}};
        Changed?.Invoke(this,nameof(ProfileMediaSources.FigureMediaId));
    }

    public void ResetFigureSource() => SelectFigureSource(OriginalProfile?.Sources?.FigureMediaId);
    private static ProfilePresentationState RestoreCover(ProfilePresentationState working, ProfilePresentationState original)
    {
        var o = original.Overrides;
        return working with
        {
            Sources = (working.Sources ?? original.Sources) is { } sources ? sources with { CoverMediaId = original.Sources?.CoverMediaId } : null,
            Overrides = working.Overrides with
            {
                CoverSourceKind = o.CoverSourceKind,
                CoverVideoTimestampMilliseconds = o.CoverVideoTimestampMilliseconds,
                CoverFit = o.CoverFit,
                CropX = o.CropX,
                CropY = o.CropY,
                Zoom = o.Zoom,
                CoverOffsetX = o.CoverOffsetX,
                CoverOffsetY = o.CoverOffsetY,
                CoverRotation = o.CoverRotation,
            },
        };
    }

    private static ProfilePresentationState RestoreBanner(ProfilePresentationState working, ProfilePresentationState original)
    {
        var o = original.Overrides;
        return working with
        {
            Sources = (working.Sources ?? original.Sources) is { } sources
                ? sources with { BannerMediaId = original.Sources?.BannerMediaId }
                : null,
            Overrides = working.Overrides with
            {
                BannerFocusX = o.BannerFocusX,
                BannerFocusY = o.BannerFocusY,
                BannerZoom = o.BannerZoom,
            },
        };
    }

    public void EditCover(Func<ProfileAppearanceOverrides, ProfileAppearanceOverrides> edit) =>
        EditProfile(PresentationSlots.ProfileCover, edit);

    public void EditBanner(Func<ProfileAppearanceOverrides, ProfileAppearanceOverrides> edit) =>
        EditProfile(PresentationSlots.ProfileBanner, edit);

    public void EditProfileLayout(Func<ProfileAppearanceOverrides, ProfileAppearanceOverrides> edit) =>
        EditProfile(PresentationSlots.ProfileLayout, edit);

    private void EditProfile(
        string changedSlot,
        Func<ProfileAppearanceOverrides, ProfileAppearanceOverrides> edit)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(edit);
        if (ProfileWorking is null)
        {
            throw new InvalidOperationException("This preview is not bound to a Profile.");
        }

        ProfileWorking = ProfileWorking with { Overrides = edit(ProfileWorking.Overrides) };
        Changed?.Invoke(this, changedSlot);
    }

    /// <summary>Re-targets an open preview after the Profile's durable row changed underneath it.</summary>
    public void RebaseProfile(ProfilePresentationState committed)
    {
        var hadLocalChanges = ProfileChanged;
        OriginalProfile = committed;
        if (!hadLocalChanges)
        {
            ProfileWorking = committed;
        }
        else if (ProfileWorking is not null)
        {
            ProfileWorking = ProfileWorking with { RowVersion = committed.RowVersion };
        }

        Context = Context with { Profile = committed };
    }
    public async Task<PresentationApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var result = await _runtime.CommitAsync(this, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            IsClosed = true;
        }

        return result;
    }

    public void Cancel() => IsClosed = true;

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("This preview has already been applied or cancelled.");
        }
    }
}
