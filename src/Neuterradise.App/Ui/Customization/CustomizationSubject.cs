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

namespace Neuterradise.App.Ui;

public sealed partial class CustomizationCenter
{
    // ---------------------------------------------------------------- Profile preview subject

    private sealed record CustomizationCoverCandidate(
        Guid MediaId,
        CoverVisualSourceKind SourceKind,
        long? TimestampMilliseconds,
        double SuggestedCropX,
        double SuggestedCropY,
        string FileName,
        string? PreviewPath,
        bool IsRecommended);

    private sealed record CustomizationBannerCandidate(
        Guid MediaId,
        double SuggestedFocusX,
        double SuggestedFocusY,
        string DisplayTitle,
        string? PreviewPath,
        string? PlaybackPath,
        bool IsRecommended);

    private sealed record CustomizationFigureCandidate(
        Guid MediaId,
        string DisplayName,
        string? ThumbnailPath,
        string? RenderPath,
        string SourceSha256,
        bool IsSelectable,
        string? EligibilityReason);

    private sealed record CustomizationSubjectSnapshot(
        ProfilePresentationState State,
        ProfileDetailReadModel Detail,
        IReadOnlyList<ProfileMediaItemReadModel> MediaItems,
        MediaGridCardViewModel? PreviewMediaCard,
        ProfilePresentationModel PreviewModel,
        IReadOnlyList<CustomizationCoverCandidate> CoverCandidates,
        IReadOnlyList<CustomizationBannerCandidate> BannerCandidates,
        IReadOnlyList<CustomizationFigureCandidate> FigureCandidates,
        ImageRef? PreviewCoverSource,
        ImageRef? PreviewBannerImageSource,
        string? PreviewBannerVideoPath);

    private async Task LoadSubjectAsync(Guid profileId, int generation)
    {
        var snapshot = await LoadSubjectSnapshotAsync(profileId, generation).ConfigureAwait(true);
        if (snapshot is null || generation != _subjectGeneration || _closed || _session.Context.ProfileId != profileId)
        {
            return;
        }

        _subject = snapshot;
        _coverSource = _committedCoverSource = snapshot.PreviewCoverSource;
        _bannerSource = _committedBannerSource = snapshot.PreviewBannerImageSource;
        _bannerVideoPath = _committedBannerVideoPath = snapshot.PreviewBannerVideoPath;
        _figureEnabledDraft = snapshot.State.Sources?.FigureMediaId is not null;
        _session.RebaseProfile(snapshot.State);
        _subjectText.Text = SubjectLine();
        SyncBannerPreviewFromWorkingSource();
        RenderAll();
    }

    private async Task<CustomizationSubjectSnapshot?> LoadSubjectSnapshotAsync(Guid profileId, int generation)
    {
        CancelSubjectLoad();
        var cts = new CancellationTokenSource();
        _subjectLoadCts = cts;
        var cancellationToken = cts.Token;
        try
        {
            var detailTask = _services.Catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken);
            var mediaTask = _services.Catalog.ProfileReads.GetMediaPageAsync(
                profileId,
                pageSize: SubjectMediaLimit,
                cancellationToken: cancellationToken);
            var figureCandidatesTask = _services.Catalog.ProfileReads.GetFigureCandidatesAsync(
                profileId,
                cancellationToken);
            await Task.WhenAll(detailTask, mediaTask, figureCandidatesTask).ConfigureAwait(true);
            if (generation != _subjectGeneration || cancellationToken.IsCancellationRequested || _closed)
            {
                return null;
            }

            var detail = await detailTask.ConfigureAwait(true);
            if (detail is null)
            {
                _services.Toast(UI.T("Customize.ProfileMissing", "That Profile is no longer available."), "warning");
                return null;
            }

            var mediaPage = await mediaTask.ConfigureAwait(true);
            var overrides = ProfileAppearanceOverrides.Parse(detail.AppearanceOverridesJson);
            var state = new ProfilePresentationState(
                detail.ProfileId,
                detail.RowVersion,
                detail.LayoutPresetId,
                overrides,
                new ProfileMediaSources(detail.CoverMediaId, detail.BannerMediaId, detail.FigureMediaId));

            MediaGridCardViewModel? previewMediaCard = null;
            if (mediaPage.Items.FirstOrDefault() is { } previewItem)
            {
                var previewThumbnail = await _services.Runtime.MediaResources.ResolveMediaThumbnailAsync(
                    previewItem.MediaId,
                    cancellationToken).ConfigureAwait(true);
                string? previewHoverPath = null;
                if (previewItem.MediaType == MediaType.Video
                    && !string.IsNullOrWhiteSpace(previewItem.HoverRelativePath))
                {
                    try
                    {
                        previewHoverPath = _services.Paths.ResolveVaultRelativePath(
                            Neuterradise.App.SystemServices.Storage.VaultPathArea.MediaAssets,
                            previewItem.HoverRelativePath);
                    }
                    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        previewHoverPath = null;
                    }
                }

                var previewRelation = previewItem.RelationType switch
                {
                    ProfileMediaRelation.Owner => MediaRelationBadge.Owned,
                    ProfileMediaRelation.Appears => MediaRelationBadge.AppearsIn,
                    _ => MediaRelationBadge.Manual,
                };
                previewMediaCard = new MediaGridCardViewModel(new MediaGridItem(
                    previewItem.MediaId,
                    previewItem.MediaType,
                    previewRelation,
                    previewItem.ManagedFileName,
                    previewItem.PixelWidth,
                    previewItem.PixelHeight,
                    previewItem.DurationMs,
                    false,
                    previewItem.IsFavorite,
                    previewItem.OriginalFileName ?? previewItem.ManagedFileName,
                    previewThumbnail.State == MediaAssetResourceState.Ready ? previewThumbnail.PhysicalPath : null,
                    previewHoverPath));
            }
            var ranked = await new ProfileMediaCandidateReads(_services.Catalog)
                .ReadAllEligibleAsync(profileId, cancellationToken)
                .ConfigureAwait(true);

            var coverCandidates = new List<CustomizationCoverCandidate>();
            foreach (var candidate in ranked.Covers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resource = await _services.Runtime.MediaResources.ResolveMediaThumbnailAsync(
                    candidate.MediaId,
                    cancellationToken).ConfigureAwait(true);
                coverCandidates.Add(new CustomizationCoverCandidate(
                    candidate.MediaId,
                    candidate.SourceTimestampMs.HasValue
                        ? CoverVisualSourceKind.VideoFrame
                        : CoverVisualSourceKind.Image,
                    candidate.SourceTimestampMs,
                    0.5,
                    0.5,
                    candidate.DisplayName,
                    resource.State == MediaAssetResourceState.Ready ? resource.PhysicalPath : null,
                    candidate.Rank <= ProfileMediaCandidateReads.MaximumCoverCandidates));
            }

            var bannerCandidates = new List<CustomizationBannerCandidate>();
            foreach (var candidate in ranked.BannerHovers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preview = await _services.Runtime.MediaResources.ResolveMediaThumbnailAsync(
                    candidate.MediaId,
                    cancellationToken).ConfigureAwait(true);
                bannerCandidates.Add(new CustomizationBannerCandidate(
                    candidate.MediaId,
                    0.5,
                    0.5,
                    candidate.DisplayName,
                    preview.State == MediaAssetResourceState.Ready ? preview.PhysicalPath : null,
                    _services.Paths.ResolveVaultRelativePath(
                        Neuterradise.App.SystemServices.Storage.VaultPathArea.MediaAssets,
                        candidate.RelativePath),
                    candidate.Rank <= ProfileMediaCandidateReads.MaximumBannerCandidates));
            }

            string? ResolveFigureAssetPath(string? relativePath)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) return null;
                try
                {
                    return _services.Paths.ResolveVaultRelativePath(
                        Neuterradise.App.SystemServices.Storage.VaultPathArea.MediaAssets,
                        relativePath);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            var figureCandidates = (await figureCandidatesTask.ConfigureAwait(true))
                .Select(candidate => new CustomizationFigureCandidate(
                    candidate.MediaId,
                    candidate.DisplayName,
                    ResolveFigureAssetPath(candidate.ThumbnailRelativePath),
                    candidate.IsSelectable ? ResolveFigureAssetPath(candidate.ModelRenderRelativePath) : null,
                    candidate.SourceSha256,
                    candidate.IsSelectable,
                    candidate.EligibilityReason))
                .ToList();

            var coverResource = await _services.Runtime.MediaResources.ResolveSelectedProfileCoverAsync(
                profileId,
                cancellationToken).ConfigureAwait(true);
            var coverPath = coverResource.State == MediaAssetResourceState.Ready
                ? coverResource.PhysicalPath
                : null;

            string? bannerPath = null;
            string? bannerVideoPath = null;
            if (detail.BannerMediaAssetId is { } bannerMediaAssetId)
            {
                var selectedBanner = await _services.Catalog.MediaAssetReads.GetPublicSelectedMediaAsync(
                    profileId,
                    bannerMediaAssetId,
                    cancellationToken).ConfigureAwait(true);
                if (selectedBanner?.Role == MediaAssetRole.Hover)
                {
                    var bannerThumbnail = await _services.Runtime.MediaResources.ResolveMediaThumbnailAsync(
                        selectedBanner.MediaId,
                        cancellationToken).ConfigureAwait(true);
                    bannerPath = bannerThumbnail.State == MediaAssetResourceState.Ready
                        ? bannerThumbnail.PhysicalPath
                        : null;
                    bannerVideoPath = _services.Paths.ResolveVaultRelativePath(
                        Neuterradise.App.SystemServices.Storage.VaultPathArea.MediaAssets,
                        selectedBanner.RelativePath);
                }
            }

            var previewModel = new ProfilePresentationModel(
                detail.ProfileId,
                detail.DisplayName,
                detail.CategoryName,
                detail.Tags,
                detail.Rating,
                detail.IsFavorite,
                detail.Overview,
                detail.Notes,
                detail.ActiveOwnedMediaCount,
                false,
                overrides.ProfileCardVariantId);

            return new CustomizationSubjectSnapshot(
                state,
                detail,
                mediaPage.Items,
                previewMediaCard,
                previewModel,
                coverCandidates,
                bannerCandidates,
                figureCandidates,
                ImageRef.FromPath(coverPath, 900),
                ImageRef.FromPath(bannerPath, 1600),
                bannerVideoPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_subjectLoadCts, cts))
            {
                _subjectLoadCts = null;
            }

            cts.Dispose();
        }
    }
    private void CancelSubjectLoad()
    {
        var cts = _subjectLoadCts;
        _subjectLoadCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

}
