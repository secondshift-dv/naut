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

}
