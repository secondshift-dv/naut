using Neuterradise.App.SystemServices.Cache;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Neuterradise.App.Design.CoverFrames;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Faces;
using Neuterradise.App.Home;
using Neuterradise.App.Import;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Presentation;
using Neuterradise.App.RelatedProfiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.Trash;

namespace Neuterradise.App.Profiles;

public sealed record ProfileCategoryOption(string? CategoryId, string DisplayName);

public sealed record ProfileTagOption(string TagId, string DisplayName);

public sealed class ProfileDetailViewModel : ScreenStateViewModel, IDisposable
{
    public const int MaxMaterializedMedia = 200;

    private readonly CatalogDb? _catalog;
    private readonly NavigationCoordinator? _navigation;
    private readonly ProfileReads? _profileReads;
    private readonly ProfileOperations? _profileOperations;
    private readonly ProfileAppearanceOperations? _appearanceOperations;
    private readonly UnknownResolutionOperations? _unknownOperations;
    private readonly ImportAssignmentReviewReads? _assignmentReviewReads;
    private readonly RelatedReads? _relatedReads;
    private readonly FaceReads? _faceReads;
    private readonly FaceDecisionOperations? _faceDecisionOperations;
    private readonly MediaOperations? _mediaOperations;
    private readonly TrashOperations? _trashOperations;

    private bool _isDisposed;
    private ProfileDetailReadModel? _profile;
    private ProfilePresentationModel? _presentationModel;
    private IReadOnlyList<ProfileTagAssignment> _tagAssignments = [];
    private string _tagSearchText = string.Empty;
    private ProfileLayoutDefinition? _layoutDefinition;
    private string? _currentPresetId;
    private ProfileMediaFilter _selectedMediaFilter = ProfileMediaFilter.All;
    private FaceReviewItemViewModel? _selectedFace;

    private string _displayName = string.Empty;
    private ProfileKind _kind = ProfileKind.Normal;
    private long _unknownSequence;
    private long _activeOwnedMediaCount;
    private string? _categoryName;
    private string? _categoryId;
    private IReadOnlyList<string> _tags = [];
    private int? _rating;
    private bool _isFavorite;
    private string? _overview;
    private string? _notes;
    private Guid? _coverMediaId;
    private Guid? _bannerMediaId;
    private Guid? _figureMediaId;
    public string? FigureThumbnailRelativePath { get; private set; }
    public string? FigureModelRenderRelativePath { get; private set; }
    public MediaAssetState? FigureModelRenderState { get; private set; }
    public string? FigureSourceSha256 { get; private set; }
    private long _rowVersion;
    private Guid? _identityId;
    private int _identitySampleCount;
    private ManagedPathState _pathState = ManagedPathState.None;
    private string? _currentManagedRelativePath;

    private string? _folderStatusMessage;
    private string? _renameStatusMessage;
    private string? _unknownStatusNotice;
    private bool _isRenaming;
    private bool _isEditingMetadata;
    private string _editDisplayName = string.Empty;
    private string? _editCategoryId;
    private int? _editRating;
    private string? _editOverview;
    private bool _editIsFavorite;
    private MediaDetailViewModel? _inspector;
    private readonly Neuterradise.App.Media.Model.ModelPreviewAdapterRegistry? _modelAdapters;
    private readonly OverlayHostViewModel? _overlay;
    private string _defaultLayoutPresetId = ProfileLayoutResolver.FallbackPresetId;
    private CoverAppearance? _coverAppearance;
    private ImageRef? _coverSource;
    private string? _coverImagePath;
    private bool _reduceMotion;
    private ProfileAppearanceOverrides? _appearanceOverrides;
    private readonly MediaResourceAuthority? _mediaResources;
    private readonly ProfileRuntimeSnapshotCache? _runtimeSnapshots;
    private string? _bannerVideoPath;

    public ProfileDetailViewModel(Guid profileId)
        : this(profileId, catalog: null)
    {
    }

    public ProfileDetailViewModel(
        Guid profileId,
        CatalogDb? catalog = null,
        NavigationCoordinator? navigation = null,
        ProfileReads? profileReads = null,
        ProfileOperations? profileOperations = null,
        ProfileAppearanceOperations? appearanceOperations = null,
        UnknownResolutionOperations? unknownOperations = null,
        RelatedReads? relatedReads = null,
        FaceReads? faceReads = null,
        FaceDecisionOperations? faceDecisionOperations = null,
        string? defaultLayoutPresetId = null,
        OverlayHostViewModel? overlay = null,
        MediaOriginState? initialOrigin = null,
        Neuterradise.App.Media.Model.ModelPreviewAdapterRegistry? modelAdapters = null,
        Guid? initialInspectMediaId = null,
        MediaResourceAuthority? mediaResources = null,
        ProfileRuntimeSnapshotCache? runtimeSnapshots = null)
    {
        ProfileId = profileId;
        _catalog = catalog;
        _modelAdapters = modelAdapters;
        _navigation = navigation;
        _mediaResources = mediaResources;
        _runtimeSnapshots = runtimeSnapshots;
        _profileReads = profileReads ?? (_catalog is not null ? new ProfileReads(_catalog) : null);
        _profileOperations = profileOperations ?? (_catalog is not null ? new ProfileOperations(_catalog) : null);
        _appearanceOperations = appearanceOperations ?? (_catalog is not null ? new ProfileAppearanceOperations(_catalog) : null);
        _unknownOperations = unknownOperations ?? (_catalog is not null ? new UnknownResolutionOperations(_catalog) : null);
        _assignmentReviewReads = _catalog is not null ? new ImportAssignmentReviewReads(_catalog) : null;
        _relatedReads = relatedReads ?? _catalog?.RelatedReads;
        _faceReads = faceReads ?? (_catalog is not null ? new FaceReads(_catalog) : null);
        _faceDecisionOperations = faceDecisionOperations ?? (_catalog is not null ? new FaceDecisionOperations(_catalog) : null);
        _mediaOperations = _catalog is not null ? new MediaOperations(_catalog) : null;
        _trashOperations = _catalog is not null ? new TrashOperations(_catalog) : null;
        _defaultLayoutPresetId = defaultLayoutPresetId ?? ProfileLayoutResolver.FallbackPresetId;
        _overlay = overlay;

        MediaGrid = new MediaGridViewModel();
        if (initialOrigin is not null)
        {
            MediaGrid.RestoreState(initialOrigin);
        }
        MediaGrid.OpenMediaAction = assetId => TaskObserver.Observe(
            OpenMediaInDefaultAppAsync(assetId),
            "ProfileDetailViewModel.OpenMediaInDefaultAppAsync",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
        MediaGrid.InspectMediaAction = OpenInspector;
        MediaGrid.QueryChangedAction = OnMediaGridQueryChanged;
        MediaGrid.ReportRecoverableErrorAction = message => FolderStatusMessage = message;
        CloseInspectorCommand = new RelayCommand(_ => CloseInspector());

        if (_catalog is not null)
        {
            var assetWrites = _catalog.MediaWrites;
            MediaGrid.PersistFavoriteAction = (assetId, isFavorite) =>
                assetWrites.SetFavoriteAsync(assetId, isFavorite);
        }
        Related = new RelatedProfilesViewModel(
            _relatedReads,
            _catalog is not null ? new RelatedProfileOperations(_catalog) : null,
            ProfileId,
            _navigation,
            overlay: _overlay);

        GoBackCommand = new RelayCommand(_ => _navigation?.GoBack());
        OpenProfileFolderCommand = new AsyncRelayCommand(() => OpenProfileFolderAsync());

        OpenMediaDetailCommand = new RelayCommand(param =>
        {
            if (param is Guid assetId && assetId != Guid.Empty)
            {
                OpenMediaDetail(assetId);
            }
        });

        ConfirmFaceCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFace is { } face)
            {
                await ConfirmFaceAsync(face);
            }
        });

        RejectFaceCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFace is { } face)
            {
                await RejectFaceAsync(face);
            }
        });

        OpenFaceReviewWorkspaceCommand = new RelayCommand(_ =>
        {
            _navigation?.Navigate(new FaceReviewRoute(ProfileId));
        });

        var resolver = new ProfileLayoutResolver();
        var initialSelection = resolver.Resolve(_defaultLayoutPresetId, null);
        _layoutDefinition = initialSelection.Definition;
        _currentPresetId = initialSelection.Definition.Id;

        var defaultCoverRequest = new CoverAppearanceRequest();
        _coverAppearance = CoverFrameCatalog.Resolve(defaultCoverRequest, _reduceMotion).Appearance;

        OpenCustomizeOverlayCommand = new RelayCommand(_ => OpenCustomizeOverlay());
        ChangeFaceProfileCommand = new AsyncRelayCommand(async param =>
        {
            var face = param as FaceReviewItemViewModel ?? SelectedFace;
            if (face is not null)
            {
                await OpenChangeFaceProfilePickerAsync(face).ConfigureAwait(true);
            }
        });

        if (initialInspectMediaId is { } inspectMediaId && inspectMediaId != Guid.Empty)
        {
            OpenInspector(inspectMediaId);
        }

        if (_profileReads is not null)
        {
            if (_runtimeSnapshots?.TryGetReady(ProfileId, out var warmSnapshot) == true
                && warmSnapshot is not null)
            {
                var mediaApplied = ApplyRuntimeSnapshot(warmSnapshot);
                ShowReady();
                StartProgressiveProfileModules(RouteCancellationToken, includeMedia: !mediaApplied);
            }
            else if (_runtimeSnapshots is not null)
            {
                StartRouteTask(LoadFromRuntimeSnapshotAsync, "This profile could not be opened.");
            }
            else
            {
                StartRouteTask(LoadAsync, "This profile could not be opened.");
            }

            if (_catalog is not null)
            {
                _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
            }
        }
        else
        {
            ShowReady();
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        UiDispatch.Run(() => ApplyCatalogInvalidation(invalidation));
    }

    private void ApplyCatalogInvalidation(CatalogInvalidation invalidation)
    {
        if (_isDisposed || !IsRouteActive)
        {
            return;
        }

        switch (invalidation.DomainKind)
        {
            case CatalogInvalidationDomain.Appearance:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                SignalAppearance();
                break;
            case CatalogInvalidationDomain.Profile:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                SignalPage();
                break;
            case CatalogInvalidationDomain.Media:
                if (!IsMediaRelevant(invalidation.EntityIds))
                {
                    return;
                }
                SignalMedia();
                break;
            case CatalogInvalidationDomain.Face:
                if (invalidation.EntityIds.Count == 0 || Faces.Any(face => invalidation.EntityIds.Contains(face.FaceId)))
                {
                    FacesRefresh.Signal();
                }
                break;
            case CatalogInvalidationDomain.Related:
                if (invalidation.EntityIds.Count != 0 && !invalidation.EntityIds.Contains(ProfileId))
                {
                    return;
                }
                SignalRelated();
                break;
        }
    }

    private bool IsMediaRelevant(IReadOnlyList<Guid> entityIds)
    {
        if (entityIds.Count == 0)
        {
            return true;
        }

        foreach (var id in entityIds)
        {
            if (MediaItems.Any(item => item.MediaId == id))
            {
                return true;
            }

            if (_inspector is { } inspector && inspector.MediaId == id)
            {
                return true;
            }
        }

        return false;
    }

    private void SignalPage()
    {
        if (!_isDisposed && IsRouteActive)
        {
            PageRefresh.Signal();
        }
    }

    private void SignalAppearance()
    {
        if (!_isDisposed && IsRouteActive)
        {
            AppearanceRefresh.Signal();
        }
    }

    private void SignalMedia()
    {
        if (!_isDisposed && IsRouteActive)
        {
            MediaRefresh.Signal();
        }
    }

    private void SignalRelated()
    {
        if (!_isDisposed && IsRouteActive)
        {
            RelatedRefresh.Signal();
        }
    }

    private RefreshCoalescer? _pageRefresh;
    private RefreshCoalescer? _appearanceRefresh;
    private RefreshCoalescer? _mediaRefresh;
    private RefreshCoalescer? _relatedRefresh;
    private RefreshCoalescer? _facesRefresh;
    private long _loadGeneration;
    private long _mediaLoadGeneration;
    private long _assignmentReviewLoadGeneration;
    private long _faceLoadGeneration;
    private readonly SemaphoreSlim _profileTagCommitGate = new(1, 1);
    private readonly SemaphoreSlim _metadataVocabularyGate = new(1, 1);
    private Task _pendingProfileTagCommit = Task.CompletedTask;
    private bool _metadataVocabularyLoaded;

    private RefreshCoalescer PageRefresh => _pageRefresh ??= new RefreshCoalescer(
        ct =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                StartRouteTask(LoadAsync, "This profile could not be refreshed.");
            }

            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer AppearanceRefresh => _appearanceRefresh ??= new RefreshCoalescer(
        ct =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                StartRouteTask(RefreshAppearanceAsync, "Profile appearance could not be refreshed.");
            }

            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(250));

    public async Task RefreshAppearanceAsync(CancellationToken cancellationToken = default)
    {
        if (_profileReads is null) return;
        var appearance = await _profileReads.GetAppearanceAsync(ProfileId, cancellationToken).ConfigureAwait(false);
        if (appearance is null || cancellationToken.IsCancellationRequested) return;
        await UiDispatch.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested && !_isDisposed && IsRouteActive)
                ApplyAppearance(appearance);
        }).ConfigureAwait(false);
    }

    private RefreshCoalescer MediaRefresh => _mediaRefresh ??= new RefreshCoalescer(
        _ => _isDisposed || !IsRouteActive ? Task.CompletedTask : LoadMediaAsync(),
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer RelatedRefresh => _relatedRefresh ??= new RefreshCoalescer(
        _ => _isDisposed || !IsRouteActive ? Task.CompletedTask : LoadRelatedAsync(),
        TimeSpan.FromMilliseconds(500));

    private RefreshCoalescer FacesRefresh => _facesRefresh ??= new RefreshCoalescer(
        _ => _isDisposed || !IsRouteActive ? Task.CompletedTask : LoadFacesAsync(),
        TimeSpan.FromMilliseconds(250));

    private const int CoverDecodeWidth = 640;
    private const int TileDecodeWidth = 256;

    private void ApplyCoverImage(string? path, string? resourceKey = null)
    {
        CoverSource = ImageRef.FromPath(path, CoverDecodeWidth, resourceKey);
    }

    private string? ResolvePreparedMediaAssetPath(string? relativePath)
    {
        if (_catalog is null || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            return _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, relativePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Prepared MediaAsset path could not be resolved: {0}", ex.GetType().Name);
            return null;
        }
    }

    private string? ResolveMediaAssetPath(MediaAssetRecord? asset)
    {
        return asset is not null && asset.State == MediaAssetState.Ready
            ? ResolvePreparedMediaAssetPath(asset.RelativePath)
            : null;
    }

    private void ApplyAppearance(ProfileAppearanceReadModel appearance)
    {
        RowVersion = appearance.ProfileRowVersion;
        _coverMediaId = appearance.CoverMediaId;
        _bannerMediaId = appearance.BannerMediaId;
        _figureMediaId = appearance.FigureMediaId;
        FigureThumbnailRelativePath = appearance.FigureThumbnailRelativePath;
        FigureModelRenderRelativePath = appearance.FigureModelRenderRelativePath;
        FigureModelRenderState = appearance.FigureModelRenderState;
        FigureSourceSha256 = appearance.FigureSourceSha256;

        var resolver = new ProfileLayoutResolver();
        var selection = resolver.Resolve(_defaultLayoutPresetId, appearance.LayoutPresetId);
        LayoutDefinition = selection.Definition;
        CurrentPresetId = selection.Definition.Id;

        _appearanceOverrides = ProfileAppearanceOverrides.Parse(appearance.AppearanceOverridesJson);
        var overrides = _appearanceOverrides;

        CoverAppearance = CoverFrameCatalog.Resolve(
            overrides.ToCoverAppearanceRequest(),
            ReduceMotion).Appearance;

        CoverImagePath = ResolvePreparedMediaAssetPath(appearance.CoverAssetRelativePath);
        ApplyCoverImage(
            CoverImagePath,
            appearance.CoverMediaAssetId is { } coverAssetId ? $"artifact:{coverAssetId:N}" : null);
        BannerVideoPath = ResolvePreparedMediaAssetPath(appearance.BannerAssetRelativePath);

        RaisePropertyChanged(nameof(CoverMediaId));
        RaisePropertyChanged(nameof(BannerMediaId));
        RaisePropertyChanged(nameof(FigureMediaId));
        RaisePropertyChanged(nameof(FigureThumbnailRelativePath));
        RaisePropertyChanged(nameof(FigureModelRenderRelativePath));
        RaisePropertyChanged(nameof(FigureModelRenderState));
        RaisePropertyChanged(nameof(FigureSourceSha256));
        RaisePropertyChanged(nameof(AppearanceOverrides));
    }

    public bool CanSaveMetadata => !string.IsNullOrWhiteSpace(_editDisplayName);

    public bool HasUnsavedMetadataChanges => IsEditingMetadata;

    public bool IsEditingMetadata
    {
        get => _isEditingMetadata;
        set => SetProperty(ref _isEditingMetadata, value);
    }

    public string EditDisplayName
    {
        get => _editDisplayName;
        set
        {
            if (SetProperty(ref _editDisplayName, value))
            {
                RaisePropertyChanged(nameof(CanSaveMetadata));
            }
        }
    }

    public string? EditCategoryId
    {
        get => _editCategoryId;
        set => SetProperty(ref _editCategoryId, value);
    }

    public int? EditRating
    {
        get => _editRating;
        set => SetProperty(ref _editRating, value);
    }

    public string? EditOverview
    {
        get => _editOverview;
        set => SetProperty(ref _editOverview, value);
    }

    public bool EditIsFavorite
    {
        get => _editIsFavorite;
        set => SetProperty(ref _editIsFavorite, value);
    }

    public ObservableCollection<ProfileCategoryOption> AvailableCategories { get; } = [];
    public ObservableCollection<ProfileTagOption> AvailableTags { get; } = [];
    public ObservableCollection<ProfileTagOption> EditTags { get; } = [];

    public IReadOnlyList<ProfileTagOption> TagSearchResults
    {
        get
        {
            var assigned = EditTags.Select(static tag => tag.TagId).ToHashSet(StringComparer.Ordinal);
            var query = TagSearchText?.Trim() ?? string.Empty;
            var normalizedQuery = Settings.TaxonomyNamePolicy.TryNormalize(query);

            return
            [
                .. AvailableTags
                    .Where(option => !assigned.Contains(option.TagId))
                    .Where(option => normalizedQuery is null
                        || Settings.TaxonomyNamePolicy.TryNormalize(option.DisplayName) is { } nk
                           && nk.Contains(normalizedQuery, StringComparison.Ordinal))
                    .OrderBy(option =>
                    {
                        var nk = Settings.TaxonomyNamePolicy.TryNormalize(option.DisplayName);
                        return normalizedQuery is not null && nk is not null
                            && nk.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1;
                    })
                    .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(MaximumTagSuggestions),
            ];
        }
    }

    public const int MaximumTagSuggestions = 12;

    public string TagSearchText
    {
        get => _tagSearchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _tagSearchText, next))
            {
                if (next.Contains(',') || next.Contains('\n') || next.Contains('\r'))
                {
                    var task = CommitTagTokensAsync();
                    _pendingProfileTagCommit = task;
                    TaskObserver.Observe(task, "ProfileDetailViewModel.CommitTagTokensAsync");
                    return;
                }
                RaisePropertyChanged(nameof(TagSearchResults));
            }
        }
    }

    public Task CommitTagTokensAsync() => CommitProfileTagTokensCoreAsync(isPartial: true);

    public Task OnTagInputEnterAsync() => CommitProfileTagTokensCoreAsync(isPartial: false);

    private async Task CommitProfileTagTokensCoreAsync(bool isPartial)
    {
        await _profileTagCommitGate.WaitAsync(RouteCancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<Settings.TaxonomyNamePolicy.TaxonomyTagToken> tokens = [];
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                if (isPartial)
                {
                    var (committed, remaining) = Settings.TaxonomyNamePolicy.TryParsePartialTagInput(_tagSearchText);
                    tokens = committed;
                    _tagSearchText = remaining ?? string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
                else
                {
                    var remaining = _tagSearchText.Trim();
                    if (string.IsNullOrWhiteSpace(remaining))
                    {
                        tokens = [];
                        return;
                    }
                    tokens = Settings.TaxonomyNamePolicy.ParseTagTokens(remaining);
                    _tagSearchText = string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
            }).ConfigureAwait(false);

            foreach (var token in tokens)
            {
                await EnsureEditTagAsync(token.CanonicalName, token.DisplayName).ConfigureAwait(false);
            }

            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive)
                {
                    RaisePropertyChanged(nameof(TagSearchResults));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _profileTagCommitGate.Release();
        }
    }

    private async Task EnsureEditTagAsync(string canonicalName, string displayName)
    {
        var alreadyAssigned = false;
        ProfileTagOption? existing = null;
        await UiDispatch.InvokeAsync(() =>
        {
            if (_isDisposed || !IsRouteActive)
            {
                return;
            }

            alreadyAssigned = EditTags.Any(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
            existing = AvailableTags.FirstOrDefault(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
        }).ConfigureAwait(false);

        if (_isDisposed || !IsRouteActive || alreadyAssigned)
        {
            return;
        }

        if (existing is not null)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && IsRouteActive)
                {
                    AddEditTag(existing);
                }
            }).ConfigureAwait(false);
            return;
        }

        if (_catalog is null)
        {
            return;
        }

        try
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            await _catalog.SettingsWrites.CreateTagAsync(id, displayName, RouteCancellationToken).ConfigureAwait(false);
            var option = new ProfileTagOption(id, displayName);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                AvailableTags.Add(option);
                AddEditTag(option);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var vocabulary = await _catalog.SettingsReads.GetAllTagsAsync(RouteCancellationToken).ConfigureAwait(false);
            var resolvedDuplicate = false;
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                PopulateAvailableTags(vocabulary.Select(tag => new ProfileTagOption(tag.TagId, tag.Name)));
                var match = AvailableTags.FirstOrDefault(tag => Settings.TaxonomyNamePolicy.AreSameName(tag.DisplayName, canonicalName));
                if (match is not null)
                {
                    AddEditTag(match);
                    resolvedDuplicate = true;
                }
            }).ConfigureAwait(false);

            if (!resolvedDuplicate && !_isDisposed && IsRouteActive)
            {
                throw;
            }
        }
    }

    public async Task CommitPendingProfileTagsForSaveAsync()
    {
        await _pendingProfileTagCommit.ConfigureAwait(false);

        var hasPendingText = false;
        await UiDispatch.InvokeAsync(() =>
        {
            hasPendingText = !_isDisposed && IsRouteActive && !string.IsNullOrWhiteSpace(_tagSearchText);
        }).ConfigureAwait(false);

        if (!hasPendingText)
        {
            return;
        }

        var task = CommitProfileTagTokensCoreAsync(isPartial: false);
        _pendingProfileTagCommit = task;
        await task.ConfigureAwait(false);
    }

    public void AddEditTag(ProfileTagOption? option)
    {
        if (option is null || EditTags.Any(tag => string.Equals(tag.TagId, option.TagId, StringComparison.Ordinal)))
        {
            return;
        }

        EditTags.Add(option);
        TagSearchText = string.Empty;
        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public void RemoveEditTag(ProfileTagOption? option)
    {
        if (option is null)
        {
            return;
        }

        var existing = EditTags.FirstOrDefault(
            tag => string.Equals(tag.TagId, option.TagId, StringComparison.Ordinal));
        if (existing is not null)
        {
            EditTags.Remove(existing);
            RaisePropertyChanged(nameof(TagSearchResults));
        }
    }

    public void PopulateAvailableTags(IEnumerable<ProfileTagOption> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        AvailableTags.Clear();
        foreach (var tag in tags)
        {
            AvailableTags.Add(tag);
        }

        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public AsyncRelayCommand ChangeFaceProfileCommand { get; }

    public Guid ProfileId { get; }
    public Guid? CoverMediaId => _coverMediaId;
    public Guid? BannerMediaId => _bannerMediaId;
    public Guid? FigureMediaId => _figureMediaId;

    public long RowVersion
    {
        get => _rowVersion;
        private set
        {
            if (_rowVersion != value)
            {
                _rowVersion = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileDetailReadModel? Profile => _profile;
    public ProfilePresentationModel? PresentationModel => _presentationModel;
    public ProfileLayoutDefinition? LayoutDefinition
    {
        get => _layoutDefinition;
        private set
        {
            if (_layoutDefinition != value)
            {
                _layoutDefinition = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CurrentPresetId
    {
        get => _currentPresetId;
        private set
        {
            if (_currentPresetId != value)
            {
                _currentPresetId = value;
                RaisePropertyChanged();
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName != value)
            {
                _displayName = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileKind Kind
    {
        get => _kind;
        private set
        {
            if (_kind != value)
            {
                _kind = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsUnknownProfile));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
            }
        }
    }

    public bool IsUnknownProfile => Kind == ProfileKind.Unknown;

    public long UnknownSequence
    {
        get => _unknownSequence;
        private set
        {
            if (_unknownSequence != value)
            {
                _unknownSequence = value;
                RaisePropertyChanged();
            }
        }
    }

    public long ActiveOwnedMediaCount
    {
        get => _activeOwnedMediaCount;
        private set
        {
            if (_activeOwnedMediaCount != value)
            {
                _activeOwnedMediaCount = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsActiveUnresolved));
            }
        }
    }

    public bool IsActiveUnresolved => IsUnknownProfile && UnknownProfileRules.IsActiveUnresolved(ActiveOwnedMediaCount);

    public string? CategoryName
    {
        get => _categoryName;
        private set
        {
            if (_categoryName != value)
            {
                _categoryName = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CategoryId => _categoryId;
    public IReadOnlyList<string> Tags => _tags;

    public int? Rating
    {
        get => _rating;
        private set
        {
            if (_rating != value)
            {
                _rating = value;
                RaisePropertyChanged();
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                RaisePropertyChanged();
            }
        }
    }

    public bool HasMediaSelection => MediaGrid.Selection.SelectedMediaId.HasValue;

    public string? Overview
    {
        get => _overview;
        private set
        {
            if (_overview != value)
            {
                _overview = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? Notes
    {
        get => _notes;
        private set
        {
            if (_notes != value)
            {
                _notes = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasNotes));
            }
        }
    }

    public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);

    public MediaDetailViewModel? Inspector
    {
        get => _inspector;
        private set
        {
            if (!ReferenceEquals(_inspector, value))
            {
                _inspector = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsInspectorOpen));
            }
        }
    }

    public bool IsInspectorOpen => _inspector is not null;
    public ICommand CloseInspectorCommand { get; }
    public Guid? IdentityId => _identityId;
    public int IdentitySampleCount => _identitySampleCount;

    public ManagedPathState PathState
    {
        get => _pathState;
        private set
        {
            if (_pathState != value)
            {
                _pathState = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsPathNeedsAttention));
                RaisePropertyChanged(nameof(IsPathPending));
            }
        }
    }

    public bool IsPathNeedsAttention => _pathState == ManagedPathState.NeedsAttention;
    public bool IsPathPending => _pathState == ManagedPathState.Pending;
    public string? CurrentManagedRelativePath => _currentManagedRelativePath;

    public string? FolderStatusMessage
    {
        get => _folderStatusMessage;
        private set
        {
            if (_folderStatusMessage != value)
            {
                _folderStatusMessage = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? RenameStatusMessage
    {
        get => _renameStatusMessage;
        private set
        {
            if (_renameStatusMessage != value)
            {
                _renameStatusMessage = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? UnknownStatusNotice
    {
        get => _unknownStatusNotice;
        private set
        {
            if (_unknownStatusNotice != value)
            {
                _unknownStatusNotice = value;
                RaisePropertyChanged();
            }
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (_isRenaming != value)
            {
                _isRenaming = value;
                RaisePropertyChanged();
            }
        }
    }

    public ProfileMediaFilter SelectedMediaFilter
    {
        get => _selectedMediaFilter;
        set
        {
            if (_selectedMediaFilter != value)
            {
                _selectedMediaFilter = value;
                RaisePropertyChanged();
                var gridFilter = value switch
                {
                    ProfileMediaFilter.Owned => MediaRelationFilter.Owned,
                    ProfileMediaFilter.AppearsIn => MediaRelationFilter.AppearsIn,
                    ProfileMediaFilter.Manual => MediaRelationFilter.Manual,
                    _ => MediaRelationFilter.All,
                };
                if (MediaGrid.RelationFilter != gridFilter)
                {
                    MediaGrid.RelationFilter = gridFilter;
                    return;
                }
                TaskObserver.Observe(LoadMediaAsync(), "ProfileDetailViewModel.LoadMediaAsync");
            }
        }
    }

    public ObservableCollection<ProfileMediaItemViewModel> MediaItems { get; } = [];
    public ObservableCollection<ProfileMediaItemViewModel> RecentMedia { get; } = [];
    public ObservableCollection<ImportAssignmentReviewCluster> AssignmentReviewClusters { get; } = [];
    public MediaGridViewModel MediaGrid { get; }
    public RelatedProfilesViewModel Related { get; }
    public ObservableCollection<RelatedProfileSummaryReadModel> RelatedProfiles => Related.RelatedProfiles;
    public ObservableCollection<FaceReviewItemViewModel> Faces { get; } = [];

    public FaceReviewItemViewModel? SelectedFace
    {
        get => _selectedFace;
        set
        {
            if (_selectedFace != value)
            {
                _selectedFace = value;
                RaisePropertyChanged();
            }
        }
    }

    public IReadOnlyList<Guid> SelectedUnknownMediaIds => MediaItems.Where(i => i.IsSelected).Select(i => i.MediaId).ToList();
    public bool HasMedia => MediaItems.Count > 0;
    public bool HasRelatedProfiles => RelatedProfiles.Count > 0;
    public bool HasFaces => Faces.Count > 0;
    public int UnresolvedFaceCount => Faces.Count(f => f.IsUnresolved);

    public ICommand GoBackCommand { get; }
    public ICommand OpenProfileFolderCommand { get; }
    public ICommand OpenMediaDetailCommand { get; }
    public ICommand ConfirmFaceCommand { get; }
    public ICommand RejectFaceCommand { get; }
    public ICommand OpenFaceReviewWorkspaceCommand { get; }
    public ICommand OpenCustomizeOverlayCommand { get; }
    public ProfileAppearanceOverrides? AppearanceOverrides => _appearanceOverrides;

    public CoverAppearance? CoverAppearance
    {
        get => _coverAppearance;
        private set
        {
            if (_coverAppearance != value)
            {
                _coverAppearance = value;
                RaisePropertyChanged();
            }
        }
    }


    public ImageRef? CoverSource
    {
        get => _coverSource;
        private set
        {
            if (_coverSource != value)
            {
                _coverSource = value;
                RaisePropertyChanged();
            }
        }
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        private set
        {
            if (_coverImagePath != value)
            {
                _coverImagePath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasCoverImage));
            }
        }
    }

    public bool HasCoverImage => !string.IsNullOrWhiteSpace(CoverImagePath);

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion != value)
            {
                _reduceMotion = value;
                RaisePropertyChanged();
                if (_coverAppearance is not null)
                {
                    var coverReq = new CoverAppearanceRequest(_appearanceOverrides?.CoverShape, _appearanceOverrides?.CoverFrameId);
                    CoverAppearance = CoverFrameCatalog.Resolve(coverReq, value).Appearance;
                }
            }
        }
    }

    public IReadOnlyList<string> AvailableCoverShapes { get; } =
        ["rounded-square", "circle"];

    public IReadOnlyList<string> AvailableCoverFrames { get; } = CoverFrameCatalog.Frames.Select(f => f.Id).ToList();

    public void PopulateProfile(ProfileDetailReadModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        DisplayName = profile.DisplayName;
        Kind = profile.Kind;
        UnknownSequence = profile.UnknownSequence ?? 0;
        ActiveOwnedMediaCount = profile.ActiveOwnedMediaCount;
        CategoryName = profile.CategoryName;
        _categoryId = profile.CategoryId;
        _tags = profile.Tags ?? [];
        _tagAssignments = profile.AssignedTags;
        Rating = profile.Rating;
        IsFavorite = profile.IsFavorite;
        Overview = profile.Overview;
        Notes = profile.Notes;
        _identityId = profile.IdentityId;
        _identitySampleCount = profile.IdentitySampleCount;
        RaisePropertyChanged(nameof(IdentityId));
        RaisePropertyChanged(nameof(IdentitySampleCount));

        ApplyAppearance(new ProfileAppearanceReadModel(
            profile.ProfileId,
            profile.RowVersion,
            profile.LayoutPresetId,
            profile.AppearanceOverridesJson,
            profile.CoverMediaId,
            profile.BannerMediaId,
            profile.BannerMediaType,
            profile.CoverMediaAssetId,
            profile.CoverAssetRelativePath,
            profile.BannerMediaAssetId,
            profile.BannerAssetRelativePath, profile.FigureMediaId,profile.FigureThumbnailRelativePath,
            profile.FigureModelRenderRelativePath,profile.FigureModelRenderState,profile.FigureSourceSha256));

        var appearanceOverrides = _appearanceOverrides ?? ProfileAppearanceOverrides.Default;

        _presentationModel = new ProfilePresentationModel(
            ProfileId: profile.ProfileId,
            DisplayName: profile.DisplayName,
            CategoryName: profile.CategoryName,
            Tags: profile.Tags,
            Rating: profile.Rating,
            IsFavorite: profile.IsFavorite,
            Overview: profile.Overview,
            Notes: profile.Notes,
            MediaCount: profile.ActiveOwnedMediaCount,
            HasRelatedIndicator: RelatedProfiles.Count > 0,
            CardVariantId: appearanceOverrides.ProfileCardVariantId);

        RaisePropertyChanged(nameof(PresentationModel));
    }

    public void PopulateMedia(IEnumerable<ProfileMediaItemReadModel> items, int? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        MediaItems.Clear();
        RecentMedia.Clear();

        var count = 0;
        var gridItems = new List<MediaGridItem>();
        foreach (var item in items)
        {
            if (count >= MaxMaterializedMedia)
            {
                break;
            }

            var vm = new ProfileMediaItemViewModel(item);
            MediaItems.Add(vm);
            var thumbnailPath = ResolvePreparedMediaAssetPath(item.ThumbnailRelativePath);
            var hoverPath = item.MediaType == MediaType.Video
                ? ResolvePreparedMediaAssetPath(item.HoverRelativePath)
                : null;
            gridItems.Add(new MediaGridItem(
                item.MediaId,
                item.MediaType,
                item.RelationType switch
                {
                    ProfileMediaRelation.Owner => MediaRelationBadge.Owned,
                    ProfileMediaRelation.Appears => MediaRelationBadge.AppearsIn,
                    _ => MediaRelationBadge.Manual,
                },
                item.ManagedFileName,
                item.PixelWidth,
                item.PixelHeight,
                item.DurationMs,
                item.PathState == ManagedPathState.NeedsAttention,
                item.IsFavorite,
                item.OriginalFileName,
                thumbnailPath,
                hoverPath));
            if (count < 12)
            {
                RecentMedia.Add(vm);
            }

            count++;
        }

        MediaGrid.SetItems(gridItems, totalCount: totalCount);
        foreach (var card in MediaGrid.Cards)
        {
            if (!string.IsNullOrWhiteSpace(card.ThumbnailPath))
            {
                card.ThumbnailSource = ImageRef.FromPath(card.ThumbnailPath, TileDecodeWidth);
            }
        }
        RaisePropertyChanged(nameof(HasMedia));
    }

    public void PopulateRelated(IEnumerable<RelatedProfileSummaryReadModel> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        Related.Populate(summaries);
        RaisePropertyChanged(nameof(HasRelatedProfiles));
    }

    public void PopulateFaces(IEnumerable<FaceReviewReadModel> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        Faces.Clear();
        foreach (var f in faces)
        {
            var item = new FaceReviewItemViewModel(f);
            if (_catalog?.Paths.CachePath is { } cachePath)
            {
                item.FaceCropPath = FaceReviewItemViewModel.ResolveFaceCropPath(cachePath, f.FaceId);
            }

            Faces.Add(item);
            EnsureFaceCrop(item);
        }

        SelectedFace = Faces.FirstOrDefault(face => face.FaceId == SelectedFace?.FaceId)
            ?? Faces.FirstOrDefault();

        RaisePropertyChanged(nameof(HasFaces));
        RaisePropertyChanged(nameof(UnresolvedFaceCount));
    }

    private bool ApplyRuntimeSnapshot(ProfileRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _defaultLayoutPresetId = snapshot.DefaultLayoutPresetId;
        PopulateProfile(snapshot.Detail);
        ApplyFolder(snapshot.Folder);

        if (!UsesInitialMediaQuery())
        {
            return false;
        }

        PopulateMedia(snapshot.InitialMediaPage.Items, snapshot.InitialMediaPage.TotalCount);
        return true;
    }

    private void ApplyFolder(ProfileFolderReadModel? folder)
    {
        if (folder is null)
        {
            return;
        }

        PathState = folder.PathState;
        _currentManagedRelativePath = folder.CurrentManagedRelativePath;
        IsRenaming = folder.PathState == ManagedPathState.Pending;
        RenameStatusMessage = IsRenaming
            ? "Renaming files... (reconciliation pending)"
            : null;
    }

    private bool UsesInitialMediaQuery() =>
        MediaGrid.RelationFilter == MediaRelationFilter.All
        && MediaGrid.TypeFilter == MediaTypeFilter.All
        && MediaGrid.Sort == MediaGridSort.NewestFirst
        && !MediaGrid.IsFavoriteOnly
        && MediaGrid.CurrentPage == 1
        && MediaGrid.PageSize == MediaGridViewModel.DefaultPageSize;

    private async Task LoadFromRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        await UiDispatch.InvokeAsync(() =>
        {
            if (generation == _loadGeneration && !_isDisposed && IsRouteActive)
            {
                ShowLoading();
            }
        }).ConfigureAwait(false);

        var snapshot = await _runtimeSnapshots!.WarmAsync(ProfileId, cancellationToken).ConfigureAwait(false);
        if (generation != _loadGeneration || _isDisposed || !IsRouteActive)
        {
            return;
        }

        if (snapshot is null)
        {
            await UiDispatch.InvokeAsync(() => ShowCriticalError("Profile not found.")).ConfigureAwait(false);
            return;
        }

        var mediaApplied = false;
        await UiDispatch.InvokeAsync(() =>
        {
            if (generation != _loadGeneration || _isDisposed || !IsRouteActive)
            {
                return;
            }

            mediaApplied = ApplyRuntimeSnapshot(snapshot);
            ShowReady();
        }).ConfigureAwait(false);

        if (generation == _loadGeneration && !_isDisposed && IsRouteActive)
        {
            StartProgressiveProfileModules(cancellationToken, includeMedia: !mediaApplied);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_profileReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        await UiDispatch.InvokeAsync(() =>
        {
            if (generation == _loadGeneration && !_isDisposed && IsRouteActive)
            {
                ShowLoading();
            }
        }).ConfigureAwait(false);

        if (generation != _loadGeneration || _isDisposed || !IsRouteActive)
        {
            return;
        }

        try
        {
            var detailTask = _profileReads.GetDetailAsync(ProfileId, cancellationToken);
            var folderTask = _profileReads.GetFolderAsync(ProfileId, cancellationToken);

            await Task.WhenAll(detailTask, folderTask).ConfigureAwait(false);

            if (generation != _loadGeneration || _isDisposed || !IsRouteActive)
            {
                return;
            }

            var detail = detailTask.Result;
            var folder = folderTask.Result;
            var applySucceeded = false;

            await UiDispatch.InvokeAsync(() =>
            {
                if (generation != _loadGeneration || _isDisposed || !IsRouteActive)
                {
                    return;
                }

                if (detail is null)
                {
                    ShowCriticalError("Profile not found.");
                    return;
                }

                PopulateProfile(detail);
                ApplyFolder(folder);

                // Cold recovery path only. Warm navigation supplies the initial MediaGrid page
                // before activation; related Profiles, faces and review modules remain progressive.
                ShowReady();
                applySucceeded = true;
            }).ConfigureAwait(false);

            if (!applySucceeded || generation != _loadGeneration || _isDisposed || !IsRouteActive)
            {
                return;
            }

            StartProgressiveProfileModules(cancellationToken, includeMedia: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsRouteActive)
        {
        }
        catch (Exception ex)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation == _loadGeneration && !_isDisposed && IsRouteActive)
                {
                    ShowRecoverableError(OperationExecution.SafeMessage(ex));
                }
            }).ConfigureAwait(false);
        }
    }

    private void StartProgressiveProfileModules(
        CancellationToken cancellationToken,
        bool includeMedia)
    {
        if (includeMedia)
        {
            TaskObserver.Observe(
                LoadMediaAsync(cancellationToken),
                "Profile media",
                exception => Trace.TraceWarning("Profile media load failed: {0}", exception));
        }
        TaskObserver.Observe(
            LoadAssignmentReviewClustersAsync(cancellationToken),
            "Profile assignment review",
            exception => Trace.TraceWarning("Profile assignment review unavailable: {0}", exception.Message));
        TaskObserver.Observe(
            LoadRelatedAsync(cancellationToken),
            "Profile related",
            exception => Trace.TraceWarning("Related Profiles unavailable: {0}", exception.Message));
        TaskObserver.Observe(
            LoadFacesAsync(cancellationToken),
            "Profile faces",
            exception => Trace.TraceWarning("Profile faces unavailable: {0}", exception.Message));
    }

    public async Task LoadMediaAsync(CancellationToken cancellationToken = default)
    {
        if (_profileReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _mediaLoadGeneration);
        ProfileMediaFilter relationFilter = default;
        MediaType? typeFilter = null;
        var pageSize = 50;
        var isFavoriteOnly = false;
        MediaGridSort sort = default;
        var currentPage = 1;
        var queryCaptured = false;

        try
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation != _mediaLoadGeneration || _isDisposed || !IsRouteActive)
                {
                    return;
                }

                relationFilter = MediaGrid.RelationFilter switch
                {
                    MediaRelationFilter.Owned => ProfileMediaFilter.Owned,
                    MediaRelationFilter.AppearsIn => ProfileMediaFilter.AppearsIn,
                    MediaRelationFilter.Manual => ProfileMediaFilter.Manual,
                    _ => ProfileMediaFilter.All,
                };
                typeFilter = MediaGrid.TypeFilter switch
                {
                    MediaTypeFilter.Images => MediaType.Image,
                    MediaTypeFilter.Videos => MediaType.Video,
                    MediaTypeFilter.Models => MediaType.Model,
                    _ => null,
                };
                pageSize = MediaGrid.PageSize;
                isFavoriteOnly = MediaGrid.IsFavoriteOnly;
                sort = MediaGrid.Sort;
                currentPage = MediaGrid.CurrentPage;
                MediaGrid.State = MediaGridState.Loading;
                queryCaptured = true;
            }).ConfigureAwait(false);

            if (!queryCaptured || generation != _mediaLoadGeneration || _isDisposed || !IsRouteActive)
            {
                return;
            }

            var page = await _profileReads.GetMediaPageAsync(
                ProfileId,
                relationFilter,
                typeFilter,
                pageSize: pageSize,
                cancellationToken: cancellationToken,
                isFavoriteOnly: isFavoriteOnly,
                sort: sort,
                pageIndex: currentPage).ConfigureAwait(false);

            await UiDispatch.InvokeAsync(() =>
            {
                if (generation != _mediaLoadGeneration || _isDisposed || !IsRouteActive)
                {
                    return;
                }

                try
                {
                    PopulateMedia(page.Items, page.TotalCount);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning("Profile media presentation failed: {0}", exception);
                    MediaGrid.State = MediaGridState.RecoverablePreviewFailure;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsRouteActive)
        {
        }
        catch (Exception ex)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation == _mediaLoadGeneration && !_isDisposed && IsRouteActive)
                {
                    Trace.TraceWarning("Profile media query failed: {0}", ex);
                    MediaGrid.State = MediaGridState.RecoverableQueryError;
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task LoadAssignmentReviewClustersAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _assignmentReviewLoadGeneration);
        if (!IsUnknownProfile || _assignmentReviewReads is null)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation == _assignmentReviewLoadGeneration && !_isDisposed && IsRouteActive)
                {
                    AssignmentReviewClusters.Clear();
                }
            }).ConfigureAwait(false);
            return;
        }

        var clusters = await _assignmentReviewReads.GetPendingForUnknownProfileAsync(ProfileId, cancellationToken).ConfigureAwait(false);
        await UiDispatch.InvokeAsync(() =>
        {
            if (generation != _assignmentReviewLoadGeneration || _isDisposed || !IsRouteActive)
            {
                return;
            }

            AssignmentReviewClusters.Clear();
            foreach (var cluster in clusters)
            {
                AssignmentReviewClusters.Add(cluster);
            }
        }).ConfigureAwait(false);
    }

    public async Task AcceptAssignmentClusterAsync(ImportAssignmentReviewCluster cluster, Guid destinationProfileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_unknownOperations is null)
        {
            return;
        }

        var request = new AssignUnknownMediasToProfileRequest(
            ProfileId,
            [],
            destinationProfileId,
            ExpectedUnknownRowVersion: RowVersion,
            AssignmentClusterId: cluster.ClusterId,
            ExpectedAssignmentClusterRowVersion: cluster.RowVersion);
        var result = await _unknownOperations.AssignUnknownMediasToProfileAsync(request, cancellationToken).ConfigureAwait(true);
        UnknownStatusNotice = result.IsSuccess
            ? SurfaceText.Format("Assignment.Cluster.Accepted", "Assigned {0} grouped media items.", cluster.MemberCount)
            : result.UserMessage ?? SurfaceText.Get("Assignment.Cluster.Failed", "The grouped assignment could not be applied.");
        if (result.IsSuccess)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task KeepAssignmentClusterUnknownAsync(ImportAssignmentReviewCluster cluster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_unknownOperations is null)
        {
            return;
        }

        var result = await _unknownOperations.KeepAssignmentClusterUnknownAsync(ProfileId, cluster.ClusterId, cluster.RowVersion, cancellationToken).ConfigureAwait(true);
        UnknownStatusNotice = result.IsSuccess
            ? SurfaceText.Get("Assignment.Cluster.KeptUnknown", "Media remains safely unassigned.")
            : result.UserMessage ?? SurfaceText.Get("Assignment.Cluster.Failed", "The grouped assignment could not be applied.");
        if (result.IsSuccess)
        {
            await LoadAssignmentReviewClustersAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task OpenAssignmentClusterPickerAsync(ImportAssignmentReviewCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (_overlay is null || _profileReads is null)
        {
            return;
        }

        IReadOnlyList<ProfilePickerItem> candidates = [];
        if (_catalog is not null)
        {
            candidates = await new ProfilePickerReads(_catalog).GetAllCandidatesAsync().ConfigureAwait(true);
        }
        _overlay.Push(new ProfilePickerOverlayRequest(
            candidates,
            picked => TaskObserver.Observe(AcceptAssignmentClusterAsync(cluster, picked.ProfileId), "ProfileDetailViewModel.AcceptAssignmentClusterAsync"),
            title: SurfaceText.Get("Assignment.Cluster.ChooseTitle", "Assign grouped media"),
            prompt: SurfaceText.Get("Assignment.Cluster.ChoosePrompt", "Choose the Profile for this evidence group:")));
    }

    private void OnMediaGridQueryChanged()
    {
        _selectedMediaFilter = MediaGrid.RelationFilter switch
        {
            MediaRelationFilter.Owned => ProfileMediaFilter.Owned,
            MediaRelationFilter.AppearsIn => ProfileMediaFilter.AppearsIn,
            MediaRelationFilter.Manual => ProfileMediaFilter.Manual,
            _ => ProfileMediaFilter.All,
        };
        RaisePropertyChanged(nameof(SelectedMediaFilter));
        CloseInspector();
        TaskObserver.Observe(
            LoadMediaAsync(),
            "Reloading profile media",
            exception => Trace.TraceWarning("Profile media reload failed: {0}", exception));
    }

    public async Task LoadRelatedAsync(CancellationToken cancellationToken = default)
    {
        await Related.LoadRelatedProfilesAsync(cancellationToken).ConfigureAwait(false);
        await UiDispatch.InvokeAsync(() =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                RaisePropertyChanged(nameof(HasRelatedProfiles));
            }
        }).ConfigureAwait(false);
    }

    public async Task LoadFacesAsync(CancellationToken cancellationToken = default)
    {
        if (_faceReads is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _faceLoadGeneration);
        try
        {
            var reviews = await _faceReads.GetFaceReviewsForProfileAsync(ProfileId, 50, cancellationToken).ConfigureAwait(false);
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation == _faceLoadGeneration && !_isDisposed && IsRouteActive)
                {
                    PopulateFaces(reviews);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !IsRouteActive)
        {
        }
        catch (Exception ex)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (generation == _faceLoadGeneration && !_isDisposed && IsRouteActive)
                {
                    FolderStatusMessage = OperationExecution.SafeMessage(ex);
                }
            }).ConfigureAwait(false);
        }
    }

    public async Task OpenProfileFolderAsync(CancellationToken cancellationToken = default)
    {
        if (_profileOperations is null)
        {
            FolderStatusMessage = "Profile operations service unavailable.";
            return;
        }

        FolderStatusMessage = null;
        try
        {
            var result = await _profileOperations.OpenProfileFolderAsync(ProfileId, cancellationToken).ConfigureAwait(true);
            FolderStatusMessage = result.IsSuccess ? "Opened in Windows Explorer." : result.UserMessage ?? "Could not open Profile folder.";
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task<bool> MoveToTrashAsync(CancellationToken cancellationToken = default)
    {
        if (_trashOperations is null)
        {
            FolderStatusMessage = "Profile Trash is unavailable.";
            return false;
        }

        if (IsUnknownProfile)
        {
            FolderStatusMessage = "Unknown Profiles are managed by assignment and cleanup workflows.";
            return false;
        }

        FolderStatusMessage = null;
        try
        {
            var result = await _trashOperations
                .MoveProfileToTrashAsync(ProfileId, cancellationToken)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                FolderStatusMessage = result.UserMessage ?? "Profile could not be moved to Trash.";
                return false;
            }

            _runtimeSnapshots?.Invalidate(ProfileId);
            return true;
        }
        catch (Exception exception)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(exception);
            return false;
        }
    }

    public void OpenCustomizeOverlay() =>
        TaskObserver.Observe(
            OpenCustomizeOverlayAsync(),
            "ProfileDetailViewModel.OpenCustomizeOverlayAsync");

    public Task OpenCustomizeOverlayAsync(CancellationToken cancellationToken = default) =>
        OpenCustomizeOverlayAsync(ProfileCustomizationSection.ProfileLayout, cancellationToken);

    public Task OpenCustomizeOverlayAsync(ProfileCustomizationSection initialSection, CancellationToken cancellationToken = default)
    {
        if (_overlay is null)
        {
            FolderStatusMessage = "Customize is unavailable without the application shell.";
            return Task.CompletedTask;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            var presentationState = new ProfilePresentationState(
                ProfileId,
                RowVersion,
                CurrentPresetId,
                _appearanceOverrides ?? ProfileAppearanceOverrides.Default,
                new ProfileMediaSources(CoverMediaId, BannerMediaId, FigureMediaId));

            _overlay.Push(new ProfileCustomizationOverlayRequest(
                presentationState,
                DisplayName,
                initialSection));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(exception);
        }

        return Task.CompletedTask;
    }
    private void EnsureFaceCrop(FaceReviewItemViewModel item)
    {
        if (_mediaResources is null || item.HasFaceCrop)
        {
            return;
        }

        if (FaceBoundingBox.TryParse(item.BoundingBoxJson) is null)
        {
            return;
        }

        TaskObserver.Observe(
            LoadFaceCropAsync(item, item.FaceId),
            "ProfileDetailViewModel.LoadFaceCropAsync",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
    }

    private async Task LoadFaceCropAsync(FaceReviewItemViewModel item, Guid faceId)
    {
        var artifact = await _mediaResources!.ResolveMediaThumbnailAsync(item.MediaId, RouteCancellationToken)
            .ConfigureAwait(true);
        var path = artifact.State == MediaAssetResourceState.Ready ? artifact.PhysicalPath : null;
        if (!_isDisposed
            && !RouteCancellationToken.IsCancellationRequested
            && Faces.Contains(item)
            && item.FaceId == faceId
            && !string.IsNullOrWhiteSpace(path))
        {
            item.FaceCropPath = path;
        }
    }

    public string? BannerVideoPath
    {
        get => _bannerVideoPath;
        private set
        {
            if (!string.Equals(_bannerVideoPath, value, StringComparison.Ordinal))
            {
                _bannerVideoPath = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasBannerVideo));
            }
        }
    }

    public bool HasBannerVideo => !string.IsNullOrWhiteSpace(BannerVideoPath);

    private async Task ResolveCoverResourceAsync(CancellationToken cancellationToken)
    {
        MediaAssetResource? resource = null;
        string? path = null;
        if (_mediaResources is not null)
        {
            resource = await _mediaResources.ResolveSelectedProfileCoverAsync(ProfileId, cancellationToken)
                .ConfigureAwait(false);
            path = resource.State == MediaAssetResourceState.Ready ? resource.PhysicalPath : null;
        }

        await UiDispatch.InvokeAsync(() =>
        {
            if (_isDisposed || !IsRouteActive)
            {
                return;
            }

            CoverImagePath = path;
            ApplyCoverImage(path, resource is null ? null : $"artifact:{resource.MediaAssetId:N}");
            RaisePropertyChanged(nameof(CoverImagePath));
            RaisePropertyChanged(nameof(CoverSource));
            RaisePropertyChanged(nameof(HasCoverImage));
        }).ConfigureAwait(false);
    }

    private async Task RefreshBannerVideoAsync(CancellationToken cancellationToken)
    {
        string? path = null;
        if (_catalog is not null)
        {
            var selection = await _catalog.MediaAssetReads.GetPublicSelectionAsync(ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var asset = selection?.BannerMediaAssetId is { } assetId
                ? await _catalog.MediaAssetReads.GetPublicSelectedMediaAsync(ProfileId, assetId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (asset?.Role == MediaAssetRole.Hover)
            {
                path = ResolveMediaAssetPath(asset);
            }
        }

        await UiDispatch.InvokeAsync(() =>
        {
            if (!_isDisposed && IsRouteActive)
            {
                BannerVideoPath = path;
            }
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>> LoadAppearanceFaceEvidenceAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null || MediaItems.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AppearanceFaceEvidence>>();
        }

        return await _catalog.FaceReads.GetAppearanceFaceEvidenceAsync(
            [.. MediaItems.Select(static m => m.MediaId)],
            ProfileId,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task RenameAsync(string newDisplayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            RenameStatusMessage = "Display name cannot be empty.";
            return;
        }

        if (_profileOperations is null)
        {
            DisplayName = newDisplayName.Trim();
            RenameStatusMessage = "Name updated.";
            return;
        }

        try
        {
            var request = new RenameProfileRequest(ProfileId, RowVersion, newDisplayName);
            var result = await _profileOperations.RenameProfileAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                DisplayName = outcome.DisplayName;
                RowVersion = outcome.RowVersion;
                PathState = outcome.PathState;
                if (outcome.PathState == ManagedPathState.Pending)
                {
                    IsRenaming = true;
                    RenameStatusMessage = "Renaming files... (reconciliation pending)";
                }
                else if (outcome.PathState == ManagedPathState.NeedsAttention)
                {
                    RenameStatusMessage = "Folder renaming needs attention.";
                }
                else
                {
                    IsRenaming = false;
                    RenameStatusMessage = "Profile renamed successfully.";
                }
            }
            else
            {
                RenameStatusMessage = result.UserMessage ?? "Failed to rename Profile.";
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task EnsureMetadataVocabularyLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_metadataVocabularyLoaded || _catalog is null)
        {
            return;
        }

        await _metadataVocabularyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadataVocabularyLoaded || _catalog is null)
            {
                return;
            }

            var categoriesTask = _catalog.SettingsReads.GetAllCategoriesAsync(cancellationToken);
            var tagsTask = _catalog.SettingsReads.GetAllTagsAsync(cancellationToken);
            await Task.WhenAll(categoriesTask, tagsTask).ConfigureAwait(false);

            var categories = await categoriesTask.ConfigureAwait(false);
            var tags = await tagsTask.ConfigureAwait(false);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || !IsRouteActive)
                {
                    return;
                }

                PopulateAvailableCategories(categories.Select(c => new ProfileCategoryOption(c.CategoryId, c.Name)));
                PopulateAvailableTags(tags.Select(t => new ProfileTagOption(t.TagId, t.Name)));
                _metadataVocabularyLoaded = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            _metadataVocabularyGate.Release();
        }
    }

    public void BeginEditMetadata()
    {
        EditDisplayName = DisplayName;
        EditCategoryId = CategoryId;
        EditTags.Clear();
        foreach (var assignment in _tagAssignments)
        {
            EditTags.Add(new ProfileTagOption(assignment.TagId, assignment.DisplayName));
        }

        TagSearchText = string.Empty;
        EditRating = Rating;
        EditIsFavorite = IsFavorite;
        EditOverview = Overview ?? string.Empty;
        IsEditingMetadata = true;
        RaisePropertyChanged(nameof(TagSearchResults));
    }

    public void CancelEditMetadata()
    {
        IsEditingMetadata = false;
    }

    public async Task<bool> SavePrivateNotesAsync(string? notes, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (string.Equals(normalized, Notes, StringComparison.Ordinal))
        {
            return true;
        }

        if (!ProfileRules.IsPrivateNotesValid(normalized))
        {
            FolderStatusMessage = string.Format(SurfaceText.Get("Profile.PrivateNote.Limit", "Private notes are limited to {0:N0} characters."), ProfileRules.MaximumPrivateNotesLength);
            return false;
        }

        try
        {
            if (_profileOperations is null)
            {
                Notes = normalized;
                RowVersion++;
                if (_presentationModel is not null)
                {
                    _presentationModel = _presentationModel with { Notes = normalized };
                    RaisePropertyChanged(nameof(PresentationModel));
                }
                return true;
            }

            var request = new UpdateProfileMetadataRequest(
                ProfileId,
                RowVersion,
                CategoryId,
                [.. _tagAssignments.Select(static assignment => assignment.TagId)],
                Rating,
                IsFavorite,
                Overview,
                normalized);
            var result = await _profileOperations.UpdateProfileMetadataAsync(request, cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.Value is not { } outcome)
            {
                FolderStatusMessage = result.UserMessage ?? "Private note could not be saved.";
                return false;
            }

            RowVersion = outcome.RowVersion;
            Notes = normalized;
            if (_presentationModel is not null)
            {
                _presentationModel = _presentationModel with { Notes = normalized };
                RaisePropertyChanged(nameof(PresentationModel));
            }
            FolderStatusMessage = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(exception);
            return false;
        }
    }

    public void PopulateAvailableCategories(IEnumerable<ProfileCategoryOption> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        AvailableCategories.Clear();
        AvailableCategories.Add(new ProfileCategoryOption(null, "(None)"));
        foreach (var c in categories)
        {
            AvailableCategories.Add(c);
        }
    }

    public async Task SaveMetadataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CommitPendingProfileTagsForSaveAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (RouteCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
            return;
        }

        var trimmedName = EditDisplayName?.Trim() ?? string.Empty;
        var stagedAssignments = EditTags.DistinctBy(static tag => tag.TagId, StringComparer.Ordinal).ToList();
        var parsedTags = stagedAssignments.Select(static tag => tag.TagId).ToList();
        var stagedTagNames = stagedAssignments.Select(static tag => tag.DisplayName).ToList();
        var targetCategoryId = string.IsNullOrWhiteSpace(EditCategoryId) ? null : EditCategoryId;

        if (_profileOperations is null)
        {
            if (!string.IsNullOrWhiteSpace(trimmedName))
            {
                DisplayName = trimmedName;
            }
            _categoryId = targetCategoryId;
            CategoryName = AvailableCategories.FirstOrDefault(c => c.CategoryId == targetCategoryId)?.DisplayName;
            if (CategoryName == "(None)")
            {
                CategoryName = null;
            }
            _tags = stagedTagNames;
            _tagAssignments = [.. stagedAssignments.Select(static tag => new ProfileTagAssignment(tag.TagId, tag.DisplayName))];
            Rating = EditRating;
            IsFavorite = EditIsFavorite;
            Overview = string.IsNullOrWhiteSpace(EditOverview) ? null : EditOverview.Trim();
            RowVersion++;
            if (_presentationModel is not null)
            {
                _presentationModel = _presentationModel with
                {
                    DisplayName = DisplayName,
                    CategoryName = CategoryName,
                    Tags = _tags,
                    Rating = Rating,
                    Overview = Overview,
                    Notes = Notes
                };
                RaisePropertyChanged(nameof(PresentationModel));
            }
            RaisePropertyChanged(nameof(Tags));
            RaisePropertyChanged(nameof(CategoryId));
            RaisePropertyChanged(nameof(CategoryName));
            FolderStatusMessage = "Profile metadata updated.";
            IsEditingMetadata = false;
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(trimmedName) && !string.Equals(trimmedName, DisplayName, StringComparison.Ordinal))
            {
                await RenameAsync(trimmedName, cancellationToken).ConfigureAwait(true);
            }

            var request = new UpdateProfileMetadataRequest(
                ProfileId: ProfileId,
                ExpectedRowVersion: RowVersion,
                CategoryId: targetCategoryId,
                TagIds: parsedTags,
                Rating: EditRating,
                IsFavorite: EditIsFavorite,
                Overview: string.IsNullOrWhiteSpace(EditOverview) ? null : EditOverview.Trim(),
                Notes: Notes);
            var result = await _profileOperations.UpdateProfileMetadataAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                RowVersion = outcome.RowVersion;
                _categoryId = targetCategoryId;
                CategoryName = AvailableCategories.FirstOrDefault(c => c.CategoryId == targetCategoryId)?.DisplayName;
                if (CategoryName == "(None)")
                {
                    CategoryName = null;
                }
                _tags = stagedTagNames;
                _tagAssignments = [.. stagedAssignments.Select(static tag => new ProfileTagAssignment(tag.TagId, tag.DisplayName))];
                Rating = request.Rating;
                IsFavorite = request.IsFavorite;
                Overview = request.Overview;
                Notes = request.Notes;
                FolderStatusMessage = "Profile metadata updated.";

                if (_presentationModel is not null)
                {
                    _presentationModel = _presentationModel with
                    {
                        DisplayName = DisplayName,
                        CategoryName = CategoryName,
                        Tags = _tags,
                        Rating = Rating,
                        IsFavorite = IsFavorite,
                        Overview = Overview,
                        Notes = Notes
                    };
                    RaisePropertyChanged(nameof(PresentationModel));
                }
                RaisePropertyChanged(nameof(Tags));
                RaisePropertyChanged(nameof(CategoryId));
                RaisePropertyChanged(nameof(CategoryName));
                IsEditingMetadata = false;
            }
            else
            {
                FolderStatusMessage = result.UserMessage ?? "Failed to update profile metadata.";
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task OpenChangeFaceProfilePickerAsync(FaceReviewItemViewModel face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_overlay is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<ProfilePickerItem> candidates = [];
            if (_catalog is not null)
            {
                candidates = await new ProfilePickerReads(_catalog).GetAllCandidatesAsync().ConfigureAwait(true);
            }
            else if (AvailableCategories.Count > 0)
            {
                candidates = [new ProfilePickerItem(ProfileId, DisplayName, CategoryName)];
            }

            var request = new ProfilePickerOverlayRequest(
                candidates,
                picked => OnReassignFacePicked(face, picked),
                title: "Reassign Face to Profile",
                prompt: "Select a Profile to assign this face detection to:");
            _overlay.Push(request);
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    private void OnReassignFacePicked(FaceReviewItemViewModel face, ProfilePickerItem picked)
    {
        TaskObserver.Observe(ReassignFaceAsync(face, picked), "ProfileDetailViewModel.ReassignFaceAsync");
    }

    private async Task ReassignFaceAsync(FaceReviewItemViewModel face, ProfilePickerItem picked)
    {
        try
        {
            if (_faceDecisionOperations is not null)
            {
                var command = new ConfirmFaceCommand(face.FaceId, picked.ProfileId, face.RowVersion);
                var result = await _faceDecisionOperations.ConfirmFaceAsync(command).ConfigureAwait(true);
                if (!result.IsSuccess || result.Value is not { } outcome)
                {
                    FolderStatusMessage = result.UserMessage ?? "Failed to reassign the face detection.";
                    return;
                }

                face.ApplyDecision(outcome.DecisionState, picked.ProfileId, picked.DisplayName, outcome.RowVersion);
            }
            else
            {
                face.ApplyDecision(FaceDecisionState.Confirmed, picked.ProfileId, picked.DisplayName, face.RowVersion + 1);
            }

            RemoveResolvedFace(face);
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task ConfirmFaceAsync(FaceReviewItemViewModel face, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_faceDecisionOperations is null)
        {
            face.ApplyDecision(FaceDecisionState.Confirmed, ProfileId, DisplayName, face.RowVersion + 1);
            RemoveResolvedFace(face);
            return;
        }

        try
        {
            var command = new ConfirmFaceCommand(face.FaceId, ProfileId, face.RowVersion);
            var result = await _faceDecisionOperations.ConfirmFaceAsync(command, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                face.ApplyDecision(outcome.DecisionState, ProfileId, DisplayName, outcome.RowVersion);
                RemoveResolvedFace(face);
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task RejectFaceAsync(FaceReviewItemViewModel face, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (_faceDecisionOperations is null)
        {
            face.ApplyDecision(FaceDecisionState.Rejected, null, null, face.RowVersion + 1);
            RemoveResolvedFace(face);
            return;
        }

        try
        {
            var command = new RejectFaceCommand(face.FaceId, face.RowVersion);
            var result = await _faceDecisionOperations.RejectFaceAsync(command, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                face.ApplyDecision(outcome.DecisionState, null, null, outcome.RowVersion);
                RemoveResolvedFace(face);
            }
        }
        catch (Exception ex)
        {
            FolderStatusMessage = OperationExecution.SafeMessage(ex);
        }
    }

    private void RemoveResolvedFace(FaceReviewItemViewModel face)
    {
        if (face.IsUnresolved)
        {
            return;
        }

        // A read started before this decision must not restore the resolved item.
        Interlocked.Increment(ref _faceLoadGeneration);
        Faces.Remove(face);
        if (ReferenceEquals(SelectedFace, face))
        {
            SelectedFace = Faces.FirstOrDefault();
        }
        RaisePropertyChanged(nameof(HasFaces));
        RaisePropertyChanged(nameof(UnresolvedFaceCount));
    }

    public async Task OpenMediaInDefaultAppAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return;
        }

        if (_mediaOperations is null)
        {
            FolderStatusMessage = "Media actions are unavailable.";
            return;
        }

        var result = await _mediaOperations.OpenInDefaultAppAsync(assetId, cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            FolderStatusMessage = result.UserMessage ?? "Windows could not open this media item with its default app.";
        }
    }

    public void OpenMediaDetail(Guid assetId) => OpenInspector(assetId);

    public void OpenInspector(Guid assetId)
    {
        if (_isDisposed || !IsRouteActive || assetId == Guid.Empty || _inspector?.MediaId == assetId)
        {
            return;
        }

        var origin = MediaGrid.CaptureState(ProfileId);
        if (origin.SelectedMediaId is null)
        {
            origin = origin with { SelectedMediaId = assetId };
        }

        RetireInspector();
        Inspector = new MediaDetailViewModel(
            assetId,
            origin,
            _catalog,
            modelAdapters: _modelAdapters,
            closeInspector: CloseInspector,
            retargetInspector: OpenInspector,
            mediaChanged: OnInspectedMediaChanged);
    }

    public void CloseInspector()
    {
        if (_inspector is null)
        {
            return;
        }

        RetireInspector();
        Inspector = null;
    }

    private void RetireInspector()
    {
        if (_inspector is { } current)
        {
            current.RetireRoute();
            current.Dispose();
        }
    }

    private void OnInspectedMediaChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        TaskObserver.Observe(
            LoadMediaAsync(),
            "Refreshing profile media after an inspector change",
            exception => FolderStatusMessage = OperationExecution.SafeMessage(exception));
    }

    public async Task AssignUnknownMediasToProfileAsync(Guid destinationProfileId, IReadOnlyList<Guid>? assetIds = null, CancellationToken cancellationToken = default)
    {
        var selected = (assetIds ?? SelectedUnknownMediaIds).ToList();
        if (selected.Count == 0)
        {
            UnknownStatusNotice = "No media items selected for assignment.";
            return;
        }

        if (_unknownOperations is null)
        {
            ActiveOwnedMediaCount = Math.Max(0, ActiveOwnedMediaCount - selected.Count);
            RaisePropertyChanged(nameof(ActiveOwnedMediaCount));
            RaisePropertyChanged(nameof(IsActiveUnresolved));
            UnknownStatusNotice = $"Assigned {selected.Count} items to Profile.";
            return;
        }

        try
        {
            var request = new AssignUnknownMediasToProfileRequest(ProfileId, selected, destinationProfileId, RowVersion);
            var result = await _unknownOperations.AssignUnknownMediasToProfileAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                ActiveOwnedMediaCount = outcome.RemainingUnknownMediaCount;
                UnknownStatusNotice = outcome.SourceUnknownLeavesActiveUnresolved
                    ? $"Assigned {outcome.ResolvedMediaCount} items. UNKNOWN Profile is now fully resolved and leaves active queue."
                    : $"Assigned {outcome.ResolvedMediaCount} items. UNKNOWN Profile has {outcome.RemainingUnknownMediaCount} active items remaining.";
                RaisePropertyChanged(nameof(ActiveOwnedMediaCount));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
                await LoadAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                UnknownStatusNotice = result.UserMessage ?? "Failed to assign media.";
            }
        }
        catch (Exception ex)
        {
            UnknownStatusNotice = OperationExecution.SafeMessage(ex);
        }
    }

    public async Task CreateProfileFromUnknownMediasAsync(string newDisplayName, IReadOnlyList<Guid>? assetIds = null, CancellationToken cancellationToken = default)
    {
        var selected = (assetIds ?? SelectedUnknownMediaIds).ToList();
        if (selected.Count == 0)
        {
            UnknownStatusNotice = "No media items selected for assignment.";
            return;
        }

        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            UnknownStatusNotice = "New Profile name is required.";
            return;
        }

        if (_unknownOperations is null)
        {
            ActiveOwnedMediaCount = Math.Max(0, ActiveOwnedMediaCount - selected.Count);
            RaisePropertyChanged(nameof(ActiveOwnedMediaCount));
            RaisePropertyChanged(nameof(IsActiveUnresolved));
            UnknownStatusNotice = $"Created Profile '{newDisplayName}' with {selected.Count} items.";
            return;
        }

        try
        {
            var request = new CreateProfileFromUnknownMediasRequest(ProfileId, selected, newDisplayName, ExpectedUnknownRowVersion: RowVersion);
            var result = await _unknownOperations.CreateProfileFromUnknownMediasAsync(request, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is { } outcome)
            {
                ActiveOwnedMediaCount = outcome.RemainingUnknownMediaCount;
                UnknownStatusNotice = outcome.SourceUnknownLeavesActiveUnresolved
                    ? $"Created Profile '{outcome.DestinationDisplayName}'. UNKNOWN Profile is now fully resolved and leaves active queue."
                    : $"Created Profile '{outcome.DestinationDisplayName}'. UNKNOWN has {outcome.RemainingUnknownMediaCount} active items remaining.";
                RaisePropertyChanged(nameof(ActiveOwnedMediaCount));
                RaisePropertyChanged(nameof(IsActiveUnresolved));
                _navigation?.Navigate(new ProfileRoute(outcome.DestinationProfileId));
            }
            else
            {
                UnknownStatusNotice = result.UserMessage ?? "Failed to create Profile from Unknown.";
            }
        }
        catch (Exception ex)
        {
            UnknownStatusNotice = OperationExecution.SafeMessage(ex);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        RetireRoute();
        _isDisposed = true;
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _mediaLoadGeneration);
        Interlocked.Increment(ref _assignmentReviewLoadGeneration);
        Interlocked.Increment(ref _faceLoadGeneration);
        if (_catalog is not null)
        {
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        }

        RetireInspector();
        _inspector = null;
        _pageRefresh?.Dispose();
        _appearanceRefresh?.Dispose();
        _mediaRefresh?.Dispose();
        _relatedRefresh?.Dispose();
        _facesRefresh?.Dispose();
        Related.Dispose();
        MediaGrid.Dispose();
        MediaItems.Clear();
        RecentMedia.Clear();
        RelatedProfiles.Clear();
        Related.EvidenceEntries.Clear();
        Faces.Clear();
    }
}

public sealed class ProfileMediaItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ProfileMediaItemViewModel(ProfileMediaItemReadModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public ProfileMediaItemReadModel Model { get; }
    public Guid MediaId => Model.MediaId;
    public MediaType MediaType => Model.MediaType;
    public ProfileMediaRelation RelationType => Model.RelationType;
    public string? ManagedRelativePath => Model.ManagedRelativePath;
    public string? ManagedFileName => Model.ManagedFileName;
    public string? DisplayFileName => Model.OriginalFileName ?? Model.ManagedFileName;
    public DateTimeOffset CreatedAtUtc => Model.CreatedAtUtc;
    public int? PixelWidth => Model.PixelWidth;
    public string? ContentFingerprint => Model.ContentFingerprint;
    public int? PixelHeight => Model.PixelHeight;
    public int? DurationMs => Model.DurationMs;
    public ManagedPathState PathState => Model.PathState;
    public bool IsNeedsAttention => PathState == ManagedPathState.NeedsAttention;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                RaisePropertyChanged();
            }
        }
    }

    public string RelationLabel => RelationType switch
    {
        ProfileMediaRelation.Owner => "Owner",
        ProfileMediaRelation.Appears => "Appears In",
        ProfileMediaRelation.Manual => "Manual",
        _ => RelationType.ToString()
    };

    public string DurationFormatted => DurationMs.HasValue && DurationMs.Value > 0
        ? TimeSpan.FromMilliseconds(DurationMs.Value).ToString(@"m\:ss")
        : string.Empty;
}
