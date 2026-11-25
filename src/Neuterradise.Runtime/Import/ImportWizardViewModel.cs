using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.Localization;
using Neuterradise.App.Media;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Cache;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Diagnostics;
using Neuterradise.App.SystemServices.Jobs.Handlers;
using Neuterradise.App.SystemServices.Operations;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import;

public enum ImportWizardStep
{
    Profile = 1,
    Details = 2,
}

public enum ReviewResolutionAction
{
    RetryPreparation,
    Remove,
    ReuseDuplicate,
    KeepDestination,
    MoveToRecognizedProfile,
}

public enum ImportDestinationChoice
{
    New,
    Existing,
}

/// <summary>What intake found for the files the user picked, before any preparation.</summary>
public sealed record ImportIntakeSummary(int DiscoveredCount, int ReadyCount)
{
    public int UnsupportedCount => Math.Max(0, DiscoveredCount - ReadyCount);
}

public sealed record CategoryOptionViewModel(string CategoryId, string Name);

public sealed record TagOptionViewModel(string TagId, string Name);

public sealed class ProfileLookupItemViewModel(ProfileLookupResult result) : ObservableObject
{
    private ImageRef? _coverSource;

    public Guid ProfileId { get; } = result.ProfileId;

    public string DisplayName { get; } = result.DisplayName;

    public string? CategoryName { get; } = result.CategoryName;

    public ImageRef? CoverSource
    {
        get => _coverSource;
        set => SetProperty(ref _coverSource, value);
    }
}

/// <summary>
/// A likely existing profile, with the kind of evidence kept separate: shared media (the Vault
/// already holds some of these exact files in that profile) is not the same claim as a face match.
/// </summary>
public sealed record ImportProfileHint(Guid ProfileId, string DisplayName, string Evidence, bool IsFaceMatch);

/// <summary>One Cover or Banner choice, with its real preview decoded off the UI thread.</summary>
public sealed class ImportVisualOption : ObservableObject
{
    private bool _isSelected;
    private ImageRef? _preview;
    private string? _motionPath;

    public ImportVisualOption(VerificationCoverCandidate cover)
    {
        Cover = cover;
        CandidateId = cover.CandidateId;
        Title = cover.DisplayTitle;
        IsRecommended = cover.IsRecommended;
        PreviewPath = cover.PreviewImagePath;
    }

    public ImportVisualOption(VerificationBannerCandidate banner)
    {
        Banner = banner;
        CandidateId = banner.CandidateId;
        Title = banner.DisplayTitle;
        IsRecommended = banner.IsRecommended;
        PreviewPath = banner.PreviewImagePath;
    }

    public VerificationCoverCandidate? Cover { get; }

    public VerificationBannerCandidate? Banner { get; }

    public string CandidateId { get; }

    public string Title { get; }

    public bool IsRecommended { get; }

    public string? PreviewPath { get; private set; }

    public Guid MediaId => Cover?.MediaId ?? Banner!.MediaId;

    public long? PreviewTimestampMilliseconds => Cover?.TimestampMilliseconds;

    public void SetPreviewPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PreviewPath = path;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ImageRef? Preview
    {
        get => _preview;
        set
        {
            if (SetProperty(ref _preview, value))
            {
                RaisePropertyChanged(nameof(HasPreview));
            }
        }
    }

    public bool HasPreview => _preview is not null;

    public string? MotionPath
    {
        get => _motionPath;
        set => SetProperty(ref _motionPath, value);
    }
}

/// <summary>
/// The two-step Import overlay: Step 1 "Who is this media for?", Step 2 "Details". It owns only the
/// user's draft. Background intake keeps running while it is open; hints, duplicate facts, face matches
/// and Cover/Banner candidates fill in progressively and never gate typing, choosing or Import.
///
/// Pressing Import hands the draft to <see cref="ImportFinalizer"/> as durable intent and closes the
/// overlay; closing without importing keeps the durable import (and saves the draft) so it can be
/// continued later. Neither action cancels anything.
/// </summary>
public sealed class ImportWizardViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan HintRefreshInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DraftSaveDebounce = TimeSpan.FromMilliseconds(450);

    private readonly CatalogDb? _catalog;
    private readonly VerificationOperations? _operations;
    private readonly ImportFinalizer? _finalizer;
    private readonly ImportPublicationCoordinator? _publication;
    private readonly Action<ImportWizardViewModel>? _close;
    private readonly Action<Guid>? _imported;
    private readonly DerivedImageLoader _images;
    private readonly MediaAssetResourceAuthority? _mediaAssets;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ImportIntakeSummary? _intakeSummary;
    private readonly RefreshCoalescer? _hintRefreshCoalescer;
    private readonly RefreshCoalescer? _draftSaveCoalescer;

    private readonly List<TagOptionViewModel> _allTags = [];
    private readonly List<StagedDuplicateDecision> _duplicateDecisions = [];
    private readonly List<StagedProfileCollisionDecision> _collisionDecisions = [];
    private IReadOnlyList<StagedFaceDecision> _faceDecisions = [];
    private IReadOnlyList<Guid> _acknowledgedMissingDependencyItemIds = [];
    private IReadOnlyList<Guid> _attentionItemIds = [];
    private CancellationTokenSource? _nameCheck;
    private CancellationTokenSource? _profileSearch;
    private readonly SemaphoreSlim _hintRefreshGate = new(1, 1);
    private int _pendingHintSections;

    [Flags]
    private enum HintSection { None = 0, Progress = 1, Review = 2, Faces = 4, Appearance = 8, All = 15 }

    private ImportWizardStep _step = ImportWizardStep.Profile;
    private ImportDestinationChoice _choice = ImportDestinationChoice.New;
    private string _newProfileName = string.Empty;
    private ProfileLookupItemViewModel? _duplicateNameProfile;
    private string _existingSearchText = string.Empty;
    private ProfileLookupItemViewModel? _selectedExistingProfile;
    private string? _selectedCategoryId;
    private string _tagSearchText = string.Empty;
    private string _newCategoryName = string.Empty;
    private int _rating;
    private readonly SemaphoreSlim _tagCommitGate = new(1, 1);
    private Task _pendingTagCommit = Task.CompletedTask;
    private bool _isFavorite;
    private string _overview = string.Empty;
    private ImportVisualOption? _selectedCover;
    private ImportVisualOption? _selectedBanner;
    private bool _coverTouched;
    private bool _bannerTouched;
    private VerificationAppearanceIntent _coverIntent = VerificationAppearanceIntent.Unchanged;
    private VerificationAppearanceIntent _bannerIntent = VerificationAppearanceIntent.Unchanged;
    private bool _candidatesLoaded;
    private int _totalCount;
    private int _alreadyInLibraryCount;
    private int _checkedCount;
    private int _admittedCount;
    private bool _hasPreparationPending;
    private bool _isImporting;
    private bool _isReviewResolving;
    private bool _importRequested;
    private bool _publishWhenReady;
    private bool _initialized;
    private VerificationDestinationDraft? _lockedDestination;
    private string? _invalidConfirmedDraftJson;
    private string? _errorMessage;
    private bool _disposed;

    public ImportWizardViewModel(
        Guid unitId,
        string sourceDisplayName,
        CatalogDb? catalog = null,
        ImportFinalizer? finalizer = null,
        Action<ImportWizardViewModel>? close = null,
        Action<Guid>? imported = null,
        ImportIntakeSummary? intakeSummary = null,
        DerivedImageLoader? images = null,
        TimeProvider? timeProvider = null)
    {
        UnitId = unitId;
        SourceDisplayName = sourceDisplayName;
        _catalog = catalog;
        _operations = catalog is null ? null : new VerificationOperations(catalog, timeProvider);
        _finalizer = finalizer;
        _publication = catalog is null ? null : new ImportPublicationCoordinator(catalog, timeProvider);
        _close = close;
        _imported = imported;
        _intakeSummary = intakeSummary;
        _images = images ?? DerivedImageLoader.Shared;
        _mediaAssets = catalog is null ? null : new MediaAssetResourceAuthority(catalog);
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_catalog is not null)
        {
            _hintRefreshCoalescer = new RefreshCoalescer(
                RefreshSignalledHintsAsync,
                TimeSpan.FromMilliseconds(250));
            _draftSaveCoalescer = new RefreshCoalescer(
                PersistAutosaveDraftAsync,
                DraftSaveDebounce);
            _catalog.WriteCoordinator.Invalidated += OnCatalogInvalidated;
        }
        if (_finalizer is not null)
            _finalizer.Changed += OnFinalizerChanged;

        ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => CanContinue, onError: ReportImportFailure);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => CanImport, onError: ReportImportFailure);
        CloseCommand = new AsyncRelayCommand(CloseAsync, canExecute: () => !_isReviewResolving, onError: exception => Trace.TraceWarning("Import overlay close: {0}", exception));
        ChooseNewCommand = new RelayCommand(_ => Choice = ImportDestinationChoice.New);
        ChooseExistingCommand = new RelayCommand(_ => Choice = ImportDestinationChoice.Existing);
        UseHintCommand = new RelayCommand(parameter =>
        {
            if (parameter is ImportProfileHint hint)
            {
                UseExistingProfile(hint.ProfileId, hint.DisplayName);
            }
        });
        UseDuplicateNameProfileCommand = new RelayCommand(_ =>
        {
            if (_duplicateNameProfile is { } profile)
            {
                UseExistingProfile(profile.ProfileId, profile.DisplayName);
            }
        });
        AddTagCommand = new RelayCommand(parameter =>
        {
            if (parameter is TagOptionViewModel tag)
            {
                AddTag(tag);
            }
        });
        RemoveTagCommand = new RelayCommand(parameter =>
        {
            if (parameter is TagOptionViewModel tag)
            {
                RemoveTag(tag);
            }
        });
        SelectCoverCommand = new RelayCommand(parameter => SelectCover(parameter as ImportVisualOption, byUser: true));
        SelectBannerCommand = new RelayCommand(parameter => SelectBanner(parameter as ImportVisualOption, byUser: true));
        ClearCoverCommand = new RelayCommand(_ => SelectCover(null, byUser: true));
        ClearBannerCommand = new RelayCommand(_ => SelectBanner(null, byUser: true));
        ResolveDuplicateReuseCommand = ReviewCommand<VerificationDuplicateProblem>(ReviewResolutionAction.ReuseDuplicate, problem => problem.ImportItemId);
        ResolveDuplicateSkipCommand = ReviewCommand<VerificationDuplicateProblem>(ReviewResolutionAction.Remove, problem => problem.ImportItemId);
        KeepCollisionCommand = ReviewCommand<VerificationProfileCollisionProblem>(ReviewResolutionAction.KeepDestination, problem => problem.ImportItemId);
        MoveCollisionCommand = ReviewCommand<VerificationProfileCollisionProblem>(ReviewResolutionAction.MoveToRecognizedProfile, problem => problem.ImportItemId);
        SkipCollisionCommand = ReviewCommand<VerificationProfileCollisionProblem>(ReviewResolutionAction.Remove, problem => problem.ImportItemId);
        RetryPreparationCommand = ReviewCommand<VerificationPreparationProblem>(ReviewResolutionAction.RetryPreparation, problem => problem.ImportItemId);
        RemovePreparationItemCommand = ReviewCommand<VerificationPreparationProblem>(ReviewResolutionAction.Remove, problem => problem.ImportItemId);
        ResolveAllReviewCommand = new AsyncRelayCommand(
            parameter => parameter is ReviewResolutionAction action ? ResolveReviewAsync(action, null) : Task.CompletedTask,
            parameter => parameter is ReviewResolutionAction && CanResolveReview,
            ReportImportFailure);
        CreateCategoryCommand = new AsyncRelayCommand(CreateCategoryAsync, onError: ReportImportFailure);
    }

    public Guid UnitId { get; }

    public string SourceDisplayName { get; }

    public ICommand ContinueCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ChooseNewCommand { get; }
    public ICommand ChooseExistingCommand { get; }
    public ICommand UseHintCommand { get; }
    public ICommand UseDuplicateNameProfileCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand SelectCoverCommand { get; }
    public ICommand SelectBannerCommand { get; }
    public ICommand ClearCoverCommand { get; }
    public ICommand ClearBannerCommand { get; }
    public ICommand ResolveDuplicateReuseCommand { get; }
    public ICommand ResolveDuplicateSkipCommand { get; }
    public ICommand KeepCollisionCommand { get; }
    public ICommand MoveCollisionCommand { get; }
    public ICommand SkipCollisionCommand { get; }
    public ICommand RetryPreparationCommand { get; }
    public ICommand RemovePreparationItemCommand { get; }
    public ICommand ResolveAllReviewCommand { get; }
    public ICommand CreateCategoryCommand { get; }

    public bool IsInitialized => _initialized;

    public bool IsChooseProfileMode => _step == ImportWizardStep.Profile;

    public bool IsVerifyMode => _step == ImportWizardStep.Details;

    // ---------------------------------------------------------------- steps

    public string StepTitle => IsChooseProfileMode
        ? SurfaceText.Get("ImportWizard.ChooseProfile.Title", "Choose Profile")
        : SurfaceText.Get("ImportWizard.Verify.Title", "Review");

    public string StepIndicator => IsChooseProfileMode
        ? SurfaceText.Get("ImportWizard.ChooseProfile.Indicator", "Destination")
        : SurfaceText.Get("ImportWizard.Verify.Indicator", "Profile details and import decisions");

    public string MediaSummaryText
    {
        get
        {
            var parts = new List<string>();
            var total = _totalCount > 0 ? _totalCount : _intakeSummary?.ReadyCount ?? 0;
            if (_intakeSummary is { UnsupportedCount: > 0 } intake)
            {
                parts.Add(SurfaceText.Format("ImportWizard.Found", "{0} files found", intake.DiscoveredCount));
                parts.Add(SurfaceText.Format("ImportWizard.Ready", "{0} media ready", total));
                parts.Add(SurfaceText.Format("ImportWizard.Unsupported", "{0} not supported (left untouched)", intake.UnsupportedCount));
            }
            else
            {
                parts.Add(total == 1
                    ? SurfaceText.Get("ImportWizard.OneMedia", "1 media")
                    : SurfaceText.Format("ImportWizard.ManyMedia", "{0} media", total));
            }

            if (_alreadyInLibraryCount > 0)
            {
                parts.Add(SurfaceText.Format("ImportWizard.New", "{0} new", Math.Max(0, total - _alreadyInLibraryCount)));
                parts.Add(SurfaceText.Format("ImportWizard.AlreadyInLibrary", "{0} already in your Vault", _alreadyInLibraryCount));
            }

            return string.Join(" · ", parts);
        }
    }

    // Reused Media has no new Candidate work count; Verify follows the required capability gate.
    public string PreparationText => IsVerifyMode
        ? HasPreparationPending ? SurfaceText.Get("Import.Stage.Preparing", "Preparing media") : string.Empty
        : _totalCount > 0 && _checkedCount < _totalCount
        ? SurfaceText.Format("ImportWizard.Preparing", "Preparing in the background… {0} of {1} checked. You can carry on.", _checkedCount, _totalCount)
        : string.Empty;

    public bool IsPreparing => IsVerifyMode ? !_candidatesLoaded || HasPreparationPending : _totalCount == 0 || _checkedCount < _totalCount;

    public ImportDestinationChoice Choice
    {
        get => _choice;
        set
        {
            if (SetProperty(ref _choice, value))
            {
                RaisePropertyChanged(nameof(IsNewChoice));
                RaisePropertyChanged(nameof(IsExistingChoice));
                RaisePropertyChanged(nameof(ShowNewProfileDetails));
                RaisePropertyChanged(nameof(DestinationNote));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                if (value == ImportDestinationChoice.Existing)
                {
                    ScheduleProfileSearch();
                }
                ScheduleDraftSave();
                RaiseValidity();
            }
        }
    }

    public bool IsNewChoice
    {
        get => _choice == ImportDestinationChoice.New;
        set { if (value) Choice = ImportDestinationChoice.New; }
    }

    public bool IsExistingChoice
    {
        get => _choice == ImportDestinationChoice.Existing;
        set { if (value) Choice = ImportDestinationChoice.Existing; }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (SetProperty(ref _newProfileName, value ?? string.Empty))
            {
                if (_choice != ImportDestinationChoice.New && !string.IsNullOrWhiteSpace(value))
                {
                    Choice = ImportDestinationChoice.New;
                }

                RaisePropertyChanged(nameof(NameValidationText));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                ScheduleDraftSave();
                RaiseValidity();
                ScheduleNameCheck();
            }
        }
    }

    public string? NameValidationText => !string.IsNullOrWhiteSpace(_newProfileName)
        && !ProfileRules.TryValidateDisplayName(_newProfileName, out _, out var failure) ? failure : null;

    public ProfileLookupItemViewModel? DuplicateNameProfile
    {
        get => _duplicateNameProfile;
        private set
        {
            if (SetProperty(ref _duplicateNameProfile, value))
            {
                RaisePropertyChanged(nameof(HasDuplicateName));
                RaisePropertyChanged(nameof(DuplicateNameText));
            }
        }
    }

    public bool HasDuplicateName => _duplicateNameProfile is not null && _choice == ImportDestinationChoice.New;

    public string DuplicateNameText => _duplicateNameProfile is null
        ? string.Empty
        : SurfaceText.Format(
            "ImportWizard.DuplicateName",
            "A profile named \"{0}\" already exists. Add to it, or continue to create a second profile with the same name.",
            _duplicateNameProfile.DisplayName);

    public string ExistingSearchText
    {
        get => _existingSearchText;
        set
        {
            if (SetProperty(ref _existingSearchText, value ?? string.Empty))
            {
                ScheduleProfileSearch();
            }
        }
    }

    public ObservableCollection<ProfileLookupItemViewModel> ExistingProfiles { get; } = [];

    public ProfileLookupItemViewModel? SelectedExistingProfile
    {
        get => _selectedExistingProfile;
        set
        {
            if (SetProperty(ref _selectedExistingProfile, value))
            {
                if (value is not null)
                {
                    Choice = ImportDestinationChoice.Existing;
                }

                RaisePropertyChanged(nameof(DestinationNote));
                RaisePropertyChanged(nameof(DestinationDisplayName));
                ScheduleDraftSave();
                RaiseValidity();
                RaiseSummary();
            }
        }
    }

    public ObservableCollection<ImportProfileHint> SharedMediaHints { get; } = [];
    public ObservableCollection<ImportProfileHint> FaceHints { get; } = [];
    public bool ShowNewProfileDetails => _choice == ImportDestinationChoice.New;

    public string DestinationNote => _choice switch
    {
        ImportDestinationChoice.Existing => SurfaceText.Format(
            "ImportWizard.ExistingNote",
            "Adding to {0}. Their name, category, tags, rating and overview stay as they are.",
            _selectedExistingProfile?.DisplayName ?? "the chosen profile"),
        _ => string.Empty,
    };

    public ObservableCollection<CategoryOptionViewModel> Categories { get; } = [];

    public string? SelectedCategoryId
    {
        get => _selectedCategoryId;
        set
        {
            if (SetProperty(ref _selectedCategoryId, value))
            {
                ScheduleDraftSave();
                RaiseSummary();
            }
        }
    }

    public ObservableCollection<TagOptionViewModel> SelectedTags { get; } = [];
    public ObservableCollection<TagOptionViewModel> TagSuggestions { get; } = [];
    public ObservableCollection<VerificationDuplicateProblem> DuplicateProblems { get; } = [];
    public ObservableCollection<VerificationProfileCollisionProblem> ProfileCollisionProblems { get; } = [];
    public ObservableCollection<VerificationPreparationProblem> PreparationProblems { get; } = [];
    public bool HasPreparationPending => _hasPreparationPending;
    public bool IsReviewResolving => _isReviewResolving;
    private bool CanResolveReview => _operations is not null && IsVerifyMode && !_disposed && !_isImporting && !_isReviewResolving;
    public bool HasProblems => DuplicateProblems.Count > 0
        || ProfileCollisionProblems.Count > 0
        || PreparationProblems.Count > 0
        || HasPreparationPending;

    public string NewCategoryName
    {
        get => _newCategoryName;
        set => SetProperty(ref _newCategoryName, value ?? string.Empty);
    }

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
                    _pendingTagCommit = task;
                    TaskObserver.Observe(task, "ImportWizardViewModel.CommitTagTokensAsync");
                    return;
                }
                RefreshTagSuggestions();
            }
        }
    }

    public Task CommitTagTokensAsync() => CommitTagTokensCoreAsync(isPartial: true);

    public Task OnTagInputEnterAsync() => CommitTagTokensCoreAsync(isPartial: false);

    private async Task CommitTagTokensCoreAsync(bool isPartial)
    {
        if (_catalog is null)
        {
            return;
        }

        await _tagCommitGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            IReadOnlyList<TaxonomyNamePolicy.TaxonomyTagToken> tokens = [];
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (isPartial)
                {
                    var (committed, remaining) = TaxonomyNamePolicy.TryParsePartialTagInput(_tagSearchText);
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

                    tokens = TaxonomyNamePolicy.ParseTagTokens(remaining);
                    _tagSearchText = string.Empty;
                    RaisePropertyChanged(nameof(TagSearchText));
                }
            }).ConfigureAwait(false);

            foreach (var token in tokens)
            {
                await EnsureTagAndSelectAsync(token.CanonicalName, token.DisplayName).ConfigureAwait(false);
            }

            await UiDispatch.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    RefreshTagSuggestions();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _tagCommitGate.Release();
        }
    }

    public async Task CommitPendingTagInputForSubmissionAsync()
    {
        await _pendingTagCommit.ConfigureAwait(false);

        var hasPendingText = false;
        await UiDispatch.InvokeAsync(() =>
        {
            hasPendingText = !_disposed && !string.IsNullOrWhiteSpace(_tagSearchText);
        }).ConfigureAwait(false);

        if (!hasPendingText)
        {
            return;
        }

        var commitTask = CommitTagTokensCoreAsync(isPartial: false);
        _pendingTagCommit = commitTask;
        await commitTask.ConfigureAwait(false);
    }

    private async Task EnsureTagAndSelectAsync(string canonicalName, string displayName)
    {
        var alreadySelected = false;
        TagOptionViewModel? existing = null;
        await UiDispatch.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            alreadySelected = SelectedTags.Any(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
            existing = _allTags.FirstOrDefault(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
        }).ConfigureAwait(false);

        if (_disposed || alreadySelected)
        {
            return;
        }

        if (existing is not null)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    AddTag(existing);
                }
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            await _catalog!.SettingsWrites.CreateTagAsync(id, displayName, _lifetime.Token).ConfigureAwait(false);
            var option = new TagOptionViewModel(id, displayName);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _allTags.Add(option);
                AddTag(option);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            IReadOnlyList<TagRecord> tags;
            try
            {
                tags = await _catalog!.SettingsReads.GetAllTagsAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            var resolvedDuplicate = false;
            await UiDispatch.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _allTags.Clear();
                _allTags.AddRange(tags
                    .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(tag => new TagOptionViewModel(tag.TagId, tag.Name)));
                var match = _allTags.FirstOrDefault(tag => TaxonomyNamePolicy.AreSameName(tag.Name, canonicalName));
                if (match is not null)
                {
                    AddTag(match);
                    resolvedDuplicate = true;
                }
                RefreshTagSuggestions();
            }).ConfigureAwait(false);

            if (!resolvedDuplicate && !_disposed)
            {
                throw;
            }
        }
    }

    public int Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, Math.Clamp(value, 0, 5)))
            {
                ScheduleDraftSave();
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                ScheduleDraftSave();
            }
        }
    }

    public string Overview
    {
        get => _overview;
        set
        {
            if (SetProperty(ref _overview, value ?? string.Empty))
            {
                ScheduleDraftSave();
            }
        }
    }

    public ObservableCollection<ImportVisualOption> CoverOptions { get; } = [];
    public ObservableCollection<ImportVisualOption> BannerOptions { get; } = [];

    public bool IsCoverPending => !_candidatesLoaded || (CoverOptions.Count == 0 && IsPreparing);
    public bool IsBannerPending => !_candidatesLoaded || (BannerOptions.Count == 0 && IsPreparing);
    public bool HasNoCoverOptions => _candidatesLoaded && CoverOptions.Count == 0 && !IsPreparing;
    public bool HasNoBannerOptions => _candidatesLoaded && BannerOptions.Count == 0 && !IsPreparing;
    public bool HasWeakCoverOptions => CoverOptions.Count > 0 && CoverOptions.All(static option => !option.IsRecommended);
    public bool HasWeakBannerOptions => BannerOptions.Count > 0 && BannerOptions.All(static option => !option.IsRecommended);
    public ImportVisualOption? SelectedCover => _selectedCover;
    public ImportVisualOption? SelectedBanner => _selectedBanner;
    public string ProfileSummaryText => _choice switch
    {
        ImportDestinationChoice.Existing => _selectedExistingProfile?.DisplayName ?? "—",
        _ => string.IsNullOrWhiteSpace(_newProfileName) ? "—" : _newProfileName.Trim(),
    };

    public string DestinationDisplayName => _choice switch
    {
        ImportDestinationChoice.Existing => _selectedExistingProfile?.DisplayName ?? SurfaceText.Get("Import.Destination.Current", "current Profile"),
        _ => string.IsNullOrWhiteSpace(_newProfileName) ? SurfaceText.Get("Import.Destination.Current", "current Profile") : _newProfileName.Trim(),
    };

    public string TransferText => SurfaceText.Get(
        "ImportWizard.Transfer.Copy",
        "Your files will be copied into your Vault. The originals stay exactly where they are.");

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                RaiseValidity();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);
    public bool CanContinue => _initialized && IsChooseProfileMode && IsProfileChoiceComplete && !_isImporting && _finalizer is not null;
    public bool CanImport => _initialized && IsVerifyMode && !IsPreparing
        && (_lockedDestination is not null || IsProfileChoiceComplete)
        && !HasProblems && _admittedCount > 0 && !_isImporting && !_isReviewResolving && _publication is not null;

    public bool IsProfileChoiceComplete => _choice switch
    {
        ImportDestinationChoice.New => ProfileRules.TryValidateDisplayName(_newProfileName, out _, out _),
        ImportDestinationChoice.Existing => _selectedExistingProfile is not null,
        _ => false,
    };

    public async Task InitializeAsync()
    {
        using var measure = PerfTrace.Measure("import.wizard.open", 150);
        if (_operations is null || _catalog is null)
        {
            _candidatesLoaded = true;
            return;
        }

        var token = _lifetime.Token;
        var model = await Task.Run(() => _operations.LoadVerificationReadModelAsync(UnitId, token), token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        VerificationDraftV1? draft = null;
        if (model is not null)
        {
            draft = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            RestoreDraft(draft);
            if (model.CommitOperationId is not null || model.LibraryCommitState.HasReachedDomainCommit())
            {
                var destinationKind = model.DestinationKind is { } kind
                    ? DbEnum.ParseDestinationKind(kind) : draft.Destination.Kind;
                _lockedDestination = draft.Destination with
                {
                    Kind = destinationKind,
                    ProfileId = destinationKind == DestinationKind.ExistingNormal
                        ? model.DestinationProfileId ?? draft.Destination.ProfileId : null,
                };
            }
        }

        await LoadOrganizationAsync(token).ConfigureAwait(true);
        var confirmed = model?.VerificationStep >= (int)ImportWizardStep.Details && _importRequested;
        var canReview = _lockedDestination is not null || (confirmed && IsProfileChoiceComplete);
        _step = canReview ? ImportWizardStep.Details : ImportWizardStep.Profile;
        if (!canReview)
        {
            if (draft?.Destination.Kind is not null && !IsProfileChoiceComplete)
            {
                ErrorMessage = SurfaceText.Get("ImportWizard.ChooseProfile.Again", "The saved destination is missing or unavailable. Choose a Profile to continue.");
                if (_importRequested)
                    _invalidConfirmedDraftJson = model!.VerificationDraftJson;
            }
            _importRequested = false;
            _publishWhenReady = false;
        }
        _initialized = true;
        RaisePropertyChanged(string.Empty);
        RaiseValidity();
        await RefreshHintsAsync(token).ConfigureAwait(true);
        if (_choice == ImportDestinationChoice.Existing)
        {
            ScheduleProfileSearch();
        }
        TaskObserver.Observe(KeepHintsFreshAsync(token), "Import overlay background refresh");
    }

    private void RestoreDraft(VerificationDraftV1 draft)
    {
        _importRequested = draft.IsImportRequested;
        _publishWhenReady = draft.PublishWhenReady;
        switch (draft.Destination.Kind)
        {
            case DestinationKind.ExistingNormal:
                _choice = ImportDestinationChoice.Existing;
                _pendingExistingProfileId = draft.Destination.ProfileId;
                break;
            case DestinationKind.NewNormal when draft.Destination.NewProfile is { } profile:
                _choice = ImportDestinationChoice.New;
                _newProfileName = profile.DisplayName;
                _selectedCategoryId = profile.CategoryId;
                _rating = profile.Rating ?? 0;
                _isFavorite = profile.Favorite ?? false;
                _overview = profile.Overview ?? string.Empty;
                _pendingTagIds = profile.TagIds;
                break;
        }

        _pendingCoverMediaId = draft.Appearance.CoverMediaId;
        _pendingCoverSourceKind = draft.Appearance.CoverSourceKind;
        _pendingCoverTimestampMilliseconds = draft.Appearance.CoverVideoTimestampMilliseconds;
        _pendingBannerMediaId = draft.Appearance.BannerMediaId;
        _coverIntent = draft.Appearance.CoverIntent;
        _bannerIntent = draft.Appearance.BannerIntent;
        _coverTouched = draft.Appearance.CoverIntent != VerificationAppearanceIntent.Unchanged;
        _bannerTouched = draft.Appearance.BannerIntent != VerificationAppearanceIntent.Unchanged;
        _duplicateDecisions.Clear();
        _duplicateDecisions.AddRange(draft.DuplicateDecisions ?? []);
        _collisionDecisions.Clear();
        _collisionDecisions.AddRange(draft.ProfileCollisionDecisions ?? []);
        _faceDecisions = draft.FaceDecisions;
        _acknowledgedMissingDependencyItemIds = draft.AcknowledgedMissingDependencyItemIds ?? [];
        _attentionItemIds = draft.AttentionItemIds ?? [];
        RaisePropertyChanged(string.Empty);
    }

    private Guid? _pendingExistingProfileId;
    private IReadOnlyList<string>? _pendingTagIds;
    private Guid? _pendingCoverMediaId;
    private string? _pendingCoverSourceKind;
    private long? _pendingCoverTimestampMilliseconds;
    private Guid? _pendingBannerMediaId;

    private async Task LoadOrganizationAsync(CancellationToken token)
    {
        try
        {
            var categories = await Task.Run(() => _catalog!.SettingsReads.GetAllCategoriesAsync(token), token).ConfigureAwait(true);
            var tags = await Task.Run(() => _catalog!.SettingsReads.GetAllTagsAsync(token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            Categories.Clear();
            foreach (var category in categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(new CategoryOptionViewModel(category.CategoryId, category.Name));
            }

            _allTags.Clear();
            _allTags.AddRange(tags
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(tag => new TagOptionViewModel(tag.TagId, tag.Name)));

            if (_pendingTagIds is { } pending)
            {
                foreach (var tag in _allTags.Where(tag => pending.Contains(tag.TagId, StringComparer.Ordinal)))
                {
                    SelectedTags.Add(tag);
                }

                _pendingTagIds = null;
            }

            RefreshTagSuggestions();
            RaisePropertyChanged(nameof(SelectedCategoryId));
            RaiseSummary();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Import overlay taxonomy could not be read: {0}", exception.GetType().Name);
        }

        if (_pendingExistingProfileId is { } existingId)
        {
            try
            {
                var profileName = await Task.Run(() => _operations!.ReadActiveDestinationNameAsync(existingId, token), token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (profileName is not null)
                {
                    UseExistingProfile(existingId, profileName);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Import overlay could not restore the chosen profile: {0}", exception.GetType().Name);
            }

            _pendingExistingProfileId = null;
        }
    }

    private async Task KeepHintsFreshAsync(CancellationToken token)
    {
        // Catalog/finalizer signals are the primary refresh path. This bounded timer is recovery-only
        // so a missed notification can never strand the wizard in a stale preparation state.
        while (!token.IsCancellationRequested && (IsPreparing || !_candidatesLoaded))
        {
            await Task.Delay(HintRefreshInterval, token).ConfigureAwait(true);
            await RefreshHintsAsync(token).ConfigureAwait(true);
        }
    }

    private void OnCatalogInvalidated(object? sender, CatalogInvalidation invalidation)
    {
        var sections = invalidation.DomainKind switch
        {
            CatalogInvalidationDomain.Import => HintSection.Progress | HintSection.Review,
            CatalogInvalidationDomain.Media => HintSection.Progress | HintSection.Review | HintSection.Appearance,
            CatalogInvalidationDomain.Face => HintSection.Faces | HintSection.Review,
            CatalogInvalidationDomain.Appearance => HintSection.Appearance,
            CatalogInvalidationDomain.Profile => HintSection.All,
            _ => HintSection.None,
        };
        if (sections != 0) SignalHints(sections);
    }

    private void OnFinalizerChanged(object? sender, EventArgs args) => SignalHints(HintSection.All);

    private void SignalHints(HintSection sections)
    {
        Interlocked.Or(ref _pendingHintSections, (int)sections);
        _hintRefreshCoalescer?.Signal();
    }

    public Task RefreshHintsAsync(CancellationToken cancellationToken = default) =>
        RefreshHintsAsync(HintSection.All, cancellationToken);

    private Task RefreshSignalledHintsAsync(CancellationToken token) =>
        RefreshHintsAsync((HintSection)Interlocked.Exchange(ref _pendingHintSections, 0), token);

    private async Task RefreshHintsAsync(HintSection sections, CancellationToken cancellationToken)
    {
        if (_disposed || _operations is null || _catalog is null || sections == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await Task.Run(() => ReadHintsAsync(sections, linked.Token)).ConfigureAwait(false);
    }

    private async Task ReadHintsAsync(HintSection sections, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _hintRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            using var measure = PerfTrace.Measure("import.wizard.hints", 150);
            if (sections.HasFlag(HintSection.Progress))
            {
                var progress = await _catalog!.ImportReads.ListUnitProgressAsync([UnitId], cancellationToken).ConfigureAwait(false);
                await DispatchAsync(() => ApplyProgress(progress.GetValueOrDefault(UnitId))).ConfigureAwait(false);
            }
            if (sections.HasFlag(HintSection.Review))
            {
                VerificationDraftV1? draft = null;
                await DispatchAsync(() =>
                {
                    if (!_disposed && !cancellationToken.IsCancellationRequested) draft = BuildDraft();
                }).ConfigureAwait(false);
                if (draft is null) return;
                var matches = await _operations!.GetExactDuplicateMatchesAsync(UnitId, cancellationToken).ConfigureAwait(false);
                var shared = await ImportWizardQueries.ReadSharedMediaHintsAsync(_catalog!, matches, cancellationToken).ConfigureAwait(false);
                var problems = await _operations.GetGateProblemsAsync(UnitId, draft, matches, cancellationToken).ConfigureAwait(false);
                await DispatchAsync(() =>
                {
                    if (_disposed || cancellationToken.IsCancellationRequested) return;
                    if ((BuildDraft() with { UpdatedAtMs = 0 }).ToJson() != (draft with { UpdatedAtMs = 0 }).ToJson())
                    {
                        SignalHints(HintSection.Review);
                        return;
                    }
                    ApplyReview(matches, shared, problems);
                }).ConfigureAwait(false);
            }
            if (sections.HasFlag(HintSection.Faces))
            {
                var hints = BuildFaceHints(await ReadFaceCandidatesSafelyAsync(cancellationToken).ConfigureAwait(false));
                await DispatchAsync(() => { if (!_disposed) ReplaceIfChanged(FaceHints, hints); }).ConfigureAwait(false);
            }
            if (sections.HasFlag(HintSection.Appearance))
            {
                var appearance = await _operations!.GetAppearanceCandidatesAsync(UnitId, cancellationToken).ConfigureAwait(false);
                await DispatchAsync(() => ApplyAppearance(appearance)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Import overlay hints could not be read: {0}", exception.GetType().Name);
        }
        finally
        {
            if (entered) _hintRefreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<VerificationFaceCandidate>> ReadFaceCandidatesSafelyAsync(CancellationToken token)
    {
        try
        {
            return await _operations!.GetFaceCandidatesAsync(UnitId, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceInformation("Face hints unavailable for import: {0}", exception.GetType().Name);
            return [];
        }
    }

    private static Task DispatchAsync(Action action) => UiDispatch.InvokeAsync(action);

    private void ApplyProgress(ImportUnitProgress? progress)
    {
        if (_disposed || progress is null) return;
        _totalCount = progress.TotalItemCount;
        _checkedCount = progress.CheckedItemCount;
        _admittedCount = progress.AdmittedItemCount;
        RaisePropertyChanged(nameof(MediaSummaryText));
        RaisePropertyChanged(nameof(TransferText));
        RaisePreparationStatus();
    }

    private void ApplyReview(
        IReadOnlyList<ImportDuplicateMatch> matches,
        IReadOnlyList<ImportProfileHint> shared,
        VerificationGateProblems problems)
    {
        if (_disposed)
        {
            return;
        }

        _alreadyInLibraryCount = matches
            .Where(match => match.MatchedMediaState == Media.MediaState.Active)
            .Select(match => match.ImportItemId)
            .Distinct()
            .Count();

        ReplaceIfChanged(SharedMediaHints, shared);
        ReplaceProblems(DuplicateProblems, problems.Duplicates, static problem => problem.ImportItemId);
        ReplaceProblems(ProfileCollisionProblems, problems.ProfileCollisions, static problem => problem.ImportItemId);
        ReplaceProblems(PreparationProblems, problems.PreparationFailures, static problem => problem.ImportItemId,
            static (current, replacement) => current.SourceFileName == replacement.SourceFileName
                && current.MissingDependencies == replacement.MissingDependencies
                && current.Failures.SequenceEqual(replacement.Failures));
        _hasPreparationPending = problems.RequiredPreparationPending;
        RaisePropertyChanged(nameof(MediaSummaryText));
        RaisePropertyChanged(nameof(HasPreparationPending));
        RaisePropertyChanged(nameof(HasProblems));
        RaisePreparationStatus();
    }

    private void ApplyAppearance(VerificationAppearanceCandidates candidates)
    {
        if (_disposed) return;
        MergeOptions(CoverOptions, candidates.Covers.Select(c => new ImportVisualOption(c)), isCover: true);
        MergeOptions(BannerOptions, candidates.Banners.Select(b => new ImportVisualOption(b)), isCover: false);
        _candidatesLoaded = true;
        AutoSelectVisuals();
        RaisePreparationStatus();
    }

    private void RaisePreparationStatus()
    {
        RaisePropertyChanged(nameof(PreparationText));
        RaisePropertyChanged(nameof(IsPreparing));
        RaisePropertyChanged(nameof(IsCoverPending));
        RaisePropertyChanged(nameof(IsBannerPending));
        RaisePropertyChanged(nameof(HasNoCoverOptions));
        RaisePropertyChanged(nameof(HasNoBannerOptions));
        RaisePropertyChanged(nameof(HasWeakCoverOptions));
        RaisePropertyChanged(nameof(HasWeakBannerOptions));
        RaiseValidity();
    }

    private static void ReplaceProblems<T>(
        ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, Guid> id, Func<T, T, bool>? sameDetails = null)
    {
        var next = source.ToDictionary(id);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            var current = target[index];
            if (!next.Remove(id(current), out var replacement))
            {
                target.RemoveAt(index);
            }
            else if (!(sameDetails?.Invoke(current, replacement) ?? EqualityComparer<T>.Default.Equals(current, replacement)))
            {
                target[index] = replacement;
            }
        }

        foreach (var item in source)
        {
            if (next.Remove(id(item))) target.Add(item);
        }
    }

    private static IReadOnlyList<ImportProfileHint> BuildFaceHints(IReadOnlyList<VerificationFaceCandidate> faces) =>
        [.. faces
            .Where(face => face.SuggestedProfileId.HasValue
                && !string.IsNullOrWhiteSpace(face.SuggestedProfileName)
                && face.SuggestionSimilarity is { } similarity
                && similarity >= ProfilingFaceAnalysisJobOperation.SuggestionThreshold)
            .GroupBy(face => face.SuggestedProfileId!.Value)
            .OrderByDescending(group => group.Max(face => face.SuggestionSimilarity))
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => new ImportProfileHint(
                group.Key,
                group.First().SuggestedProfileName!,
                group.Count() == 1
                    ? SurfaceText.Get("ImportWizard.FaceHint.One", "Face match in 1 photo")
                    : SurfaceText.Format("ImportWizard.FaceHint.Many", "Face match in {0} photos", group.Count()),
                IsFaceMatch: true))
            .Take(3)];

    private static void ReplaceIfChanged(ObservableCollection<ImportProfileHint> target, IReadOnlyList<ImportProfileHint> next)
    {
        if (target.SequenceEqual(next)) return;
        target.Clear();
        foreach (var hint in next) target.Add(hint);
    }

    private void MergeOptions(ObservableCollection<ImportVisualOption> target, IEnumerable<ImportVisualOption> incoming, bool isCover)
    {
        var existing = target.ToDictionary(option => option.CandidateId, StringComparer.Ordinal);
        var ordered = new List<ImportVisualOption>();
        foreach (var option in incoming)
        {
            if (existing.TryGetValue(option.CandidateId, out var kept) && ((kept.Banner is not null && option.Banner is not null) || string.Equals(kept.PreviewPath, option.PreviewPath, StringComparison.Ordinal))
                && kept.Banner?.IsReady == option.Banner?.IsReady)
            {
                ordered.Add(kept);
            }
            else
            {
                ordered.Add(option);
                LoadPreview(option);
            }
        }

        if (ordered.Select(o => o.CandidateId).SequenceEqual(target.Select(o => o.CandidateId), StringComparer.Ordinal)
            && ordered.Zip(target).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return;
        }

        var selectedId = isCover ? _selectedCover?.CandidateId : _selectedBanner?.CandidateId;
        target.Clear();
        foreach (var option in ordered)
        {
            option.IsSelected = string.Equals(option.CandidateId, selectedId, StringComparison.Ordinal);
            target.Add(option);
        }

        if (isCover && _selectedCover is not null)
        {
            _selectedCover = ordered.FirstOrDefault(option => option.CandidateId == _selectedCover.CandidateId) ?? _selectedCover;
        }
        else if (!isCover && _selectedBanner is not null)
        {
            _selectedBanner = ordered.FirstOrDefault(option => option.CandidateId == _selectedBanner.CandidateId) ?? _selectedBanner;
        }
    }

    private void LoadPreview(ImportVisualOption option)
    {
        if (option.Banner is not null && _mediaAssets is not null)
        {
            TaskObserver.Observe(LoadCandidatePreviewAsync(option), "ImportWizardViewModel.LoadBannerPreviewAsync");
            return;
        }
        if (!string.IsNullOrWhiteSpace(option.PreviewPath))
        {
            option.Preview = ImageRef.FromPath(option.PreviewPath, PreviewDecodeWidth, $"import-visual:{option.CandidateId}");
            TaskObserver.Observe(
                _images.PrefetchAsync(option.PreviewPath, PreviewDecodeWidth, _lifetime.Token),
                "ImportWizardViewModel.PrefetchPreviewAsync");
            return;
        }

        if (_mediaAssets is not null)
        {
            TaskObserver.Observe(
                LoadCandidatePreviewAsync(option),
                "ImportWizardViewModel.LoadCandidatePreviewAsync");
        }
    }

    private async Task LoadCandidatePreviewAsync(ImportVisualOption option)
    {
        string? motionPath = null;
        if (option.Banner is not null)
        {
            var motion = await _mediaAssets!.ResolveImportCandidateAsync(
                UnitId, option.MediaId, MediaAssetRole.Hover, _lifetime.Token).ConfigureAwait(false);
            motionPath = motion.State == MediaAssetResourceState.Ready ? motion.PhysicalPath : null;
            if (motionPath is null && _catalog is not null)
            {
                var published = await _catalog.MediaAssetReads
                    .GetPublicMediaAssetAsync(option.MediaId, MediaAssetRole.Hover, _lifetime.Token)
                    .ConfigureAwait(false);
                motionPath = ResolveMediaAssetPath(published);
            }
        }

        var mediaAsset = await _mediaAssets!.ResolveImportCandidateAsync(
            UnitId, option.MediaId, MediaAssetRole.Thumbnail, _lifetime.Token).ConfigureAwait(false);
        if (mediaAsset.State != MediaAssetResourceState.Ready)
        {
            mediaAsset = await _mediaAssets.ResolveMediaThumbnailAsync(option.MediaId, _lifetime.Token).ConfigureAwait(false);
        }
        var path = mediaAsset.PhysicalPath;

        if (_disposed)
        {
            return;
        }

        await DispatchAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            option.MotionPath = motionPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                option.SetPreviewPath(path);
                option.Preview = ImageRef.FromPath(path, PreviewDecodeWidth, $"import-visual:{option.CandidateId}");
            }
        }).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(path))
            await _images.PrefetchAsync(path, PreviewDecodeWidth, _lifetime.Token).ConfigureAwait(false);
    }

    public const int PreviewDecodeWidth = 320;

    private void AutoSelectVisuals()
    {
        if (_coverIntent != VerificationAppearanceIntent.Clear
            && ((_choice == ImportDestinationChoice.New && !_coverTouched)
                || (_pendingCoverMediaId is not null && _selectedCover is null)))
        {
            var preferred = _pendingCoverMediaId is { } coverId
                ? CoverOptions.FirstOrDefault(option => MatchesPendingCover(option, coverId))
                : CoverOptions.FirstOrDefault(option => option.IsRecommended) ?? CoverOptions.FirstOrDefault();
            if (preferred is not null)
            {
                SelectCover(preferred, byUser: false);
                _pendingCoverMediaId = null;
            }
        }

        if (_bannerIntent != VerificationAppearanceIntent.Clear
            && ((_choice == ImportDestinationChoice.New && !_bannerTouched)
                || (_pendingBannerMediaId is not null && _selectedBanner is null)))
        {
            var preferred = _pendingBannerMediaId is { } bannerId
                ? BannerOptions.FirstOrDefault(option => MatchesPendingBanner(option, bannerId))
                : BannerOptions.FirstOrDefault(option => option.IsRecommended) ?? BannerOptions.FirstOrDefault();
            if (preferred is not null)
            {
                SelectBanner(preferred, byUser: false);
                _pendingBannerMediaId = null;
            }
        }
    }

    private string? ResolveMediaAssetPath(MediaAssetRecord? asset)
    {
        if (_catalog is null || asset is null || asset.State != MediaAssetState.Ready)
        {
            return null;
        }

        try
        {
            return _catalog.Paths.ResolveVaultRelativePath(VaultPathArea.MediaAssets, asset.RelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Import MediaAsset path resolution failed: {0}", exception.GetType().Name);
            return null;
        }
    }

    private bool MatchesPendingCover(ImportVisualOption option, Guid assetId)
    {
        var cover = option.Cover;
        return cover is not null
            && cover.MediaId == assetId
            && (string.IsNullOrWhiteSpace(_pendingCoverSourceKind)
                || string.Equals(cover.SourceKind.ToString(), _pendingCoverSourceKind, StringComparison.OrdinalIgnoreCase))
            && cover.TimestampMilliseconds == _pendingCoverTimestampMilliseconds;
    }

    private static bool MatchesPendingBanner(ImportVisualOption option, Guid assetId) =>
        option.Banner?.MediaId == assetId;

    public void SelectCover(ImportVisualOption? option, bool byUser)
    {
        if (byUser)
        {
            _coverTouched = true;
            _coverIntent = option is null ? VerificationAppearanceIntent.Clear : VerificationAppearanceIntent.Set;
            _pendingCoverMediaId = null;
            _pendingCoverSourceKind = null;
            _pendingCoverTimestampMilliseconds = null;
        }

        _selectedCover = option;
        foreach (var candidate in CoverOptions) candidate.IsSelected = ReferenceEquals(candidate, option);
        RaisePropertyChanged(nameof(SelectedCover));
        if (byUser)
        {
            ScheduleDraftSave();
        }
    }

    public void SelectBanner(ImportVisualOption? option, bool byUser)
    {
        if (byUser)
        {
            _bannerTouched = true;
            _bannerIntent = option is null ? VerificationAppearanceIntent.Clear : VerificationAppearanceIntent.Set;
            _pendingBannerMediaId = null;
        }

        _selectedBanner = option;
        foreach (var candidate in BannerOptions) candidate.IsSelected = ReferenceEquals(candidate, option);
        RaisePropertyChanged(nameof(SelectedBanner));
        if (byUser)
        {
            ScheduleDraftSave();
        }
    }

    private void ScheduleNameCheck()
    {
        _nameCheck?.Cancel();
        _nameCheck?.Dispose();
        _nameCheck = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TaskObserver.Observe(CheckNameAsync(_newProfileName, _nameCheck.Token), "Import overlay name check");
    }

    private async Task CheckNameAsync(string name, CancellationToken token)
    {
        await Task.Delay(SearchDebounce, token).ConfigureAwait(true);
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || _operations is null)
        {
            DuplicateNameProfile = null;
            return;
        }

        var matches = await _operations.SearchProfilesAsync(trimmed, 10, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !string.Equals(_newProfileName.Trim(), trimmed, StringComparison.Ordinal)) return;
        var exact = matches.FirstOrDefault(profile => string.Equals(profile.DisplayName.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase));
        DuplicateNameProfile = exact is null ? null : new ProfileLookupItemViewModel(exact);
    }

    private void ScheduleProfileSearch()
    {
        _profileSearch?.Cancel();
        _profileSearch?.Dispose();
        _profileSearch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TaskObserver.Observe(SearchProfilesAsync(_existingSearchText, _profileSearch.Token), "Import overlay profile search");
    }

    private async Task SearchProfilesAsync(string query, CancellationToken token)
    {
        await Task.Delay(SearchDebounce, token).ConfigureAwait(true);
        if (_operations is null) return;

        var limit = string.IsNullOrWhiteSpace(query) ? 100 : 40;
        var results = await _operations.SearchProfilesAsync(query, limit, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !string.Equals(_existingSearchText, query, StringComparison.Ordinal))
        {
            return;
        }

        var faceHintIds = FaceHints.Select(static hint => hint.ProfileId).ToHashSet();
        var sharedHintIds = SharedMediaHints.Select(static hint => hint.ProfileId).ToHashSet();
        var selectedProfileId = _selectedExistingProfile?.ProfileId;
        var items = results
            .OrderByDescending(result => faceHintIds.Contains(result.ProfileId))
            .ThenByDescending(result => sharedHintIds.Contains(result.ProfileId))
            .ThenBy(result => result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(24)
            .Select(static result => new ProfileLookupItemViewModel(result))
            .ToList();

        await WarmProfileLookupCoversAsync(items, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || !string.Equals(_existingSearchText, query, StringComparison.Ordinal))
        {
            return;
        }

        ExistingProfiles.Clear();
        foreach (var item in items)
        {
            ExistingProfiles.Add(item);
        }

        if (selectedProfileId is { } selectedId
            && items.FirstOrDefault(item => item.ProfileId == selectedId) is { } selected)
        {
            SelectedExistingProfile = selected;
        }
    }

    private async Task WarmProfileLookupCoversAsync(
        IReadOnlyList<ProfileLookupItemViewModel> profiles,
        CancellationToken token)
    {
        if (_mediaAssets is null || profiles.Count == 0)
        {
            return;
        }

        var resolutions = await Task.WhenAll(profiles.Select(async profile =>
        {
            try
            {
                var resource = await _mediaAssets
                    .ResolveSelectedProfileCoverAsync(profile.ProfileId, token)
                    .ConfigureAwait(false);
                return (
                    Profile: profile,
                    Cover: resource.State == MediaAssetResourceState.Ready
                        ? ImageRef.FromPath(resource.PhysicalPath, PreviewDecodeWidth)
                        : null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceInformation(
                    "Import profile cover unavailable for {0}: {1}",
                    profile.ProfileId,
                    exception.GetType().Name);
                return (Profile: profile, Cover: (ImageRef?)null);
            }
        })).ConfigureAwait(false);

        if (token.IsCancellationRequested || _disposed)
        {
            return;
        }

        await UiDispatch.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            foreach (var (profile, cover) in resolutions)
            {
                profile.CoverSource = cover;
            }
        }).ConfigureAwait(false);
    }

    private void UseExistingProfile(Guid profileId, string displayName)
    {
        var existing = ExistingProfiles.FirstOrDefault(profile => profile.ProfileId == profileId);
        if (existing is null)
        {
            existing = new ProfileLookupItemViewModel(new ProfileLookupResult(profileId, displayName, string.Empty));
            ExistingProfiles.Insert(0, existing);
        }

        SelectedExistingProfile = existing;
        Choice = ImportDestinationChoice.Existing;
    }

    private void RefreshTagSuggestions()
    {
        TagSuggestions.Clear();
        var attached = SelectedTags.Select(tag => tag.TagId).ToHashSet(StringComparer.Ordinal);
        var query = _tagSearchText.Trim();
        var normalizedQuery = TaxonomyNamePolicy.TryNormalize(query);

        var candidates = _allTags
            .Where(tag => !attached.Contains(tag.TagId))
            .Where(tag => normalizedQuery is null || TaxonomyNamePolicy.TryNormalize(tag.Name) is { } nk && nk.Contains(normalizedQuery, StringComparison.Ordinal))
            .OrderBy(tag =>
            {
                var nk = TaxonomyNamePolicy.TryNormalize(tag.Name);
                return normalizedQuery is not null && nk is not null && nk.StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1;
            })
            .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12);

        foreach (var tag in candidates) TagSuggestions.Add(tag);
    }

    public void AddTag(TagOptionViewModel tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (SelectedTags.Any(existing => existing.TagId == tag.TagId)) return;
        SelectedTags.Add(tag);
        TagSearchText = string.Empty;
        RefreshTagSuggestions();
        ScheduleDraftSave();
    }

    public void RemoveTag(TagOptionViewModel tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var match = SelectedTags.FirstOrDefault(existing => existing.TagId == tag.TagId);
        if (match is not null)
        {
            SelectedTags.Remove(match);
            RefreshTagSuggestions();
            ScheduleDraftSave();
        }
    }

    public async Task CreateCategoryAsync()
    {
        if (_catalog is null || TaxonomyNamePolicy.TryNormalizeDisplayName(_newCategoryName) is not { } displayName) return;
        var id = Guid.NewGuid().ToString("N")[..12];
        await _catalog.SettingsWrites.CreateCategoryAsync(id, displayName, _lifetime.Token).ConfigureAwait(true);
        var option = new CategoryOptionViewModel(id, displayName);
        Categories.Add(option);
        SelectedCategoryId = id;
        NewCategoryName = string.Empty;
    }

    private AsyncRelayCommand ReviewCommand<T>(ReviewResolutionAction action, Func<T, Guid> itemId) where T : class =>
        new(parameter => parameter is T problem ? ResolveReviewAsync(action, itemId(problem)) : Task.CompletedTask,
            parameter => parameter is T && CanResolveReview, ReportImportFailure);

    private void SetReviewResolving(bool value)
    {
        if (!SetProperty(ref _isReviewResolving, value, nameof(IsReviewResolving))) return;
        foreach (var command in new[] { ResolveDuplicateReuseCommand, ResolveDuplicateSkipCommand, KeepCollisionCommand,
                     MoveCollisionCommand, SkipCollisionCommand, RetryPreparationCommand, RemovePreparationItemCommand,
                     ResolveAllReviewCommand, CloseCommand })
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseValidity();
    }

    private async Task ResolveReviewAsync(ReviewResolutionAction action, Guid? itemId)
    {
        if (!CanResolveReview || _operations is null) return;
        SetReviewResolving(true);
        ErrorMessage = null;
        var token = _lifetime.Token;
        try
        {
            var duplicates = DuplicateProblems.ToArray();
            var collisions = ProfileCollisionProblems.ToArray();
            var ids = (action switch
            {
                ReviewResolutionAction.RetryPreparation => PreparationProblems.Where(problem => !problem.MissingDependencies)
                    .Select(problem => problem.ImportItemId),
                ReviewResolutionAction.ReuseDuplicate => duplicates.Select(problem => problem.ImportItemId),
                ReviewResolutionAction.KeepDestination or ReviewResolutionAction.MoveToRecognizedProfile =>
                    collisions.Select(problem => problem.ImportItemId),
                ReviewResolutionAction.Remove => PreparationProblems.Select(problem => problem.ImportItemId)
                    .Concat(duplicates.Select(problem => problem.ImportItemId))
                    .Concat(collisions.Select(problem => problem.ImportItemId)),
                _ => Enumerable.Empty<Guid>(),
            }).Where(id => itemId is null || itemId == id).ToHashSet();
            if (ids.Count == 0) return;

            if (action == ReviewResolutionAction.RetryPreparation)
            {
                foreach (var id in ids)
                {
                    var result = await _operations.RetryRequiredPreparationAsync(UnitId, id, token).ConfigureAwait(true);
                    if (!result.IsStarted && result.Code != "PREPARATION_ALREADY_RECOVERED")
                        ErrorMessage = result.UserMessage
                            ?? SurfaceText.Get("Import.PreparationRetryUnavailable", "Media preparation could not be retried.");
                }
                return;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var model = await _operations.LoadVerificationReadModelAsync(UnitId, token).ConfigureAwait(true);
                if (model is null || !model.State.CanCancelUnpublished())
                    throw new CatalogConcurrencyConflictException("The import is no longer available for Review.");

                var persisted = VerificationDraftV1.FromJson(model.VerificationDraftJson, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                var current = await _operations.GetGateProblemsAsync(UnitId, BuildDraft(), token).ConfigureAwait(true);
                var currentDuplicates = current.Duplicates.ToDictionary(problem => problem.ImportItemId);
                var currentCollisions = current.ProfileCollisions.ToDictionary(problem => problem.ImportItemId);
                var currentProblems = current.PreparationFailures.Select(problem => problem.ImportItemId)
                    .Concat(currentDuplicates.Keys).Concat(currentCollisions.Keys).ToHashSet();
                if (!ids.IsSubsetOf(currentProblems)
                    || action == ReviewResolutionAction.ReuseDuplicate && duplicates.Where(problem => ids.Contains(problem.ImportItemId))
                        .Any(problem => !currentDuplicates.TryGetValue(problem.ImportItemId, out var match) || match.MatchedMediaId != problem.MatchedMediaId)
                    || (action is ReviewResolutionAction.KeepDestination or ReviewResolutionAction.MoveToRecognizedProfile)
                        && collisions.Where(problem => ids.Contains(problem.ImportItemId))
                        .Any(problem => !currentCollisions.TryGetValue(problem.ImportItemId, out var match)
                            || match.CandidateProfileId != problem.CandidateProfileId || match.MediaId != problem.MediaId))
                    throw new CatalogConcurrencyConflictException("One or more Review items have changed. Refresh and try again.");

                var draft = BuildDraft();
                var duplicateDecisions = (draft.DuplicateDecisions ?? []).ToDictionary(decision => decision.ImportItemId);
                var collisionDecisions = (draft.ProfileCollisionDecisions ?? []).ToDictionary(decision => decision.ImportItemId);
                var changes = new List<VerificationOperations.ReviewItemMutation>(ids.Count);
                draft = BuildReviewDecisionDraft(action, ids, draft, duplicateDecisions, collisionDecisions,
                    currentDuplicates, currentCollisions, changes) with
                {
                    CurrentStep = Math.Max(model.VerificationStep, draft.CurrentStep),
                    ImportRequested = persisted.IsImportRequested,
                    PublishWhenReady = persisted.PublishWhenReady,
                };
                try
                {
                    await _operations.UpdateReviewDraftAsync(UnitId, draft, model.RowVersion, changes, token).ConfigureAwait(true);
                }
                catch (CatalogConcurrencyConflictException) when (attempt < 2)
                {
                    continue;
                }

                ApplyResolvedReviewDraft(action, ids, draft);
                return;
            }
        }
        finally
        {
            try
            {
                await RefreshHintsAsync(token).ConfigureAwait(true);
            }
            finally
            {
                SetReviewResolving(false);
                ScheduleDraftSave();
            }
        }
    }

    private static VerificationDraftV1 BuildReviewDecisionDraft(
        ReviewResolutionAction action, HashSet<Guid> ids, VerificationDraftV1 draft,
        Dictionary<Guid, StagedDuplicateDecision> duplicateDecisions,
        Dictionary<Guid, StagedProfileCollisionDecision> collisionDecisions,
        Dictionary<Guid, VerificationDuplicateProblem> currentDuplicates,
        Dictionary<Guid, VerificationProfileCollisionProblem> currentCollisions,
        List<VerificationOperations.ReviewItemMutation> changes)
    {
        if (action == ReviewResolutionAction.Remove)
        {
            foreach (var id in ids)
            {
                var isDuplicate = currentDuplicates.ContainsKey(id) || duplicateDecisions.ContainsKey(id);
                changes.Add(new(id, isDuplicate ? DuplicateDecision.Skip : null));
                if (isDuplicate) duplicateDecisions[id] = new(id, DuplicateDecisionAction.Skip);
                collisionDecisions.Remove(id);
            }
            var appearance = draft.Appearance;
            if (appearance.CoverImportItemId is { } cover && ids.Contains(cover))
                appearance = appearance with { CoverMediaId = null, CoverImportItemId = null, CoverSourceKind = null,
                    CoverMediaAssetId = null, CoverVideoTimestampMilliseconds = null, CoverIntent = VerificationAppearanceIntent.Unchanged };
            if (appearance.BannerImportItemId is { } banner && ids.Contains(banner))
                appearance = appearance with { BannerMediaId = null, BannerImportItemId = null, BannerMediaAssetId = null,
                    BannerIntent = VerificationAppearanceIntent.Unchanged };
            draft = draft with
            {
                Appearance = appearance,
                FaceDecisions = draft.FaceDecisions.Where(decision => decision.SourceImportItemId is not { } id || !ids.Contains(id)).ToArray(),
            };
        }
        else if (action == ReviewResolutionAction.ReuseDuplicate)
        {
            foreach (var id in ids)
            {
                changes.Add(new(id, DuplicateDecision.Reuse, currentDuplicates[id].MatchedMediaId));
                duplicateDecisions[id] = new(id, DuplicateDecisionAction.Reuse);
            }
        }
        else
        {
            foreach (var id in ids)
            {
                var move = action == ReviewResolutionAction.MoveToRecognizedProfile;
                collisionDecisions[id] = new(id, move ? ProfileCollisionAction.MoveToProfile : ProfileCollisionAction.KeepDestination,
                    move ? currentCollisions[id].CandidateProfileId : null);
            }
        }

        return draft with
        {
            DuplicateDecisions = duplicateDecisions.Values.ToArray(),
            ProfileCollisionDecisions = collisionDecisions.Values.ToArray(),
        };
    }

    private void ApplyResolvedReviewDraft(ReviewResolutionAction action, HashSet<Guid> ids, VerificationDraftV1 draft)
    {
        _duplicateDecisions.Clear();
        _duplicateDecisions.AddRange(draft.DuplicateDecisions ?? []);
        _collisionDecisions.Clear();
        _collisionDecisions.AddRange(draft.ProfileCollisionDecisions ?? []);
        _faceDecisions = draft.FaceDecisions;
        if (action != ReviewResolutionAction.Remove) return;
        if (_selectedCover?.Cover?.ImportItemId is { } cover && ids.Contains(cover))
        {
            SelectCover(null, byUser: true);
            _coverTouched = false;
            _coverIntent = VerificationAppearanceIntent.Unchanged;
        }
        if (_selectedBanner?.Banner?.ImportItemId is { } banner && ids.Contains(banner))
        {
            SelectBanner(null, byUser: true);
            _bannerTouched = false;
            _bannerIntent = VerificationAppearanceIntent.Unchanged;
        }
    }

    private void ScheduleDraftSave()
    {
        if (_initialized) _draftSaveCoalescer?.Signal();
    }

    private Task PersistAutosaveDraftAsync(CancellationToken cancellationToken) =>
        PersistDraftAsync(cancellationToken);

    private Task PersistCurrentDraftAsync() => PersistDraftAsync(_lifetime.Token);

    private async Task PersistDraftAsync(CancellationToken cancellationToken)
    {
        if (_operations is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var model = await Task.Run(() => _operations.LoadVerificationReadModelAsync(UnitId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (model is null
                || model.State.IsUnitCommitted()
                || model.State is ImportUnitState.Cancelled or ImportUnitState.Committing)
            {
                return;
            }

            var persisted = VerificationDraftV1.FromJson(
                model.VerificationDraftJson, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            VerificationDraftV1? draft = null;
            bool clearInvalidConfirmation = false;
            await UiDispatch.InvokeAsync(() =>
            {
                if (_initialized && !_disposed && !_isImporting && !_isReviewResolving)
                {
                    clearInvalidConfirmation = _invalidConfirmedDraftJson == model.VerificationDraftJson
                        && model.CommitOperationId is null && !model.LibraryCommitState.HasReachedDomainCommit();
                    if (IsChooseProfileMode && persisted.IsImportRequested && !clearInvalidConfirmation)
                        return;
                    draft = BuildDraft();
                }
            }).ConfigureAwait(false);

            if (draft is null) return;

            try
            {
                // The version predates the UI snapshot. A retry takes a fresh snapshot and
                // preserves durable confirmation/publication intent written by another path.
                draft = draft with
                {
                    CurrentStep = clearInvalidConfirmation ? draft.CurrentStep : Math.Max(model.VerificationStep, draft.CurrentStep),
                    ImportRequested = !clearInvalidConfirmation && persisted.IsImportRequested,
                    PublishWhenReady = !clearInvalidConfirmation && persisted.PublishWhenReady,
                };
                await _operations
                    .UpdateDraftAsync(UnitId, draft, model.RowVersion, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (CatalogConcurrencyConflictException) when (attempt < 2)
            {
            }
        }
    }

    public async Task ContinueAsync()
    {
        if (!CanContinue || _finalizer is null) return;
        IsImporting = true;
        ErrorMessage = null;
        try
        {
            var token = _lifetime.Token;
            var draft = BuildDraft();
            if (_selectedExistingProfile is { } selected && _choice == ImportDestinationChoice.Existing
                && await Task.Run(() => _operations!.ReadActiveDestinationNameAsync(selected.ProfileId, token), token).ConfigureAwait(true) is null)
            {
                SelectedExistingProfile = null;
                ErrorMessage = SurfaceText.Get("ImportWizard.ChooseProfile.Again", "The saved destination is missing or unavailable. Choose a Profile to continue.");
                return;
            }
            token.ThrowIfCancellationRequested();
            var accepted = await Task.Run(() => _finalizer.RequestImportAsync(UnitId, draft, token), token).ConfigureAwait(true);
            if (!accepted)
            {
                ErrorMessage = SurfaceText.Get("ImportWizard.Gone", "This import is no longer available.");
                return;
            }
            _importRequested = true;
            _imported?.Invoke(UnitId);
            _close?.Invoke(this);
        }
        finally
        {
            IsImporting = false;
        }
    }

    public VerificationDraftV1 BuildDraft()
    {
        NewProfileDraft? newProfile = null;
        VerificationDestinationDraft destination;
        switch (_choice)
        {
            case ImportDestinationChoice.Existing:
                destination = new VerificationDestinationDraft(DestinationKind.ExistingNormal, _selectedExistingProfile?.ProfileId, null);
                break;
            default:
                newProfile = new NewProfileDraft(
                    DisplayName: _newProfileName.Trim(),
                    CategoryId: string.IsNullOrWhiteSpace(_selectedCategoryId) ? null : _selectedCategoryId,
                    TagIds: SelectedTags.Count == 0 ? null : [.. SelectedTags.Select(tag => tag.TagId)],
                    Rating: _rating > 0 ? _rating : null,
                    Favorite: _isFavorite,
                    Overview: string.IsNullOrWhiteSpace(_overview) ? null : _overview.Trim());
                destination = new VerificationDestinationDraft(DestinationKind.NewNormal, null, newProfile);
                break;
        }

        var cover = IsVerifyMode ? _selectedCover?.Cover : null;
        var banner = IsVerifyMode ? _selectedBanner?.Banner : null;
        var coverIntent = _coverIntent;
        var bannerIntent = _bannerIntent;
        if (_choice == ImportDestinationChoice.New)
        {
            if (coverIntent == VerificationAppearanceIntent.Unchanged && cover is not null)
            {
                coverIntent = VerificationAppearanceIntent.Set;
            }
            if (bannerIntent == VerificationAppearanceIntent.Unchanged && banner is not null)
            {
                bannerIntent = VerificationAppearanceIntent.Set;
            }
        }

        var appearance = new VerificationAppearanceDraft(
            CoverMediaId: cover?.MediaId,
            BannerMediaId: banner?.MediaId,
            CoverSourceKind: cover?.SourceKind.ToString(),
            CoverImportItemId: cover?.ImportItemId,
            BannerImportItemId: banner?.ImportItemId,
            CoverVideoTimestampMilliseconds: cover?.TimestampMilliseconds,
            CoverIntent: coverIntent,
            BannerIntent: bannerIntent,
            CoverMediaAssetId: cover is not null && Guid.TryParse(cover.CandidateId, out var coverMediaAssetId)
                ? coverMediaAssetId : null,
            BannerMediaAssetId: banner is not null && Guid.TryParse(banner.CandidateId, out var bannerMediaAssetId)
                ? bannerMediaAssetId : null);

        return new VerificationDraftV1(
            SchemaVersion: VerificationDraftV1.CurrentSchemaVersion,
            CurrentStep: (int)_step,
            Destination: _lockedDestination is { Kind: DestinationKind.NewNormal } lockedNew
                ? lockedNew with { NewProfile = newProfile ?? lockedNew.NewProfile }
                : _lockedDestination ?? destination,
            Appearance: appearance,
            FaceDecisions: _faceDecisions,
            UpdatedAtMs: _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            AcknowledgedMissingDependencyItemIds: _acknowledgedMissingDependencyItemIds,
            AttentionItemIds: _attentionItemIds,
            DuplicateDecisions: [.. _duplicateDecisions],
            ProfileCollisionDecisions: [.. _collisionDecisions],
            ImportRequested: _importRequested,
            PublishWhenReady: _publishWhenReady);
    }

    public async Task ImportAsync()
    {
        if (!CanImport || _publication is null) return;
        IsImporting = true;
        ErrorMessage = null;
        try
        {
            await CommitPendingTagInputForSubmissionAsync().ConfigureAwait(true);
            var token = _lifetime.Token;
            var draft = BuildDraft();
            var result = await Task.Run(() => _publication.PublishAsync(UnitId, draft, token), token).ConfigureAwait(true);
            if (result.Status == ImportPublicationStatus.Blocked)
            {
                ErrorMessage = result.UserMessage ?? SurfaceText.Get("ImportWizard.PublishBlocked", "Verify still has unresolved choices.");
                return;
            }
            _imported?.Invoke(UnitId);
            _close?.Invoke(this);
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void ReportImportFailure(Exception exception)
    {
        IsImporting = false;
        ErrorMessage = SurfaceText.Format(
            "ImportWizard.ImportFailed",
            "Could not save your choices. Try again. {0}",
            OperationExecution.SafeMessage(exception));
    }

    public async Task CloseAsync()
    {
        if (_isReviewResolving) return;
        if (_operations is not null && !_isImporting)
        {
            try
            {
                // Reuse the optimistic-concurrency retry path used by autosave. A pending autosave
                // can legitimately race the close button; direct one-shot UpdateDraftAsync here
                // could otherwise lose the final field edit to a row-version conflict.
                await PersistCurrentDraftAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Import draft could not be saved on close: {0}", exception.GetType().Name);
            }
        }

        _close?.Invoke(this);
    }

    private void RaiseValidity()
    {
        RaisePropertyChanged(nameof(IsProfileChoiceComplete));
        RaisePropertyChanged(nameof(CanContinue));
        RaisePropertyChanged(nameof(CanImport));
        RaisePropertyChanged(nameof(HasDuplicateName));
        (ContinueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseSummary();
    }

    private void RaiseSummary() => RaisePropertyChanged(nameof(ProfileSummaryText));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_catalog is not null)
            _catalog.WriteCoordinator.Invalidated -= OnCatalogInvalidated;
        if (_finalizer is not null)
            _finalizer.Changed -= OnFinalizerChanged;
        _hintRefreshCoalescer?.Dispose();
        _draftSaveCoalescer?.Dispose();
        _nameCheck?.Dispose();
        _profileSearch?.Dispose();
        _lifetime.Dispose();
    }
}

public static class ImportWizardQueries
{
    public static async Task<IReadOnlyList<ImportProfileHint>> ReadSharedMediaHintsAsync(
        CatalogDb catalog,
        IReadOnlyList<ImportDuplicateMatch> matches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var assetIds = matches
            .Where(match => match.MatchedMediaState == Media.MediaState.Active)
            .Select(match => match.MatchedMediaId)
            .Distinct()
            .Take(400)
            .ToList();
        if (assetIds.Count == 0) return [];

        await using var connection = await catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = new List<string>();
        for (var index = 0; index < assetIds.Count; index++)
        {
            var name = "$a" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, DbGuid.Format(assetIds[index]));
        }

        command.CommandText =
            $"""
            SELECT p.profile_id, p.display_name, COUNT(DISTINCT pa.media_id)
            FROM profile_media pa
            JOIN profiles p ON p.profile_id = pa.profile_id
            WHERE pa.relation_type = 'OWNER'
              AND pa.publication_import_unit_id IS NULL
              AND p.kind = 'NORMAL'
              AND p.trashed_at_ms IS NULL
              AND pa.media_id IN ({string.Join(",", names)})
            GROUP BY p.profile_id, p.display_name
            ORDER BY COUNT(DISTINCT pa.media_id) DESC
            LIMIT 3;
            """;

        var hints = new List<ImportProfileHint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = reader.GetInt32(2);
            hints.Add(new ImportProfileHint(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                count == 1
                    ? SurfaceText.Get("ImportWizard.SharedHint.One", "1 of these files is already in this profile")
                    : SurfaceText.Format("ImportWizard.SharedHint.Many", "{0} of these files are already in this profile", count),
                IsFaceMatch: false));
        }

        return hints;
    }

}
