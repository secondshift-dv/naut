using System.Text.Json;
using Neuterradise.App.Localization;

namespace Neuterradise.App.Activity;

public static class ActivityPresentation
{
    public static string AllCategory => SurfaceText.Get("Activity.Category.All", "All");

    public static IReadOnlyList<string> Categories { get; } =
        [
            AllCategory,
            SurfaceText.Get("Activity.Category.Profiles", "Profiles"),
            SurfaceText.Get("Activity.Category.Media", "Media"),
            SurfaceText.Get("Activity.Category.Import", "Import"),
            SurfaceText.Get("Activity.Category.Trash", "Trash"),
            SurfaceText.Get("Activity.Category.Faces", "Faces"),
            SurfaceText.Get("Activity.Category.Settings", "Settings")
        ];

    public static string CategorizeEvent(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        if (StartsWithAny(eventType, "PROFILE_", "Profile")
            || eventType.Equals("OWNER_CHANGED", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("PrimaryProfileChanged", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("UnknownResolved", StringComparison.OrdinalIgnoreCase)
            || eventType.StartsWith("ManualRelated", StringComparison.OrdinalIgnoreCase))
        {
            return SurfaceText.Get("Activity.Category.Profiles", "Profiles");
        }

        if (StartsWithAny(eventType, "ASSET_", "Media"))
        {
            return eventType.Contains("Trash", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("Purge", StringComparison.OrdinalIgnoreCase)
                ? SurfaceText.Get("Activity.Category.Trash", "Trash")
                : SurfaceText.Get("Activity.Category.Media", "Media");
        }

        if (eventType.Contains("TRASH", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("PURGE", StringComparison.OrdinalIgnoreCase))
        {
            return SurfaceText.Get("Activity.Category.Trash", "Trash");
        }

        if (StartsWithAny(eventType, "IMPORT_", "Import"))
        {
            return SurfaceText.Get("Activity.Category.Import", "Import");
        }

        if (StartsWithAny(eventType, "FACE_", "Face"))
        {
            return SurfaceText.Get("Activity.Category.Faces", "Faces");
        }

        if (eventType.Contains("Theme", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Setting", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("LibraryRepair", StringComparison.OrdinalIgnoreCase))
        {
            return SurfaceText.Get("Activity.Category.Settings", "Settings");
        }

        return SurfaceText.Get("Activity.Category.Profiles", "Profiles");
    }

    public static string DescribeActivity(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return eventType switch
        {
            "ProfileCreated" or "PROFILE_CREATED" => SurfaceText.Get("Activity.ProfileCreated", "Profile created"),
            "ProfileRenamed" or "PROFILE_RENAMED" => SurfaceText.Get("Activity.ProfileRenamed", "Profile renamed"),
            "ProfileMetadataChanged" => SurfaceText.Get("Activity.ProfileMetadataChanged", "Profile details changed"),
            "ProfileAppearanceChanged" => SurfaceText.Get("Activity.ProfileAppearanceChanged", "Profile appearance changed"),
            "ProfileTrashed" or "PROFILE_TRASHED" => SurfaceText.Get("Activity.ProfileTrashed", "Profile moved to Trash"),
            "ProfileRestored" or "PROFILE_RESTORED" => SurfaceText.Get("Activity.ProfileRestored", "Profile restored"),
            "ProfilePurged" => SurfaceText.Get("Activity.ProfilePurged", "Profile permanently deleted"),
            "MediaAssociationAdded" => SurfaceText.Get("Activity.MediaAssociationAdded", "Media linked to Profile"),
            "MediaAssociationRemoved" => SurfaceText.Get("Activity.MediaAssociationRemoved", "Media unlinked from Profile"),
            "MediaMovedToTrash" or "ASSET_TRASHED" => SurfaceText.Get("Activity.MediaTrashed", "Media moved to Trash"),
            "MediaRestored" or "ASSET_RESTORED" => SurfaceText.Get("Activity.MediaRestored", "Media restored"),
            "MediaPurged" or "ASSET_PURGED" => SurfaceText.Get("Activity.MediaPurged", "Media permanently deleted"),
            "ASSET_ACTIVATED" => SurfaceText.Get("Activity.MediaActivated", "Media added to Vault"),
            "PrimaryProfileChanged" or "OWNER_CHANGED" => SurfaceText.Get("Activity.OwnerChanged", "Primary Profile changed"),
            "ManualRelatedAdded" => SurfaceText.Get("Activity.ManualRelatedAdded", "Related Profile added"),
            "ManualRelatedRemoved" => SurfaceText.Get("Activity.ManualRelatedRemoved", "Related Profile removed"),
            "UnknownResolved" => SurfaceText.Get("Activity.UnknownResolved", "Unknown person resolved"),
            "IMPORT_COMMITTED" or "ImportCommitted" => SurfaceText.Get("Activity.ImportCommitted", "Import finished"),
            "IMPORT_CANCELLED" or "ImportCancelled" => SurfaceText.Get("Activity.ImportCancelled", "Import cancelled"),
            "FACE_CONFIRMED" or "FaceConfirmed" => SurfaceText.Get("Activity.FaceConfirmed", "Face association confirmed"),
            "FACE_REJECTED" or "FaceRejected" => SurfaceText.Get("Activity.FaceRejected", "Face association rejected"),
            "ThemeChanged" => SurfaceText.Get("Activity.ThemeChanged", "Application theme changed"),
            "LibraryRepairCompleted" => SurfaceText.Get("Activity.LibraryRepairCompleted", "Vault repair completed"),
            _ => SplitEventName(eventType),
        };
    }

    public static string DescribeSubject(ActivityItemReadModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var subjects = new List<string>(3);
        AddDistinct(subjects, item.ProfileDisplayName);
        AddDistinct(subjects, item.MediaDisplayName);
        AddDistinct(subjects, item.ImportDisplayName);

        if (subjects.Count == 0)
        {
            foreach (var value in ReadPayloadLabels(item.PayloadJson))
            {
                AddDistinct(subjects, value);
                if (subjects.Count >= 3)
                {
                    break;
                }
            }
        }

        if (subjects.Count == 0)
        {
            if (item.ProfileId is { } profileId)
            {
                subjects.Add(SurfaceText.Format("Activity.ProfileId", "Profile {0}", profileId.ToString("N")[..8]));
            }
            else if (item.MediaId is { } assetId)
            {
                subjects.Add(SurfaceText.Format("Activity.MediaId", "Media {0}", assetId.ToString("N")[..8]));
            }
            else if (item.ImportUnitId is { } importId)
            {
                subjects.Add(SurfaceText.Format("Activity.ImportId", "Import {0}", importId.ToString("N")[..8]));
            }
        }

        return string.Join(" · ", subjects);
    }

    public static bool IsAllCategory(string? category) =>
        string.IsNullOrWhiteSpace(category)
        || string.Equals(category, AllCategory, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static void AddDistinct(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!values.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(trimmed);
        }
    }

    private static IEnumerable<string> ReadPayloadLabels(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var key in new[]
            {
                "displayName", "profileName", "newDisplayName", "fileName",
                "sourceFileName", "sourceDisplayName", "assetName", "name"
            })
            {
                if (document.RootElement.TryGetProperty(key, out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    yield return property.GetString()!;
                }
            }
        }
    }

    private static string SplitEventName(string value)
    {
        if (value.Contains('_'))
        {
            return value.Replace('_', ' ');
        }

        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
            {
                result.Append(' ');
            }

            result.Append(character);
        }

        return result.ToString();
    }
}
