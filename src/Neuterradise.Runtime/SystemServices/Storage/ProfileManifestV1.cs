using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record ProfileManifestV1
{
    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; }

    [JsonPropertyOrder(2)]
    public Guid ProfileId { get; init; }

    [JsonPropertyOrder(3)]
    [JsonConverter(typeof(ProfileKindUpperStringConverter))]
    public ProfileKind Kind { get; init; }

    [JsonPropertyOrder(4)]
    public string DisplayName { get; init; }

    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UnknownSequence { get; init; }

    [JsonPropertyOrder(6)]
    public string FolderName { get; init; }

    [JsonPropertyOrder(7)]
    public Guid? IdentityId { get; init; }

    [JsonPropertyOrder(8)]
    public Guid? CoverMediaAssetId { get; init; }

    [JsonPropertyOrder(9)]
    public Guid? BannerMediaAssetId { get; init; }

    [JsonPropertyOrder(13)]
    public Guid? FigureMediaId { get; init; }

    [JsonPropertyOrder(10)]
    public string? LayoutPresetId { get; init; }

    [JsonPropertyOrder(11)]
    public string? AppearanceOverridesJson { get; init; }

    [JsonPropertyOrder(12)]
    [JsonConverter(typeof(IsoDateTimeOffsetUtcConverter))]
    public DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonConstructor]
    public ProfileManifestV1(
        int schemaVersion,
        Guid profileId,
        ProfileKind kind,
        string displayName,
        string folderName,
        Guid? identityId,
        Guid? coverMediaAssetId,
        Guid? bannerMediaAssetId,
        string? layoutPresetId,
        string? appearanceOverridesJson,
        DateTimeOffset updatedAtUtc,
        int? unknownSequence = null, Guid? figureMediaId = null)
    {
        SchemaVersion = schemaVersion;
        ProfileId = profileId;
        Kind = kind;
        DisplayName = displayName;
        FolderName = folderName;
        IdentityId = identityId;
        CoverMediaAssetId = coverMediaAssetId;
        BannerMediaAssetId = bannerMediaAssetId;
        LayoutPresetId = layoutPresetId;
        AppearanceOverridesJson = appearanceOverridesJson;
        UpdatedAtUtc = updatedAtUtc;
        UnknownSequence = unknownSequence;
        FigureMediaId = figureMediaId;
    }
}

public sealed class ProfileKindUpperStringConverter : JsonConverter<ProfileKind>
{
    public override ProfileKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                try
                {
                    return DbEnum.ParseProfileKind(str.ToUpperInvariant());
                }
                catch (FormatException exception)
                {
                    throw new JsonException("Invalid ProfileKind value in manifest.", exception);
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return (ProfileKind)reader.GetInt32();
        }

        throw new JsonException($"Invalid ProfileKind value in manifest: {reader.GetString()}");
    }

    public override void Write(Utf8JsonWriter writer, ProfileKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(DbEnum.Format(value));
    }
}

public sealed class IsoDateTimeOffsetUtcConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TryGetDateTimeOffset(out var dto))
        {
            return dto.ToUniversalTime();
        }

        var str = reader.GetString();
        if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new JsonException($"Invalid DateTimeOffset format: {str}");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff+00:00", CultureInfo.InvariantCulture));
    }
}
