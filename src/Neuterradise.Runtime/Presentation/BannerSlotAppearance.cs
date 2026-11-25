using System.Text.Json;
using Neuterradise.App.Profiles;

namespace Neuterradise.App.Presentation;

/// <summary>Profile-owned customization of one authored secondary Banner slot.</summary>
public sealed record BannerSlotAppearance(Guid? MediaId = null, double FocusX = .5, double FocusY = .5,
    double Zoom = 1, double Rotation = 0, string? EffectPackId = null, string? EffectDefinitionId = null,
    double EffectIntensity = .2)
{
    public static BannerSlotAppearance Default { get; } = new();
    public MediaTransformState Transform => new("fill", FocusX, FocusY, Zoom, 0, 0, Rotation);
    public DefinitionRef? Effect => EffectPackId is not null && EffectDefinitionId is not null
        ? new(EffectPackId, EffectDefinitionId) : null;

    internal bool IsValid => MediaId != Guid.Empty
        && ProfileAppearanceRules.IsBannerFocusValid(FocusX) && ProfileAppearanceRules.IsBannerFocusValid(FocusY)
        && ProfileAppearanceRules.IsBannerZoomValid(Zoom) && double.IsFinite(Rotation) && Math.Abs(Rotation) <= 180
        && double.IsFinite(EffectIntensity) && EffectIntensity is >= 0 and <= 1
        && ((EffectPackId is null && EffectDefinitionId is null) ||
            (PackValidator.IsValidId(EffectPackId) && PackValidator.IsValidId(EffectDefinitionId)));
}

/// <summary>Canonical bounded JSON value inside appearance schema 1; no Catalog migration.</summary>
public static class BannerSlotState
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public const string PrimarySlotId = "primary";
    public static bool IsValidSlotId(string? id) => PackValidator.IsValidId(id) && id!.Length <= 64 && id != PrimarySlotId;

    private static bool ValidStoredSlot(string id, BannerSlotAppearance state) =>
        state.IsValid && (IsValidSlotId(id) || id == PrimarySlotId && state.MediaId is null
            && state.FocusX == .5 && state.FocusY == .5 && state.Zoom == 1 && state.Rotation == 0);

    public static IReadOnlyDictionary<string, BannerSlotAppearance> Read(string? json)
    {
        if (!TryRead(json, out var result)) return new Dictionary<string, BannerSlotAppearance>();
        return result;
    }

    internal static bool TryRead(string? json, out Dictionary<string, BannerSlotAppearance> result)
    {
        result = new(StringComparer.Ordinal);
        if (json is null) return true;
        if (json.Length > 32 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (result.Count >= 33 || property.Value.ValueKind != JsonValueKind.Object) return false;
                var state = property.Value.Deserialize<BannerSlotAppearance>(Options);
                if (state is null || !ValidStoredSlot(property.Name, state) || !result.TryAdd(property.Name, state)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static BannerSlotAppearance Get(string? json, string id) =>
        Read(json).GetValueOrDefault(id) ?? BannerSlotAppearance.Default;

    public static string? Write(string? json, string id, BannerSlotAppearance state)
    {
        if (!ValidStoredSlot(id, state)) throw new ArgumentException("Invalid Banner slot appearance.");
        var values = new SortedDictionary<string, BannerSlotAppearance>(StringComparer.Ordinal);
        foreach (var pair in Read(json)) values.Add(pair.Key, pair.Value);
        if (state == BannerSlotAppearance.Default) values.Remove(id);
        else values[id] = state;
        if (values.Keys.Count(key => key != PrimarySlotId) > 32) throw new ArgumentException("Too many saved Banner slots.");
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, Options);
    }
}
