using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.SystemServices.Database;

namespace Neuterradise.App.Ui;

internal static class MediaSurfaceText
{
    public static string TypeFilter(MediaTypeFilter value) => value switch
    {
        MediaTypeFilter.All => UI.T("MediaGrid.Type.All", "All types"),
        MediaTypeFilter.Images => UI.T("MediaGrid.Type.Images", "Images"),
        MediaTypeFilter.Videos => UI.T("MediaGrid.Type.Videos", "Videos"),
        MediaTypeFilter.Models => UI.T("MediaGrid.Type.Models", "Models"),
        _ => value.ToString(),
    };

    public static string RelationFilter(MediaRelationFilter value) => value switch
    {
        MediaRelationFilter.All => UI.T("MediaGrid.Relation.All", "All relations"),
        MediaRelationFilter.Owned => UI.T("MediaGrid.Relation.Owned", "Owned"),
        MediaRelationFilter.AppearsIn => UI.T("MediaGrid.Relation.AppearsIn", "Appears In"),
        MediaRelationFilter.Manual => UI.T("MediaGrid.Relation.Manual", "Manual"),
        _ => value.ToString(),
    };

    public static string Sort(MediaGridSort value) => value switch
    {
        MediaGridSort.NewestFirst => UI.T("MediaGrid.Sort.NewestFirst", "Newest first"),
        MediaGridSort.OldestFirst => UI.T("MediaGrid.Sort.OldestFirst", "Oldest first"),
        MediaGridSort.NameAscending => UI.T("MediaGrid.Sort.NameAscending", "Name A-Z"),
        MediaGridSort.CapturedNewestFirst => UI.T("MediaGrid.Sort.CapturedNewestFirst", "Captured newest"),
        MediaGridSort.SizeLargestFirst => UI.T("MediaGrid.Sort.SizeLargestFirst", "Largest first"),
        _ => value.ToString(),
    };

    public static string State(MediaGridState value) => value switch
    {
        MediaGridState.Loading => UI.T("MediaGrid.State.Loading", "Loading media…"),
        MediaGridState.EmptyProfileMedia => UI.T("MediaGrid.State.Empty", "This Profile does not have media yet."),
        MediaGridState.FilteredNoResults => UI.T("MediaGrid.State.NoResults", "No media matches these filters."),
        MediaGridState.BackgroundUpdating => UI.T("MediaGrid.State.Updating", "Media is updating in the background."),
        MediaGridState.RecoverablePreviewFailure => UI.T("MediaGrid.State.PreviewFailure", "A preview could not be shown. Open the media item for details."),
        MediaGridState.RecoverableQueryError => UI.T("MediaGrid.State.QueryError", "Media could not be loaded. Try refreshing the Profile."),
        _ => string.Empty,
    };

    public static string Range(MediaGridViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel.TotalCount == 0)
        {
            return UI.F("MediaGrid.Range", "{0}–{1} of {2}", 0, 0, 0);
        }

        var start = ((viewModel.CurrentPage - 1) * viewModel.PageSize) + 1;
        var end = Math.Min(viewModel.CurrentPage * viewModel.PageSize, viewModel.TotalCount);
        return UI.F("MediaGrid.Range", "{0}–{1} of {2}", start, end, viewModel.TotalCount);
    }

    public static string Type(MediaType value) => value switch
    {
        MediaType.Image => UI.T("Media.Type.Image", "Image"),
        MediaType.Video => UI.T("Media.Type.Video", "Video"),
        MediaType.Model => UI.T("Media.Type.Model", "Model"),
        _ => value.ToString(),
    };

    public static string MediaStatus(MediaState value) => value switch
    {
        MediaState.Candidate => UI.T("Media.Status.Candidate", "Candidate"),
        MediaState.Active => UI.T("Media.Status.Active", "Active"),
        MediaState.Trashed => UI.T("Media.Status.InTrash", "In Trash"),
        MediaState.Retired => UI.T("Media.Status.Retired", "Retired"),
        _ => value.ToString(),
    };

    public static string ReconciliationState(ManagedPathState value) => value switch
    {
        ManagedPathState.None => UI.T("Media.Reconciliation.None", "None"),
        ManagedPathState.Pending => UI.T("Media.Reconciliation.Pending", "Pending"),
        ManagedPathState.NeedsAttention => UI.T("Media.Reconciliation.NeedsAttention", "Needs attention"),
        _ => value.ToString(),
    };

    public static string InspectorFeedback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Trim() switch
        {
            "File path copied to clipboard." => UI.T("Inspector.Feedback.FilePathCopiedToClipboard", "File path copied to clipboard."),
            "File name copied to clipboard." => UI.T("Inspector.Feedback.FileNameCopiedToClipboard", "File name copied to clipboard."),
            "Set as Profile cover." => UI.T("Inspector.Feedback.SetAsProfileCover", "Set as Profile cover."),
            "Set as Profile banner." => UI.T("Inspector.Feedback.SetAsProfileBanner", "Set as Profile banner."),
            "Primary Profile changed." => UI.T("Inspector.Feedback.PrimaryProfileChanged", "Primary Profile changed."),
            "Association added." => UI.T("Inspector.Feedback.AssociationAdded", "Association added."),
            "Association removed." => UI.T("Inspector.Feedback.AssociationRemoved", "Association removed."),
            "Moved to Trash (reversible)." => UI.T("Inspector.Feedback.MovedToTrashReversible", "Moved to Trash (reversible)."),
            _ => UI.T("Inspector.Feedback.TheMediaActionCouldNotBeCompleted", "The media action could not be completed."),
        };
    }

    public static string AppearanceBasis(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }

        return raw.Trim() switch
        {
            "Confirmed Appearance" => UI.T("Inspector.Appearance.ConfirmedAppearance", "Confirmed appearance"),
            "Confirmed Face" => UI.T("Inspector.Appearance.ConfirmedFace", "Confirmed face"),
            "Suggested Appearance" => UI.T("Inspector.Appearance.SuggestedAppearance", "Suggested appearance"),
            "Manual" => UI.T("Inspector.Appearance.Manual", "Manual"),
            _ => UI.T("Inspector.Appearance.AppearanceEvidence", "Appearance evidence"),
        };
    }

    public static string DetailGroup(string raw) => raw switch
    {
        "File" => UI.T("Inspector.Group.File", "File"),
        "Image" => UI.T("Inspector.Group.Image", "Image"),
        "Color" => UI.T("Inspector.Group.Color", "Color"),
        "Camera" => UI.T("Inspector.Group.Camera", "Camera"),
        "Dates" => UI.T("Inspector.Group.Dates", "Dates"),
        "Video" => UI.T("Inspector.Group.Video", "Video"),
        "Audio" => UI.T("Inspector.Group.Audio", "Audio"),
        "Metadata" => UI.T("Inspector.Group.Metadata", "Metadata"),
        "3D Model" => UI.T("Inspector.Group.3DModel", "3D Model"),
        "Details" => UI.T("Inspector.Group.Details", "Details"),
        "Vault Location" => UI.T("Inspector.Group.VaultLocation", "Vault Location"),
        "System Details" => UI.T("Inspector.Group.SystemDetails", "System Details"),
        "Raw Metadata" => UI.T("Inspector.Group.RawMetadata", "Raw Metadata"),
        _ => raw,
    };

    public static string DetailField(string raw) => raw switch
    {
        "File Name" => UI.T("Inspector.Field.FileName", "File Name"),
        "Original Name" => UI.T("Inspector.Field.OriginalName", "Original Name"),
        "Format" => UI.T("Inspector.Field.Format", "Format"),
        "File Size" => UI.T("Inspector.Field.FileSize", "File Size"),
        "Dimensions" => UI.T("Inspector.Field.Dimensions", "Dimensions"),
        "Aspect Ratio" => UI.T("Inspector.Field.AspectRatio", "Aspect Ratio"),
        "Orientation" => UI.T("Inspector.Field.Orientation", "Orientation"),
        "Bit Depth" => UI.T("Inspector.Field.BitDepth", "Bit Depth"),
        "Color Space" => UI.T("Inspector.Field.ColorSpace", "Color Space"),
        "Color Profile" => UI.T("Inspector.Field.ColorProfile", "Color Profile"),
        "Camera Make" => UI.T("Inspector.Field.CameraMake", "Camera Make"),
        "Camera Model" => UI.T("Inspector.Field.CameraModel", "Camera Model"),
        "Lens" => UI.T("Inspector.Field.Lens", "Lens"),
        "Focal Length" => UI.T("Inspector.Field.FocalLength", "Focal Length"),
        "Aperture" => UI.T("Inspector.Field.Aperture", "Aperture"),
        "Shutter Speed" => UI.T("Inspector.Field.ShutterSpeed", "Shutter Speed"),
        "ISO" => "ISO",
        "Exposure Program" => UI.T("Inspector.Field.ExposureProgram", "Exposure Program"),
        "Exposure Bias" => UI.T("Inspector.Field.ExposureBias", "Exposure Bias"),
        "Flash" => UI.T("Inspector.Field.Flash", "Flash"),
        "Captured" => UI.T("Inspector.Field.Captured", "Captured"),
        "Created" => UI.T("Inspector.Field.Created", "Created"),
        "Added to Vault" => UI.T("Inspector.Field.AddedToVault", "Added to Vault"),
        "Duration" => UI.T("Inspector.Field.Duration", "Duration"),
        "Video Codec" => UI.T("Inspector.Field.VideoCodec", "Video Codec"),
        "Profile" => UI.T("Inspector.Field.Profile", "Profile"),
        "Frame Rate" => UI.T("Inspector.Field.FrameRate", "Frame Rate"),
        "Bitrate" => UI.T("Inspector.Field.Bitrate", "Bitrate"),
        "Pixel Format" => UI.T("Inspector.Field.PixelFormat", "Pixel Format"),
        "Audio Codec" => UI.T("Inspector.Field.AudioCodec", "Audio Codec"),
        "Channels" => UI.T("Inspector.Field.Channels", "Channels"),
        "Sample Rate" => UI.T("Inspector.Field.SampleRate", "Sample Rate"),
        "Audio Bitrate" => UI.T("Inspector.Field.AudioBitrate", "Audio Bitrate"),
        "Color Primaries" => UI.T("Inspector.Field.ColorPrimaries", "Color Primaries"),
        "Color Transfer" => UI.T("Inspector.Field.ColorTransfer", "Color Transfer"),
        "HDR" => "HDR",
        "Container" => UI.T("Inspector.Field.Container", "Container"),
        "Rotation" => UI.T("Inspector.Field.Rotation", "Rotation"),
        "Device Make" => UI.T("Inspector.Field.DeviceMake", "Device Make"),
        "Device Model" => UI.T("Inspector.Field.DeviceModel", "Device Model"),
        "Software" => UI.T("Inspector.Field.Software", "Software"),
        "Encoder" => UI.T("Inspector.Field.Encoder", "Encoder"),
        "Location" => UI.T("Inspector.Field.Location", "Location"),
        "Recorded" => UI.T("Inspector.Field.Recorded", "Recorded"),
        "Meshes" => UI.T("Inspector.Field.Meshes", "Meshes"),
        "Vertices" => UI.T("Inspector.Field.Vertices", "Vertices"),
        "Triangles" => UI.T("Inspector.Field.Triangles", "Triangles"),
        "Materials" => UI.T("Inspector.Field.Materials", "Materials"),
        "Textures" => UI.T("Inspector.Field.Textures", "Textures"),
        "Animations" => UI.T("Inspector.Field.Animations", "Animations"),
        "Cameras" => UI.T("Inspector.Field.Cameras", "Cameras"),
        "Lights" => UI.T("Inspector.Field.Lights", "Lights"),
        "Bounds" => UI.T("Inspector.Field.Bounds", "Bounds"),
        "Units" => UI.T("Inspector.Field.Units", "Units"),
        "Adapter" => UI.T("Inspector.Field.Adapter", "Adapter"),
        "Adapter Version" => UI.T("Inspector.Field.AdapterVersion", "Adapter Version"),
        "Adapter Status" => UI.T("Inspector.Field.AdapterStatus", "Adapter Status"),
        "Dependency Status" => UI.T("Inspector.Field.DependencyStatus", "Dependency Status"),
        "Bundle SHA256" => "Bundle SHA256",
        "Import source path" => UI.T("Inspector.Field.ImportSourcePath", "Import source path"),
        "Import source name" => UI.T("Inspector.Field.ImportSourceName", "Import source name"),
        "Vault Location (current)" => UI.T("Inspector.Field.VaultLocationCurrent", "Vault Location (current)"),
        "Pending Target Location" => UI.T("Inspector.Field.PendingTargetLocation", "Pending Target Location"),
        "Reconciliation State" => UI.T("Inspector.Field.ReconciliationState", "Reconciliation State"),
        "Status" => UI.T("Inspector.Field.Status", "Status"),
        "File Fingerprint" => UI.T("Inspector.Field.FileFingerprint", "File Fingerprint"),
        "Media ID" => "Media ID",
        _ => raw,
    };

    public static string DetailValue(string field, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "—")
        {
            return raw;
        }

        if (field == "Status" && Enum.TryParse<MediaState>(raw, ignoreCase: true, out var assetState))
        {
            return MediaStatus(assetState);
        }

        if (field == "Reconciliation State"
            && Enum.TryParse<ManagedPathState>(raw, ignoreCase: true, out var pathState))
        {
            return ReconciliationState(pathState);
        }

        return raw switch
        {
            "Pending reconciliation" => UI.T("Inspector.Value.PendingReconciliation", "Pending reconciliation"),
            "Yes" => UI.T("Inspector.Value.Yes", "Yes"),
            "No" => UI.T("Inspector.Value.No", "No"),
            "No (SDR)" => UI.T("Inspector.Value.NoSDR", "No (SDR)"),
            "SelfContained" => UI.T("Inspector.Value.SelfContained", "Self-contained"),
            "DependenciesMissing" => UI.T("Inspector.Value.DependenciesMissing", "Dependencies missing"),
            "DependenciesUnknown" => UI.T("Inspector.Value.DependenciesUnknown", "Dependencies unknown"),
            _ => raw,
        };
    }
}
