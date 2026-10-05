using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Gallery;
using Neuterradise.App.Home;
using Neuterradise.App.Media;
using Neuterradise.App.Localization;
using Neuterradise.App.Presentation;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.Ui.Figure;

namespace Neuterradise.App.Ui;

/// <summary>
/// The Customization Center edits Home, Gallery, Profile, Media and Library presentation.
/// Global appearance is edited in Settings; reusable assets are managed in Settings -> Presentation.
/// Changes stay in an in-memory <see cref="PreviewSession"/> until Save changes; closing with staged changes asks before discarding them.
/// Built-in and pack definitions remain first-class choices, while Cover and Banner source edits
/// remain independent Profile mutations committed through the same presentation session.
/// </summary>
public sealed partial class CustomizationCenter : IDisposable
{
    private const int SubjectMediaLimit = 500;
    private static readonly IReadOnlySet<string> CuratedBuiltInEffects = new HashSet<string>(StringComparer.Ordinal)
    {
        "builtin.neuterradise.effect.none",
        "builtin.neuterradise.effect.silk",
        "builtin.neuterradise.effect.rain",
        "builtin.neuterradise.effect.snow",
        "builtin.neuterradise.effect.bubbles",
    };

    private readonly AppServices _services;
    private readonly PreviewSession _session;
    private ProfileCustomizationOverlayRequest? _launchRequest;
    private readonly Action _onClosed;
    private readonly Func<Task> _onApplied;
    private readonly Grid _root = new();
    private readonly VariableWrap _slotTabs = UI.Wrap(4);
    private readonly Grid _preview = new() { MinHeight = 180, Height = 240 };
    private readonly StackPanel _scopeBar = UI.V(6);
    private readonly StackPanel _editor = UI.V(10);
    private readonly TextBlock _status = UI.WrappedText(string.Empty, "caption");
    private readonly Button _apply;
    private string _category;
    private string? _slot;
    private string _group = string.Empty;
    private ScopeKind _scope;
    private bool _dedicatedEditor;
    private readonly bool _cardOnly;
    private readonly StackPanel _inspector = UI.V(12);
    private readonly CustomizationWorkspaceView _workspace;
    private bool _closed;
    private bool _closePromptOpen;
    private bool _saving;
    private ImageRef? _coverSource;
    private ImageRef? _bannerSource;
    private ImageRef? _committedCoverSource;
    private ImageRef? _committedBannerSource;
    private string? _bannerVideoPath;
    private string? _committedBannerVideoPath;
    private readonly ProfileHeaderView _profilePreviewHeader = new();
    private CustomizationSubjectSnapshot? _subject;
    private readonly TextBlock _subjectText = UI.Text(string.Empty, "caption", maxLines: 1);
    private int _subjectGeneration;
    private bool _framingEdit;
    private bool _dragging;
    private Windows.Foundation.Point _dragPoint;
    private CancellationTokenSource? _subjectLoadCts;

    public CustomizationCenter(AppServices services, string category, PresentationContext context, string? slot, object? subject, Action onClosed, Func<Task>? onApplied = null)
    {
        _services = services;
        _profilePreviewFigure = new ProfileFigureView(services);
        _profilePreviewHeader.CoverFramingSurfaceChanged += OnCoverFramingSurfaceChanged;
        _onClosed = onClosed;
        _onApplied = onApplied ?? (() => Task.CompletedTask);
        _launchRequest = subject as ProfileCustomizationOverlayRequest;
        _session = services.Presentation.BeginPreview(context);
        _figureEnabledDraft = _session.ProfileWorking?.Sources?.FigureMediaId is not null;
        _rememberedFigureId = _session.ProfileWorking?.Sources?.FigureMediaId;
        _session.Changed += OnSessionChanged;
        _category = CustomizationCategories.Ordered.Contains(category)
            ? category : throw new ArgumentException("Unknown customization context.", nameof(category));
        _slot = slot;
        _cardOnly = slot is PresentationSlots.ProfileCard or PresentationSlots.CardEffect;
        if (slot is not null && PresentationSlots.TryGet(slot, out var descriptor))
        {
            if (descriptor.Category == CustomizationCategories.Appearance)
            {
                _slot = null;
            }
            else
            {
                if (descriptor.Category != _category) throw new ArgumentException("Slot is outside this customization context.", nameof(slot));
            }
        }

        _apply = UI.Button(UI.T("Customize.SaveChanges", "Save changes"), () => Run(ApplyAsync(), "CustomizationCenter.ApplyAsync", UI.T("Customize.SaveFailed", "Could not save. Nothing was changed.")), ButtonKind.Primary);
        _workspace = new CustomizationWorkspaceView(_subjectText, _slotTabs, _preview, _scopeBar, _editor,
            _status, _apply, RequestClose, RequestCancel, ReturnToInspector, _inspector, _cardOnly);
        _root.Children.Add(_workspace.View);
        _root.SizeChanged += (_, args) => UpdatePreviewHeight(args.NewSize.Height);

        _subjectText.Text = SubjectLine();
        ShowCategory(_category, _slot);
        if (context.ProfileId is { } profileId)
        {
            Run(LoadSubjectAsync(profileId, ++_subjectGeneration), "CustomizationCenter.LoadSubjectAsync", UI.T("Customize.SubjectLoadFailed", "This Profile could not be loaded for customization."));
        }
    }

    public FrameworkElement View => _root;
    public PreviewSession Session => _session;
    public event Action<bool>? EditorChanged;
    public event Action<ImageRef?, string?>? PreviewMediaChanged;
    public bool IsEditor => _dedicatedEditor;
    public void SetFigureOccluded(bool occluded) => _profilePreviewFigure.SetOccluded(occluded);
    public bool IsCardOnly => _cardOnly;
    public bool ContainsPointer(Windows.Foundation.Point point, UIElement relativeTo) => _workspace.ContainsPointer(point, relativeTo);
    public void SetInspectorWidth(double width) => _workspace.SetInspectorWidth(width);
    public void PublishPreviewMedia()
    {
        if (_subject is not null) PreviewMediaChanged?.Invoke(_coverSource, _bannerVideoPath);
    }

    private static bool IsDedicatedSlot(string? slot) => slot is PresentationSlots.ProfileCover
        or PresentationSlots.ProfileBanner or PresentationSlots.ProfileFrame or PresentationSlots.ProfileCard
        or PresentationSlots.CardEffect or PresentationSlots.ProfileMediaLayout;

    private void ReturnToInspector()
    {
        if (_figureEditorOpen)
        {
            ReturnFromFigureEditor();
            return;
        }
        if (_cardOnly) RequestClose();
        else SelectSlot(PresentationSlots.ProfileLayout);
    }

    private PresentationContext Context => _session.LiveContext;

    private string SubjectLine() =>
        _category == CustomizationCategories.Home ? UI.T("Customize.HomeScope", "Customizing Home")
        : _category == CustomizationCategories.Gallery ? UI.T("Customize.GalleryScope", "Customizing Gallery")
        : _subject is not null ? UI.F("Customize.ForProfile", "Editing {0}", _subject.Detail.DisplayName)
        : _launchRequest is not null ? UI.F("Customize.ForProfile", "Editing {0}", _launchRequest.ProfileDisplayName)
        : _session.Context.ProfileId is not null ? UI.T("Customize.ForThisProfile", "Editing this Profile")
        : UI.T("Customize.ForThisProfile", "Editing this Profile");
    private void Run(Task task, string context, string failureMessage) => _services.RunUserAction(task, context, failureMessage);

    // ---------------------------------------------------------------- navigation

    private sealed record CustomizationGroup(
        string Id,
        string LabelKey,
        string LabelFallback,
        IReadOnlyList<string> Slots);

    private IReadOnlyList<CustomizationGroup> GroupsOf(string category)
    {
        if (category == CustomizationCategories.Profile)
        {
            if (_cardOnly) return [new("card", "Customize.Group.Card", "Card", [PresentationSlots.ProfileCard, PresentationSlots.CardEffect])];
            return
            [
                new("layout", "Customize.Group.Layout", "Layout", [PresentationSlots.ProfileLayout]),
                new("backdrop", "Customize.Profile.Backdrop", "Backdrop", [PresentationSlots.ProfileBackdrop]),
                new("effects", "Customize.ProfileEffect", "Profile Effect", [PresentationSlots.ProfileEffect]),
                new("cover", "Customize.Profile.Cover", "Cover", [PresentationSlots.ProfileCover]),
                new("banner", "Customize.Profile.Banner", "Banner", [PresentationSlots.ProfileBanner]),
                new("frame", "Customize.Profile.Frame", "Frame", [PresentationSlots.ProfileFrame]),
                new("media", "Customize.Group.Media", "Media", [PresentationSlots.ProfileMediaLayout]),
            ];
        }

        if (category == CustomizationCategories.Home)
        {
            return
            [
                new("layout", "Customize.Group.Layout", "Layout", [PresentationSlots.HomeLayout]),
                new("spotlight", "Customize.Home.Spotlight", "Spotlight", [PresentationSlots.HomeSpotlight]),
                new("backdrop", "Customize.Home.Backdrop", "Backdrop", [PresentationSlots.HomeBackdrop]),
                new("effects", "Customize.Group.Effects", "Effects", [PresentationSlots.HomeEffect]),
            ];
        }

        if (category == CustomizationCategories.Gallery)
        {
            return
            [
                new("layout", "Customize.Group.Layout", "Layout", [PresentationSlots.GalleryLayout]),
            ];
        }

        return PresentationSlots.All
            .Where(descriptor => descriptor.Category == category)
            .Select(descriptor => new CustomizationGroup(
                descriptor.Id,
                descriptor.LabelKey,
                descriptor.LabelFallback,
                [descriptor.Id]))
            .ToList();
    }

    private static string SlotLabel(SlotDescriptor descriptor) => descriptor.Id switch
    {
        PresentationSlots.ProfileCard => UI.T("Customize.Slot.ProfileCard", "Card Layout"),
        PresentationSlots.ProfileLayout => UI.T("Customize.Slot.ProfileLayout", "Profile Layout"),
        PresentationSlots.ProfileMediaLayout => UI.T("Customize.Slot.MediaLayout", "Media Layout"),
        PresentationSlots.ProfileCover => UI.T("Customize.Profile.Cover", "Cover"),
        PresentationSlots.ProfileBanner => UI.T("Customize.Profile.Banner", "Banner"),
        PresentationSlots.ProfileFrame => UI.T("Customize.Profile.Frame", "Frame"),
        PresentationSlots.ProfileBackdrop => UI.T("Customize.Profile.Backdrop", "Backdrop"),
        PresentationSlots.ProfileEffect => UI.T("Customize.Group.Effects", "Effects"),
        PresentationSlots.BackdropEffect => UI.T("Customize.Effect.Target.Backdrop", "Backdrop"),
        PresentationSlots.BannerEffect => UI.T("Customize.Effect.Target.Banner", "Banner"),
        PresentationSlots.CoverEffect => UI.T("Customize.Effect.Target.Cover", "Cover"),
        PresentationSlots.FrameEffect => UI.T("Customize.Effect.Target.Frame", "Frame"),
        PresentationSlots.CardEffect => UI.T("Customize.CardEffect", "Card Effect"),
        PresentationSlots.MediaEffect => UI.T("Customize.Effect.Target.Media", "Media"),
        _ => UI.T(descriptor.LabelKey, descriptor.LabelFallback),
    };

    private ScopeKind DefaultScope(SlotDescriptor slot) => slot.Scopes.Single();

    private void ShowCategory(string category, string? slot)
    {
        if (category != _category)
        {
            throw new InvalidOperationException("Customization context is fixed.");
        }

        _subjectText.Text = SubjectLine();
        _scopeBar.Children.Clear();
        _editor.Children.Clear();
        _slotTabs.Visibility = Visibility.Visible;
        _preview.Visibility = Visibility.Visible;

        var groups = GroupsOf(category);
        if (groups.Count == 0)
        {
            _slot = null;
            _group = string.Empty;
            RenderGroupTabs();
            RenderPreview();
            _editor.Children.Add(UI.Text(
                category == CustomizationCategories.Profile
                    ? UI.T("Customize.OpenFromProfile", "Open a Profile and choose Customize Profile to edit its Cover, Banner and frame. Global Profile defaults are below once a Profile is open.")
                    : UI.T("Customize.NothingHere", "Nothing to customize here yet."),
                "body-muted"));
            return;
        }

        var availableSlots = groups.SelectMany(group => group.Slots).ToHashSet(StringComparer.Ordinal);
        var initialSlot = slot is not null && availableSlots.Contains(slot)
            ? slot
            : groups[0].Slots[0];
        SelectSlot(initialSlot);
    }

    private void RenderGroupTabs()
    {
        _slotTabs.Children.Clear();
        if (_figureEditorOpen)
        {
            _slotTabs.Visibility = Visibility.Collapsed;
            return;
        }
        _slotTabs.Visibility = Visibility.Visible;
        foreach (var group in GroupsOf(_category))
        {
            if (_dedicatedEditor && group.Id != _group) continue;
            var id = group.Id;
            var label = UI.T(group.LabelKey, group.LabelFallback);
            _slotTabs.Children.Add(IsDedicatedSlot(group.Slots[0]) && !_dedicatedEditor
                ? UI.Button(label + " ›", () => SelectGroup(id), ButtonKind.Secondary)
                : UI.Tab(label, _group == id, () => SelectGroup(id)));
        }
    }

    private void SelectGroup(string groupId)
    {
        var group = GroupsOf(_category).FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || group.Slots.Count == 0)
        {
            return;
        }

        var slot = _slot is not null && group.Slots.Contains(_slot, StringComparer.Ordinal)
            ? _slot
            : group.Slots[0];
        SelectSlot(slot);
    }

    private void SelectSlot(string slot)
    {
        if (_saving) return;
        _figureEditorOpen = false;
        var descriptor = PresentationSlots.Get(slot);
        var group = GroupsOf(_category).FirstOrDefault(candidate => candidate.Slots.Contains(slot, StringComparer.Ordinal))
            ?? throw new InvalidOperationException($"Slot '{slot}' is not exposed in this customization context.");
        _slot = slot;
        _group = group.Id;
        _scope = DefaultScope(descriptor);
        _dedicatedEditor = IsDedicatedSlot(slot);
        _workspace.SetEditor(_dedicatedEditor);
        EditorChanged?.Invoke(_dedicatedEditor);
        RenderGroupTabs();
        RenderAll();
    }

    private void OnSessionChanged(object? sender, string slot)
    {
        if (_framingEdit && _session.ProfileWorking is { } framing)
        {
            _profilePreviewHeader.UpdateFraming(framing.Overrides);
            UpdateFramingFeedback();
            UpdateStatus();
            return;
        }
        if (slot is PresentationSlots.ProfileCover or PresentationSlots.ProfileBanner)
        {
            SyncSourcePreviews();
            PublishPreviewMedia();
        }
        if (slot == nameof(ProfileMediaSources.FigureMediaId))
        {
            _figureEnabledDraft = _session.ProfileWorking?.Sources?.FigureMediaId is not null;
            _rememberedFigureId = _session.ProfileWorking?.Sources?.FigureMediaId ?? _rememberedFigureId;
            UpdateFigurePreviewSource();
        }
        UpdateStatus();
        RenderPreview();
        RenderScopeBar();
        if (!_framingEdit && _dedicatedEditor) RenderEditor();
        RenderInspector();
    }

    /// <summary>After reset the committed source preview returns.</summary>
    private void SyncSourcePreviews()
    {
        if (_session.ProfileWorking is not { Sources: { } working } profile || _session.OriginalProfile is not { Sources: { } original } committed)
        {
            return;
        }

        if (working.CoverMediaId == original.CoverMediaId
            && profile.Overrides.CoverVideoTimestampMilliseconds == committed.Overrides.CoverVideoTimestampMilliseconds)
        {
            _coverSource = _committedCoverSource;
        }

        if (working.BannerMediaId == original.BannerMediaId)
        {
            _bannerSource = _committedBannerSource;
            _bannerVideoPath = _committedBannerVideoPath;
        }

        SyncBannerPreviewFromWorkingSource();
    }

    private void SyncBannerPreviewFromWorkingSource()
    {
        if (_session.ProfileWorking?.Sources?.BannerMediaId is not { } mediaId)
        {
            return;
        }

        var candidate = _subject?.BannerCandidates.FirstOrDefault(item => item.MediaId == mediaId);
        if (candidate is null)
        {
            return;
        }

        _bannerSource = ImageRef.FromPath(candidate.PreviewPath, 1600) ?? _bannerSource;
        if (!string.IsNullOrWhiteSpace(candidate.PlaybackPath))
        {
            _bannerVideoPath = candidate.PlaybackPath;
        }
    }
    private void RenderAll()
    {
        PublishPreviewMedia();
        RenderPreview();
        RenderScopeBar();
        RenderEditor();
        RenderInspector();
        UpdateStatus();
    }

    private void RenderInspector()
    {
        _inspector.Children.Clear();
        if (_cardOnly) return;
        if (_session.Context.ProfileId is not null && _subject is null)
        {
            _inspector.Children.Add(UI.Text(UI.T("Customize.Loading.ProfileCard", "Loading this Profile's card..."), "body-muted"));
            return;
        }

        _inspector.Children.Add(UI.Guidance(
            UI.T("Customize.PreviewNote.Title", "Preview mode"),
            UI.T("Customize.Previewing", "Previewing changes. Nothing is saved until Save changes."),
            "warning"));

        var groups = GroupsOf(_category);
        var actions = groups.Where(group => IsDedicatedSlot(group.Slots[0])).ToList();
        if (_category == CustomizationCategories.Profile && actions.Count > 0)
        {
            _inspector.Children.Add(UI.Text(UI.T("Customize.Group.Media", "Media"), "control"));
            var mediaGrid = UI.Grid("auto,auto", "*,*");
            mediaGrid.RowSpacing = 6;
            mediaGrid.ColumnSpacing = 6;
            for (var index = 0; index < actions.Count; index++)
            {
                var group = actions[index];
                var slot = group.Slots[0];
                var icon = slot switch
                {
                    PresentationSlots.ProfileCover => "icon.media.image",
                    PresentationSlots.ProfileBanner => "icon.media.video",
                    PresentationSlots.ProfileFrame => "icon.navigation.customize",
                    _ => "icon.navigation.gallery",
                };
                var button = UI.Button(UI.T(group.LabelKey, group.LabelFallback),
                    () => SelectSlot(slot), ButtonKind.Secondary, icon);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                mediaGrid.Children.Add(button.At(index / 2, index % 2));
            }
            _inspector.Children.Add(mediaGrid);
        }

        if (_category == CustomizationCategories.Profile)
        {
            _inspector.Children.Add(FigureInspectorControl());
        }

        foreach (var group in groups)
        {
            var descriptor = PresentationSlots.Get(group.Slots[0]);
            if (IsDedicatedSlot(descriptor.Id)) continue;
            _inspector.Children.Add(UI.Text(UI.T(group.LabelKey, group.LabelFallback), "control"));
            if (!descriptor.AllowsScope(ScopeKind.Profile) || _session.ProfileWorking is not null)
            {
                _inspector.Children.Add(DefinitionChooser(descriptor, compact: true));
                if (descriptor.Id == PresentationSlots.ProfileLayout)
                {
                    _inspector.Children.Add(ProfileLayoutVisibilityEditor());
                }
            }
        }

        if (_category != CustomizationCategories.Profile && actions.Count > 0)
        {
            foreach (var group in actions)
            {
                var slot = group.Slots[0];
                _inspector.Children.Add(UI.Button(UI.T(group.LabelKey, group.LabelFallback) + " >",
                    () => SelectSlot(slot), ButtonKind.Secondary));
            }
        }
    }

    private void UpdateStatus()
    {
        _status.Text = _session.HasChanges
            ? UI.T("Customize.Previewing", "Previewing changes. Nothing is saved until Save changes.")
            : UI.T("Customize.NoChanges", "No changes.");
        _apply.IsEnabled = _session.HasChanges && !_session.IsClosed && !_saving;
    }

    // ---------------------------------------------------------------- scope bar

    private void RenderScopeBar()
    {
        _scopeBar.Children.Clear();
        if (_figureEditorOpen)
        {
            _scopeBar.Children.Add(UI.Text(UI.T("Customize.Figure.Title", "3D Figure"), "control"));
            _scopeBar.Children.Add(UI.WrappedText(
                UI.T("Customize.Figure.PreviewOnly", "Figure changes stay in preview until you choose Save changes."),
                "caption",
                "textSecondary"));
            return;
        }
        if (_slot is null) return;
        var descriptor = PresentationSlots.Get(_slot);
        var group = GroupsOf(_category).FirstOrDefault(candidate => candidate.Id == _group);
        if (group is { Slots.Count: > 1 })
        {
            var subnav = UI.Wrap(6);
            foreach (var slot in group.Slots)
            {
                var target = slot;
                subnav.Children.Add(UI.Tab(
                    SlotLabel(PresentationSlots.Get(target)),
                    _slot == target,
                    () => SelectSlot(target)));
            }
            _scopeBar.Children.Add(subnav);
        }

        var reset = UI.Button(UI.T("Customize.ResetBuiltIn", "Reset to default"), () =>
        {
            _session.ResetToBuiltIn(_slot, _scope);
        }, ButtonKind.Ghost);
        reset.IsEnabled = _session.Context.ProfileId is null || _subject is not null;
        _scopeBar.Children.Add(UI.Text(SlotLabel(descriptor), "control"));
        if (HiddenSlotPreviewNotice() is { } notice)
        {
            _scopeBar.Children.Add(UI.Guidance(UI.T("Customize.PreviewNote.Title", "Preview note"), notice, "warning"));
        }
        _scopeBar.Children.Add(reset);
    }

    // ---------------------------------------------------------------- editors

    private void RenderEditor()
    {
        _editor.Children.Clear();
        if (_figureEditorOpen)
        {
            _editor.Children.Add(FigureEditor());
            return;
        }
        if (_slot is null)
        {
            return;
        }

        var descriptor = PresentationSlots.Get(_slot);
        if (_session.Context.ProfileId is not null && _subject is null)
        {
            _editor.Children.Add(UI.Text(UI.T("Customize.Loading.ProfileCard", "Loading this Profile's card…"), "body-muted"));
            return;
        }
        if (descriptor.AllowsScope(ScopeKind.Profile) && _session.ProfileWorking is null)
        {
            _editor.Children.Add(UI.Text(UI.T("Customize.ChooseProfileHint", "Choose a Profile above to edit this for that Profile."), "body-muted"));
            return;
        }

        switch (descriptor.Editor)
        {
            case EditorKind.DefinitionChooser:
                _editor.Children.Add(descriptor.Kind == DefinitionKinds.Effect
                    ? EffectChooser(descriptor)
                    : DefinitionChooser(descriptor));
                break;
            case EditorKind.CoverEditor:
            case EditorKind.BannerEditor:
                _editor.Children.Add(MediaEditor(descriptor));
                break;
        }
    }

    private FrameworkElement ProfileLayoutVisibilityEditor()
    {
        var toggle = new ToggleSwitch
        {
            Header = UI.T("Profile.RecentMedia", "Recent media"),
            IsOn = _session.ProfileWorking?.Overrides.ShowRecentMedia
                ?? ProfileAppearanceOverrides.Default.ShowRecentMedia,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        toggle.Toggled += (_, _) =>
        {
            if (_session.ProfileWorking is null)
            {
                return;
            }

            _session.EditProfileLayout(appearance => appearance with
            {
                ShowRecentMedia = toggle.IsOn,
            });
        };

        return UI.V(
            6,
            UI.WrappedText(
                UI.T(
                    "Customize.ProfileLayout.RecentMediaVisibility",
                    "Show Recent media on the Profile. This setting applies to every Profile Layout."),
                "caption",
                "textSecondary"),
            toggle);
    }

    // ---------------------------------------------------------------- packs

    // ---------------------------------------------------------------- save / cancel

    private async Task ApplyAsync()
    {
        if (_saving) return;
        if (!_session.IsClosed && !_session.HasChanges)
        {
            Close(apply: false);
            return;
        }

        _saving = true;
        _apply.IsEnabled = false;
        _editor.IsHitTestVisible = false;
        _slotTabs.IsHitTestVisible = false;
        _inspector.IsHitTestVisible = false;
        _scopeBar.IsHitTestVisible = false;
        _status.Text = UI.T("Customize.Saving", "Saving changes…");
        try
        {
            var result = _session.IsClosed
                ? new PresentationApplyResult(true, null)
                : await _session.ApplyAsync().ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _status.Text = result.Message ?? UI.T("Customize.SaveFailed", "Could not save. Nothing was changed.");
                _apply.IsEnabled = !_session.IsClosed && _session.HasChanges;
                return;
            }

            await _onApplied().ConfigureAwait(true);
            _services.Toast(UI.T("Customize.Saved", "Customization saved"), "success");
            Finish();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            _status.Text = _session.IsClosed
                ? UI.T("Customize.Saved", "Customization saved") + ". " + UI.T("Profile.LoadFailed", "This Profile could not be opened.")
                : UI.F("Customize.SaveFailedDetail", "Could not save these changes. Try again.", OperationExecution.SafeMessage(exception));
            _apply.IsEnabled = _session.HasChanges;
        }
        finally
        {
            _saving = false;
            _editor.IsHitTestVisible = !_session.IsClosed;
            _slotTabs.IsHitTestVisible = !_session.IsClosed;
            _inspector.IsHitTestVisible = !_session.IsClosed;
            _scopeBar.IsHitTestVisible = !_session.IsClosed;
        }
    }

    public void RequestClose()
    {
        if (_saving) return;
        if (_dedicatedEditor && !_cardOnly)
        {
            ReturnToInspector();
            return;
        }
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_saving) return;
        Run(
            RequestCloseAsync(),
            "CustomizationCenter.RequestCloseAsync",
            UI.T("Customize.CloseFailed", "Could not close Customize."));
    }

    private async Task RequestCloseAsync()
    {
        if (_closed || _closePromptOpen)
        {
            return;
        }

        if (!_session.IsClosed && _session.HasChanges)
        {
            _closePromptOpen = true;
            try
            {
                var discard = await _services.ConfirmAsync(
                    UI.T("Customize.DiscardTitle", "Discard changes?"),
                    UI.T("Customize.DiscardBody", "Your preview changes haven't been applied."),
                    UI.T("Customize.Discard", "Discard"),
                    destructive: true,
                    cancel: UI.T("Customize.KeepEditing", "Keep editing")).ConfigureAwait(true);
                if (!discard)
                {
                    return;
                }
            }
            finally
            {
                _closePromptOpen = false;
            }
        }

        Close(apply: false);
    }

    /// <summary>Programmatic close used by shell lifecycle. User-triggered close should call <see cref="RequestClose"/>.</summary>
    public void Close(bool apply)
    {
        if (_closed)
        {
            return;
        }

        if (apply)
        {
            Run(ApplyAsync(), "CustomizationCenter.ApplyAsync", UI.T("Customize.SaveFailed", "Could not save. Nothing was changed."));
            return;
        }

        if (!_session.IsClosed)
        {
            _session.Cancel();
        }

        Finish();
    }

    private void Finish()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CancelSubjectLoad();
        _subject = null;
        _onClosed();
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _profilePreviewHeader.CoverFramingSurfaceChanged -= OnCoverFramingSurfaceChanged;
        if (!_closed)
        {
            Close(apply: false);
        }

        _profilePreviewFigure.Dispose();
        _profilePreviewHeader.Dispose();
        CancelSubjectLoad();
        _subject = null;
    }
}
