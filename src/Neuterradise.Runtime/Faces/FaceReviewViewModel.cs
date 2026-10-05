using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.Faces;

public sealed class FaceReviewViewModel : ScreenStateViewModel, IDisposable
{
    private const int QueuePageSize = 50;
    private const int QueueLowWatermark = 10;

    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly FaceReads? _faceReads;
    private readonly FaceReviewQueueReads? _queueReads;
    private readonly FaceDecisionOperations? _decisionOperations;
    private readonly ProfileReads? _profileReads;
    private readonly OverlayHostViewModel? _overlay;

    private bool _disposed;
    private Guid? _profileId;
    private FaceReviewItemViewModel? _selectedReview;
    private string? _statusMessage;
    private string? _scopedProfileDisplayName;
    private FaceReviewQueueCursor? _nextQueueCursor;
    private int _queueLoadActive;
    private long _reviewLoadGeneration;
    private long _queueReplenishGeneration;

    public FaceReviewViewModel(Guid? profileId = null)
        : this(profileId, null, null, null, null, null, null)
    {
    }

    public FaceReviewViewModel(
        Guid? profileId,
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        FaceReads? faceReads = null,
        FaceDecisionOperations? decisionOperations = null,
        ProfileReads? profileReads = null,
        OverlayHostViewModel? overlay = null)
    {
        _profileId = profileId;
        _catalog = catalog;
        _navigation = navigation;
        _faceReads = faceReads ?? (_catalog is not null ? new FaceReads(_catalog) : null);
        _queueReads = _catalog is not null && _faceReads is not null
            ? new FaceReviewQueueReads(_catalog, _faceReads)
            : null;
        _decisionOperations = decisionOperations ?? (_catalog is not null ? new FaceDecisionOperations(_catalog) : null);
        _profileReads = profileReads ?? _catalog?.ProfileReads;
        _overlay = overlay;

        ConfirmCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedReview is { } review)
            {
                await ConfirmFaceAsync(review);
            }
        }, () => SelectedReview is { } review && CanConfirmFace(review));

        // Scoped review assigns only to the explicitly selected Profile target. The row is passed
        // explicitly so list actions never depend on hidden SelectedReview state.
        AssignToThisProfileCommand = new AsyncRelayCommand(async parameter =>
        {
            var review = parameter as FaceReviewItemViewModel ?? SelectedReview;
            if (review is not null && _profileId is { } profileId && profileId != Guid.Empty)
            {
                await ConfirmFaceAsync(review, profileId);
            }
        }, parameter =>
        {
            var review = parameter as FaceReviewItemViewModel ?? SelectedReview;
            return IsScoped && review is { IsUnresolved: true };
        });

        // Ignore / Not a person maps to the canonical rejection operation.
        RejectCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedReview is { } review)
            {
                await RejectFaceAsync(review);
            }
        }, () => SelectedReview is not null && !SelectedReview.IsRejected);

        AssignOtherCommand = new RelayCommand(param =>
        {
            if (SelectedReview is { } review && param is Guid targetProfileId)
            {
                TaskObserver.Observe(AssignOtherFaceAsync(review, targetProfileId), "FaceReviewViewModel.AssignOtherFaceAsync");
            }
        });

        AssignOtherWithPickerCommand = new AsyncRelayCommand(async param =>
        {
            var review = param as FaceReviewItemViewModel ?? SelectedReview;
            if (review is null || _overlay is null)
            {
                return;
            }

            if (_catalog is null)
            {
                var emptyPickerRequest = new ProfilePickerOverlayRequest(
                    [],
                    picked => TaskObserver.Observe(
                        AssignOtherFaceAsync(review, picked.ProfileId),
                        "FaceReviewViewModel.AssignOtherFaceAsync"),
                    title: SurfaceText.Get("Faces.Picker.AssignTitle", "Assign Other Profile"),
                    prompt: SurfaceText.Get("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"));
                _overlay.Push(emptyPickerRequest);
                return;
            }

            var candidates = await new ProfilePickerReads(_catalog!).GetAllCandidatesAsync().ConfigureAwait(true);
            var request = new ProfilePickerOverlayRequest(
                candidates,
                picked => TaskObserver.Observe(
                    AssignOtherFaceAsync(review, picked.ProfileId),
                    "FaceReviewViewModel.AssignOtherFaceAsync"),
                title: SurfaceText.Get("Faces.Picker.AssignTitle", "Assign Other Profile"),
                prompt: SurfaceText.Get("Faces.Picker.AssignPrompt", "Choose a Profile to assign this face detection to:"));
            _overlay.Push(request);
        });

        OpenMediaCommand = new AsyncRelayCommand(
            async param =>
            {
                var assetId = param is Guid id ? id : SelectedReview?.MediaId;
                if (assetId is not { } asset || asset == Guid.Empty || _navigation is null)
                {
                    return;
                }

                Guid? ownerProfileId = null;
                if (_catalog is not null)
                {
                    var detail = await _catalog.MediaReads.GetMediaDetailAsync(asset, RouteCancellationToken).ConfigureAwait(true);
                    ownerProfileId = detail?.OwnerProfile?.ProfileId;
                }

                ownerProfileId ??= SelectedReview?.ConfirmedProfileId ?? SelectedReview?.SuggestedProfileId;
                if (ownerProfileId is { } owner && owner != Guid.Empty)
                {
                    _navigation.Navigate(new ProfileRoute(owner, InspectMediaId: asset));
                }
                else
                {
                    ShowRecoverableError(SurfaceText.Get(
                        "Faces.Error.MediaNoProfile",
                        "This media item has no Profile to open it in."));
                }
            },
            onError: exception =>
            {
                Trace.TraceWarning("Face media open failed: {0}", exception.GetType().Name);
                ShowRecoverableError(SurfaceText.Get(
                    "Faces.Error.MediaOpen",
                    "This media item could not be opened."));
            });

        OpenProfileCommand = new RelayCommand(param =>
        {
            var profileIdToOpen = param is Guid id
                ? id
                : SelectedReview?.ConfirmedProfileId ?? SelectedReview?.SuggestedProfileId;
            if (profileIdToOpen.HasValue && profileIdToOpen.Value != Guid.Empty)
            {
                _navigation?.Navigate(new ProfileRoute(profileIdToOpen.Value));
            }
        });

        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }

        ShowReady();
    }

    public Guid? ProfileId
    {
        get => _profileId;
        set
        {
            if (_profileId != value)
            {
                _profileId = value;
                _scopedProfileDisplayName = null;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsScoped));
                RaisePropertyChanged(nameof(ScopedProfileDisplayName));
                (AssignToThisProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AssignAllToThisProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                // Retire the previous generation before scheduling a new scoped load.
                Interlocked.Increment(ref _reviewLoadGeneration);
                Interlocked.Increment(ref _queueReplenishGeneration);
                TaskObserver.Observe(LoadReviewsAsync(RouteCancellationToken), "FaceReviewViewModel.LoadReviewsAsync");
            }
        }
    }

    public bool IsScoped => _profileId.HasValue && _profileId.Value != Guid.Empty;

    public string? ScopedProfileDisplayName => _scopedProfileDisplayName;

    public ObservableCollection<FaceReviewItemViewModel> Reviews { get; } = [];

    public FaceReviewItemViewModel? SelectedReview
    {
        get => _selectedReview;
        set
        {
            if (_selectedReview != value)
            {
                _selectedReview = value;
                RaisePropertyChanged();
                (ConfirmCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AssignToThisProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RejectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                RaisePropertyChanged();
            }
        }
    }

    public bool HasReviews => Reviews.Count > 0;

    public int PendingReviewCount => Reviews.Count(static review => review.IsUnresolved);

    public ICommand ConfirmCommand { get; }
    public ICommand AssignToThisProfileCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand AssignOtherCommand { get; }
    public ICommand AssignOtherWithPickerCommand { get; }
    public ICommand OpenMediaCommand { get; }
    public ICommand OpenProfileCommand { get; }

    // Bulk assignment is available only for the explicitly scoped Profile.
    public ICommand AssignAllToThisProfileCommand => _assignAllCommand ??= new AsyncRelayCommand(
        () => AssignAllToThisProfileAsync(),
        () => IsScoped && Reviews.Any(r => r.IsUnresolved) && !AssignAllRunning);
    private AsyncRelayCommand? _assignAllCommand;

    private bool _assignAllRunning;
    public bool AssignAllRunning
    {
        get => _assignAllRunning;
        private set
        {
            if (_assignAllRunning != value)
            {
                _assignAllRunning = value;
                RaisePropertyChanged();
                (AssignAllToThisProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public void PopulateReviews(IEnumerable<FaceReviewReadModel> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        Reviews.Clear();
        SelectedReview = null;
        foreach (var review in reviews)
        {
            AddReview(review);
        }

        SelectedReview = Reviews.FirstOrDefault();
        RefreshCollectionState();
    }

    private void AddReview(FaceReviewReadModel model)
    {
        if (Reviews.Any(review => review.FaceId == model.FaceId))
        {
            return;
        }

        var item = new FaceReviewItemViewModel(model);
        Reviews.Add(item);

        // Existing and missing crops both go through the asynchronous derived-still authority.
        // That authority can reuse a cached derivative without synchronously enumerating the cache
        // directory on the UI dispatcher.
        EnsureFaceCrop(item);
    }

    private void EnsureFaceCrop(FaceReviewItemViewModel item)
    {
        if (_catalog is null || item.HasFaceCrop)
        {
            return;
        }

        if (FaceBoundingBox.TryParse(item.BoundingBoxJson) is null)
        {
            return;
        }

        TaskObserver.Observe(
            LoadFaceCropAsync(item, item.FaceId, item.DetectionKey),
            "FaceReviewViewModel.LoadFaceCropAsync");
    }

    private async Task LoadFaceCropAsync(FaceReviewItemViewModel item, Guid faceId, string detectionKey)
    {
        try
        {
            var artifact = await new MediaAssetResourceAuthority(_catalog!)
                .ResolveMediaThumbnailAsync(item.MediaId, RouteCancellationToken).ConfigureAwait(false);
            var path = artifact.State == MediaAssetResourceState.Ready ? artifact.PhysicalPath : null;

            // Apply only while the item is still present, its detection key matches, and the view model is alive.
            UiDispatch.Run(() =>
            {
                if (_disposed || !IsRouteActive) return;
                if (!Reviews.Contains(item)) return;
                if (item.FaceId != faceId) return;
                if (item.DetectionKey != detectionKey) return;
                if (string.IsNullOrWhiteSpace(path)) return;
                item.FaceCropPath = path;
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected lifecycle, not a user error.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Face crop generation failed: {0}", ex.GetType().Name);
        }
    }

    public async Task LoadReviewsAsync(CancellationToken cancellationToken = default)
    {
        if (_faceReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _reviewLoadGeneration);
        var snapshotProfileId = _profileId;

        // UI: loading state.
        UiDispatch.Run(() =>
        {
            if (generation != _reviewLoadGeneration || _disposed) return;
            ShowLoading();
            StatusMessage = null;
        });

        _nextQueueCursor = null;
        Interlocked.Increment(ref _queueReplenishGeneration);

        try
        {
            FaceReviewQueuePage? queuePage = null;
            IReadOnlyList<FaceReviewReadModel>? fallbackModels = null;
            string? scopedProfileDisplayName = null;

            if (snapshotProfileId is { } scopedProfileId
                && scopedProfileId != Guid.Empty
                && _profileReads is not null)
            {
                try
                {
                    var header = await _profileReads
                        .GetHeaderAsync(scopedProfileId, cancellationToken)
                        .ConfigureAwait(false);
                    scopedProfileDisplayName = header?.DisplayName;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || RouteCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Scoped face review profile header load failed: {0}", ex.GetType().Name);
                }
            }

            if (_queueReads is not null)
            {
                queuePage = await _queueReads
                    .GetPageAsync(snapshotProfileId, QueuePageSize, cursor: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (snapshotProfileId.HasValue && snapshotProfileId.Value != Guid.Empty)
                {
                    fallbackModels = await _faceReads.GetFaceReviewsForProfileAsync(
                        snapshotProfileId.Value, QueuePageSize, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    fallbackModels = await _faceReads.GetUnresolvedFaceReviewsAsync(
                        QueuePageSize, cancellationToken).ConfigureAwait(false);
                }
            }

            // UI: apply results only if generation is current and scope unchanged.
            UiDispatch.Run(() =>
            {
                if (generation != _reviewLoadGeneration || _disposed || !IsRouteActive) return;
                if (_profileId != snapshotProfileId) return;

                if (!string.Equals(_scopedProfileDisplayName, scopedProfileDisplayName, StringComparison.Ordinal))
                {
                    _scopedProfileDisplayName = scopedProfileDisplayName;
                    RaisePropertyChanged(nameof(ScopedProfileDisplayName));
                }

                if (queuePage is not null)
                {
                    _nextQueueCursor = queuePage.NextCursor;
                    PopulateReviews(queuePage.Items);
                }
                else if (fallbackModels is not null)
                {
                    PopulateReviews(fallbackModels);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || RouteCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Face review load failed: {0}", ex.GetType().Name);
            UiDispatch.Run(() =>
            {
                if (generation != _reviewLoadGeneration || _disposed) return;
                StatusMessage = SurfaceText.Get("Faces.LoadFailed", "Face reviews could not be loaded.");
                ShowRecoverableError(StatusMessage);
            });
        }
    }

    private void CompleteResolvedReview(FaceReviewItemViewModel review)
    {
        Reviews.Remove(review);

        if (ReferenceEquals(SelectedReview, review) || SelectedReview is null)
        {
            SelectedReview = Reviews.FirstOrDefault();
        }

        RefreshCollectionState();
        ReplenishQueueIfNeeded();
    }

    private void RefreshCollectionState()
    {
        RaisePropertyChanged(nameof(HasReviews));
        RaisePropertyChanged(nameof(PendingReviewCount));
        // The bulk command scopes itself to the current unresolved collection. Queue refill,
        // decision settlement, and profile changes can all alter that scope without changing the
        // selected row, so CanExecute must be invalidated with the collection projection.
        (AssignAllToThisProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        if (Reviews.Count == 0)
        {
            ShowEmpty();
        }
        else
        {
            ShowReady();
        }
    }

    private void ReplenishQueueIfNeeded()
    {
        if (_queueReads is null
            || Reviews.Count >= QueueLowWatermark
            || Interlocked.CompareExchange(ref _queueLoadActive, 1, 0) != 0)
        {
            return;
        }

        TaskObserver.Observe(ReplenishQueueAsync(RouteCancellationToken), "FaceReviewViewModel.ReplenishQueue");
    }

    private async Task ReplenishQueueAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _queueReplenishGeneration);
        var snapshotProfileId = _profileId;
        var snapshotCursor = _nextQueueCursor;

        try
        {
            if (_queueReads is null)
            {
                return;
            }

            var page = await _queueReads
                .GetPageAsync(snapshotProfileId, QueuePageSize, snapshotCursor, cancellationToken)
                .ConfigureAwait(false);

            // UI: apply only if generation and scope remain current.
            UiDispatch.Run(() =>
            {
                if (generation != _queueReplenishGeneration || _disposed || !IsRouteActive) return;
                if (_profileId != snapshotProfileId) return;

                _nextQueueCursor = page.NextCursor;

                foreach (var model in page.Items)
                {
                    AddReview(model);
                }

                if (SelectedReview is null)
                {
                    SelectedReview = Reviews.FirstOrDefault();
                }

                RefreshCollectionState();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || RouteCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Face review queue replenish failed: {0}", exception.GetType().Name);
            // Stale queue replenishment must not overwrite the current UI generation.
            UiDispatch.Run(() =>
            {
                if (generation != _queueReplenishGeneration || _disposed) return;
                StatusMessage = SurfaceText.Get("Faces.LoadFailed", "Face reviews could not be loaded.");
            });
        }
        finally
        {
            Volatile.Write(ref _queueLoadActive, 0);
        }
    }

    public async Task ConfirmFaceAsync(FaceReviewItemViewModel review, Guid? targetProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(review);

        var targetId = ResolveConfirmationTarget(review, targetProfileId);
        if (!targetId.HasValue || targetId.Value == Guid.Empty)
        {
            StatusMessage = SurfaceText.Get(
                "Faces.Status.NoTarget",
                "No target Profile is available for confirmation.");
            return;
        }

        if (_decisionOperations is null)
        {
            review.ApplyDecision(
                FaceDecisionState.Confirmed,
                targetId.Value,
                review.SelectedCandidate?.ProfileDisplayName
                    ?? review.SuggestedProfileDisplayName
                    ?? SurfaceText.Get("Common.Profile", "Profile"),
                review.RowVersion + 1);
            StatusMessage = SurfaceText.Get("Faces.Status.Confirmed", "Confirmed as {0}");
            CompleteResolvedReview(review);
            return;
        }

        try
        {
            var command = new ConfirmFaceCommand(review.FaceId, targetId.Value, review.RowVersion);
            var result = await _decisionOperations.ConfirmFaceAsync(command).ConfigureAwait(true);

            if (result.IsSuccess && result.Value is { } outcome)
            {
                review.ApplyDecision(
                    outcome.DecisionState,
                    targetId.Value,
                    review.SelectedCandidate?.ProfileDisplayName
                        ?? review.SuggestedProfileDisplayName
                        ?? SurfaceText.Get("Faces.Label.ConfirmedProfile", "Confirmed Profile"),
                    outcome.RowVersion);
                StatusMessage = SurfaceText.Get(
                    "Faces.Status.ConfirmedSuccessfully",
                    "Face confirmed successfully.");
                CompleteResolvedReview(review);
            }
            else
            {
                StatusMessage = SurfaceText.Get(
                    "Faces.Status.ConfirmFailed",
                    "The face could not be confirmed.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Face confirmation failed: {0}", ex.GetType().Name);
            StatusMessage = SurfaceText.Get(
                "Faces.Status.ConfirmFailed",
                "The face could not be confirmed.");
        }
    }

    public bool CanConfirmFace(FaceReviewItemViewModel review, Guid? targetProfileId = null) =>
        review is not null
        && review.IsUnresolved
        && ResolveConfirmationTarget(review, targetProfileId) is { } target
        && target != Guid.Empty;

    private Guid? ResolveConfirmationTarget(FaceReviewItemViewModel review, Guid? targetProfileId = null) =>
        targetProfileId
        ?? review.SelectedCandidate?.ProfileId
        ?? review.SuggestedProfileId
        ?? ProfileId;

    public async Task RejectFaceAsync(FaceReviewItemViewModel review)
    {
        ArgumentNullException.ThrowIfNull(review);

        if (_decisionOperations is null)
        {
            review.ApplyDecision(FaceDecisionState.Rejected, null, null, review.RowVersion + 1);
            StatusMessage = SurfaceText.Get("Faces.Status.Rejected", "Not this person");
            CompleteResolvedReview(review);
            return;
        }

        try
        {
            var command = new RejectFaceCommand(review.FaceId, review.RowVersion);
            var result = await _decisionOperations.RejectFaceAsync(command).ConfigureAwait(true);

            if (result.IsSuccess && result.Value is { } outcome)
            {
                review.ApplyDecision(outcome.DecisionState, null, null, outcome.RowVersion);
                StatusMessage = SurfaceText.Get("Faces.Status.Rejected", "Not this person");
                CompleteResolvedReview(review);
            }
            else
            {
                StatusMessage = SurfaceText.Get(
                    "Faces.Status.RejectFailed",
                    "The face could not be rejected.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Face rejection failed: {0}", ex.GetType().Name);
            StatusMessage = SurfaceText.Get(
                "Faces.Status.RejectFailed",
                "The face could not be rejected.");
        }
    }

    public async Task AssignOtherFaceAsync(FaceReviewItemViewModel review, Guid targetProfileId)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (targetProfileId == Guid.Empty)
        {
            return;
        }

        string? targetName = null;
        if (_profileReads is not null)
        {
            var header = await _profileReads.GetHeaderAsync(targetProfileId).ConfigureAwait(true);
            targetName = header?.DisplayName;
        }

        if (_decisionOperations is null)
        {
            review.ApplyDecision(
                FaceDecisionState.Confirmed,
                targetProfileId,
                targetName ?? SurfaceText.Get("Faces.Label.AssignedProfile", "Assigned Profile"),
                review.RowVersion + 1);
            StatusMessage = SurfaceText.Get("Faces.Status.Assigned", "Face assigned to Profile.");
            CompleteResolvedReview(review);
            return;
        }

        try
        {
            var command = new ConfirmFaceCommand(review.FaceId, targetProfileId, review.RowVersion);
            var result = await _decisionOperations.ConfirmFaceAsync(command).ConfigureAwait(true);

            if (result.IsSuccess && result.Value is { } outcome)
            {
                review.ApplyDecision(
                    outcome.DecisionState,
                    targetProfileId,
                    targetName ?? SurfaceText.Get("Faces.Label.AssignedProfile", "Assigned Profile"),
                    outcome.RowVersion);
                StatusMessage = SurfaceText.Get("Faces.Status.Assigned", "Face assigned to Profile.");
                CompleteResolvedReview(review);
            }
            else
            {
                StatusMessage = SurfaceText.Get(
                    "Faces.Status.AssignFailed",
                    "The face could not be assigned.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Face assignment failed: {0}", ex.GetType().Name);
            StatusMessage = SurfaceText.Get(
                "Faces.Status.AssignFailed",
                "The face could not be assigned.");
        }
    }

    /// <summary>
    /// Assign all unresolved faces in the scoped review dataset to its explicit Profile target.
    /// Uses bounded queue pages and FaceDecisionOperations — never raw SQL.
    /// </summary>
    private async Task AssignAllToThisProfileAsync()
    {
        if (!_profileId.HasValue || _profileId.Value == Guid.Empty || _decisionOperations is null)
        {
            return;
        }

        var targetProfileId = _profileId.Value;
        AssignAllRunning = true;
        var assigned = 0;
        var skipped = 0;

        try
        {
            while (!_disposed && IsRouteActive && !RouteCancellationToken.IsCancellationRequested)
            {
                // Read first page of unresolved scoped rows.
                IReadOnlyList<FaceReviewReadModel> page;
                if (_queueReads is not null)
                {
                    var queuePage = await _queueReads
                        .GetPageAsync(targetProfileId, QueuePageSize, cursor: null, RouteCancellationToken)
                        .ConfigureAwait(false);
                    page = queuePage.Items;
                }
                else if (_faceReads is not null)
                {
                    page = await _faceReads
                        .GetFaceReviewsForProfileAsync(targetProfileId, QueuePageSize, RouteCancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    break;
                }

                if (page.Count == 0)
                {
                    break;
                }

                var anySucceeded = false;
                foreach (var model in page)
                {
                    if (_disposed || !IsRouteActive || RouteCancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (model.DecisionState is not (FaceDecisionState.Unknown or FaceDecisionState.Suggested))
                    {
                        continue;
                    }

                    try
                    {
                        var command = new ConfirmFaceCommand(model.FaceId, targetProfileId, model.RowVersion);
                        var result = await _decisionOperations.ConfirmFaceAsync(command)
                            .ConfigureAwait(false);
                        if (result.IsSuccess)
                        {
                            assigned++;
                            anySucceeded = true;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("Batch face confirm failed for {0}: {1}", model.FaceId, ex.GetType().Name);
                        skipped++;
                    }
                }

                if (!anySucceeded)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Assign all faces failed: {0}", ex.GetType().Name);
        }
        finally
        {
            AssignAllRunning = false;
        }

        if (!_disposed && IsRouteActive)
        {
            StatusMessage = skipped > 0
                ? SurfaceText.Format("Faces.Status.AssignedAllPartial",
                    "Assigned {0} faces; {1} changed elsewhere and were skipped.", assigned, skipped)
                : SurfaceText.Format("Faces.Status.AssignedAll",
                    "Assigned {0} faces to this Profile.", assigned);

            await LoadReviewsAsync(RouteCancellationToken).ConfigureAwait(false);
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        if (_disposed)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Face:
                ScheduleRefresh();
                break;

            case CatalogInvalidationDomain.Profile:
                if (_profileId.HasValue
                    && (invalidation.EntityIds.Count == 0 || invalidation.EntityIds.Contains(_profileId.Value)))
                {
                    ScheduleRefresh();
                }
                break;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        UiDispatch.Run(() => Coalescer.Signal());
    }

    private RefreshCoalescer? _refreshCoalescer;

    private RefreshCoalescer Coalescer => _refreshCoalescer ??= new RefreshCoalescer(
        _ => _disposed ? Task.CompletedTask : LoadReviewsAsync(RouteCancellationToken),
        TimeSpan.FromMilliseconds(600));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCoalescer?.Dispose();
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }
    }
}

public sealed class FaceReviewItemViewModel : ObservableObject
{
    public const double FaceCropMarginFraction = 0.25;

    private FaceDecisionState _decisionState;
    private Guid? _suggestedProfileId;
    private string? _suggestedProfileDisplayName;
    private Guid? _confirmedProfileId;
    private string? _confirmedProfileDisplayName;
    private long _rowVersion;
    private FaceCandidateViewModel? _selectedCandidate;
    private string? _faceCropPath;

    public FaceReviewItemViewModel(FaceReviewReadModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _decisionState = model.DecisionState;
        _suggestedProfileId = model.SuggestedProfileId;
        _suggestedProfileDisplayName = model.SuggestedProfileDisplayName;
        _confirmedProfileId = model.ConfirmedProfileId;
        _confirmedProfileDisplayName = model.ConfirmedProfileDisplayName;
        _rowVersion = model.RowVersion;

        foreach (var candidate in model.Candidates)
        {
            Candidates.Add(new FaceCandidateViewModel(candidate));
        }

        _selectedCandidate = Candidates.FirstOrDefault();
    }

    public ObservableCollection<FaceCandidateViewModel> Candidates { get; } = [];

    public bool HasCandidates => Candidates.Count > 0;

    public FaceCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set => SetProperty(ref _selectedCandidate, value);
    }

    public FaceReviewReadModel Model { get; }

    public Guid FaceId => Model.FaceId;
    public Guid MediaId => Model.MediaId;
    public string DetectionKey => Model.DetectionKey;
    public string BoundingBoxJson => Model.BoundingBoxJson;
    public double? DetectionConfidence => Model.Confidence;
    public long RowVersion => _rowVersion;
    public long? SampledTimestampMilliseconds => Model.SampledTimestampMilliseconds;
    public bool IsVideoFrameDetection => Model.IsVideoFrameDetection;
    public string? SuggestionBankSignature => Model.SuggestionBankSignature;
    public FaceDecisionState DecisionState => _decisionState;
    public Guid? SuggestedProfileId => _suggestedProfileId;
    public string? SuggestedProfileDisplayName => _suggestedProfileDisplayName;
    public Guid? ConfirmedProfileId => _confirmedProfileId;
    public string? ConfirmedProfileDisplayName => _confirmedProfileDisplayName;
    public bool IsSuggested => _decisionState == FaceDecisionState.Suggested;
    public bool IsConfirmed => _decisionState == FaceDecisionState.Confirmed;
    public bool IsRejected => _decisionState == FaceDecisionState.Rejected;
    public bool IsUnresolved => _decisionState is FaceDecisionState.Unknown or FaceDecisionState.Suggested;

    public string? FaceCropPath
    {
        get => _faceCropPath;
        set
        {
            if (SetProperty(ref _faceCropPath, value))
            {
                RaisePropertyChanged(nameof(HasFaceCrop));
            }
        }
    }

    // FaceCropPath is resolved by the face-media pipeline before it reaches presentation state.
    // A property getter must remain a pure memory read; probing the file system here can stall
    // binding/layout on slow storage.
    public bool HasFaceCrop => !string.IsNullOrWhiteSpace(_faceCropPath);

    public string FaceCropFallbackText => SurfaceText.Get(
        "Faces.CropUnavailable",
        "Face preview unavailable");

    public static string? ResolveFaceCropPath(string? cacheRoot, Guid faceId)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || faceId == Guid.Empty)
        {
            return null;
        }

        var dir = Path.Combine(cacheRoot, "face-crops", faceId.ToString("N"));
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(dir).FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    public string StatusBadgeText => _decisionState switch
    {
        FaceDecisionState.Confirmed => SurfaceText.Get("Faces.Badge.Confirmed", "Confirmed"),
        FaceDecisionState.Suggested => SurfaceText.Get("Faces.Badge.Suggested", "Suggested (Non-authoritative)"),
        FaceDecisionState.Rejected => SurfaceText.Get("Faces.Badge.Rejected", "Rejected"),
        _ => SurfaceText.Get("Faces.Badge.Unresolved", "Unresolved")
    };

    public string DetectionConfidenceText => DetectionConfidence.HasValue
        ? string.Create(CultureInfo.CurrentCulture, $"YuNet {DetectionConfidence.Value:P0}")
        : SurfaceText.Get("Faces.YuNetUnavailable", "YuNet score unavailable");

    public string DetectionConfidenceNotice => DetectionConfidence.HasValue
        ? SurfaceText.Format(
            "Faces.DetectionConfidence",
            "Face detection confidence {0}. It says a face is here, not who it is.",
            DetectionConfidence.Value.ToString("P0", CultureInfo.CurrentCulture))
        : SurfaceText.Get(
            "Faces.DetectionConfidenceMissing",
            "No face-detection confidence was recorded.");

    public string? SampledTimestampText
    {
        get
        {
            if (Model.SampledTimestampMilliseconds is not { } milliseconds)
            {
                return null;
            }

            var span = TimeSpan.FromMilliseconds(milliseconds);
            return SurfaceText.Format(
                "Faces.FrameAt",
                "Frame at {0}",
                span.ToString(@"mm\:ss\.fff", CultureInfo.CurrentCulture));
        }
    }

    public string CandidateSummaryText => Candidates.Count switch
    {
        0 => SurfaceText.Get("Faces.Identity.None", "No Identity suggestion"),
        1 => SurfaceText.Get("Faces.Identity.One", "1 Identity suggestion"),
        var count => SurfaceText.Format("Faces.Identity.Many", "{0} Identity suggestions", count),
    };

    public string TargetProfileDisplayName =>
        ConfirmedProfileDisplayName
        ?? SuggestedProfileDisplayName
        ?? SurfaceText.Get("Faces.Unassigned", "Unassigned");

    public void ApplyDecision(
        FaceDecisionState newState,
        Guid? targetProfileId,
        string? targetProfileName,
        long newRowVersion)
    {
        _decisionState = newState;
        _rowVersion = newRowVersion;

        if (newState == FaceDecisionState.Confirmed)
        {
            _confirmedProfileId = targetProfileId;
            _confirmedProfileDisplayName = targetProfileName;
        }
        else if (newState == FaceDecisionState.Rejected)
        {
            _confirmedProfileId = null;
            _confirmedProfileDisplayName = null;
        }

        RaisePropertyChanged(nameof(DecisionState));
        RaisePropertyChanged(nameof(ConfirmedProfileId));
        RaisePropertyChanged(nameof(ConfirmedProfileDisplayName));
        RaisePropertyChanged(nameof(IsSuggested));
        RaisePropertyChanged(nameof(IsConfirmed));
        RaisePropertyChanged(nameof(IsRejected));
        RaisePropertyChanged(nameof(IsUnresolved));
        RaisePropertyChanged(nameof(StatusBadgeText));
        RaisePropertyChanged(nameof(TargetProfileDisplayName));
    }
}

public sealed class FaceCandidateViewModel
{
    public FaceCandidateViewModel(FaceIdentityCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
    }

    public FaceIdentityCandidate Candidate { get; }

    public Guid IdentityId => Candidate.IdentityId;
    public Guid? ProfileId => Candidate.ProfileId;
    public string ProfileDisplayName =>
        Candidate.ProfileDisplayName ?? SurfaceText.Get("Faces.UnnamedProfile", "Unnamed Profile");
    public int Rank => Candidate.Rank;
    public double Similarity => Candidate.Similarity;
    public string RankText => string.Create(CultureInfo.CurrentCulture, $"#{Rank}");
    public string SimilarityText => string.Create(CultureInfo.CurrentCulture, $"{Similarity:0.00}");
    public string DisplayText =>
        string.Create(CultureInfo.CurrentCulture, $"#{Rank}  {ProfileDisplayName}  {Similarity:0.00}");
}
