using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Neuterradise.App.Localization;
using Neuterradise.App.Shell;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices;
using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.RelatedProfiles;

public sealed class RelatedProfilesViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RelatedReads? _queries;
    private readonly RelatedProfileOperations? _operations;
    private readonly NavigationCoordinator? _navigation;
    private readonly OverlayHostViewModel? _overlay;
    private Guid? _profileId;
    private RelatedProfileSummaryReadModel? _selectedRelatedProfile;
    private bool _isLoading;
    private string? _statusMessage;
    private int _relatedLoadGeneration;
    private int _evidenceLoadGeneration;
    private bool _isDisposed;

    public RelatedProfilesViewModel()
        : this(null, null, null, null, null)
    {
    }

    public RelatedProfilesViewModel(
        RelatedReads? queries,
        RelatedProfileOperations? operations = null,
        Guid? profileId = null,
        NavigationCoordinator? navigation = null,
        OverlayHostViewModel? overlay = null)
    {
        _queries = queries;
        _operations = operations;
        _profileId = profileId;
        _navigation = navigation;
        _overlay = overlay;

        OpenRelatedProfileCommand = new RelayCommand(param =>
        {
            var id = param is Guid guid ? guid : SelectedRelatedProfile?.RelatedProfileId;
            if (id.HasValue && id.Value != Guid.Empty)
            {
                _navigation?.Navigate(new ProfileRoute(id.Value));
            }
        });

        AddManualRelationCommand = new AsyncRelayCommand(param =>
            MutateManualRelationAsync(param, add: true));

        RemoveManualRelationCommand = new AsyncRelayCommand(param =>
            MutateManualRelationAsync(param, add: false));

        OpenAddRelationPickerCommand = new AsyncRelayCommand(OpenRelationPickerAsync);
    }

    private static string RelatedFailureText() => SurfaceText.Get(
        "SurfaceText.Related.Profiles.could.not.be.loaded.3E5B9B45",
        "Related Profiles could not be loaded");

    private Guid? ResolveTargetId(object? parameter) => parameter switch
    {
        Guid guid => guid,
        RelatedProfileSummaryReadModel model => model.RelatedProfileId,
        _ => SelectedRelatedProfile?.RelatedProfileId,
    };

    private async Task MutateManualRelationAsync(object? parameter, bool add)
    {
        var targetId = ResolveTargetId(parameter);
        if (_operations is null || !_profileId.HasValue || !targetId.HasValue)
        {
            return;
        }

        var sourceId = _profileId.Value;
        try
        {
            var result = add
                ? await _operations.AddManualRelatedProfileAsync(sourceId, targetId.Value).ConfigureAwait(true)
                : await _operations.RemoveManualRelatedProfileAsync(sourceId, targetId.Value).ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                Trace.TraceWarning("Related Profile mutation failed: {0}", result.ErrorCode);
                StatusMessage = RelatedFailureText();
                return;
            }

            StatusMessage = null;
            await LoadRelatedProfilesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Related Profile mutation failed: {0}", exception.GetType().Name);
            StatusMessage = RelatedFailureText();
        }
    }

    private async Task OpenRelationPickerAsync()
    {
        if (_overlay is null || _operations is null || !_profileId.HasValue)
        {
            return;
        }

        try
        {
            // Picker search is local to the overlay, so its source set must be complete. Do not turn
            // a presentation preload into a search ceiling by asking ProfileReads for an arbitrary
            // first N rows.
            var candidates = await new ProfilePickerReads(_operations.Catalog)
                .GetAllCandidatesAsync(excludeProfileId: _profileId.Value)
                .ConfigureAwait(true);

            var request = new ProfilePickerOverlayRequest(
                candidates,
                OnRelationPicked,
                title: SurfaceText.Get("SurfaceText.Add.Related.Profile.4DCB36CF", "Add Related Profile..."),
                prompt: SurfaceText.Get("SurfaceText.Select.Existing.Profile.7B23D2D3", "Choose a profile"));

            _overlay.Push(request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Related Profile picker failed: {0}", exception.GetType().Name);
            StatusMessage = RelatedFailureText();
        }
    }

    private void OnRelationPicked(ProfilePickerItem picked)
    {
        TaskObserver.Observe(
            AddPickedRelationAsync(picked),
            "RelatedProfilesViewModel.AddPickedRelationAsync",
            exception =>
            {
                Trace.TraceWarning("Picked Related Profile mutation failed: {0}", exception.GetType().Name);
                StatusMessage = RelatedFailureText();
            });
    }

    private async Task AddPickedRelationAsync(ProfilePickerItem picked)
    {
        if (_operations is null || !_profileId.HasValue)
        {
            return;
        }

        var sourceId = _profileId.Value;
        try
        {
            var result = await _operations.AddManualRelatedProfileAsync(sourceId, picked.ProfileId)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Trace.TraceWarning("Picked Related Profile mutation failed: {0}", result.ErrorCode);
                StatusMessage = RelatedFailureText();
                return;
            }

            StatusMessage = null;
            await LoadRelatedProfilesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Picked Related Profile mutation failed: {0}", exception.GetType().Name);
            StatusMessage = RelatedFailureText();
        }
    }

    public ICommand OpenRelatedProfileCommand { get; }
    public ICommand AddManualRelationCommand { get; }
    public ICommand RemoveManualRelationCommand { get; }
    public ICommand OpenAddRelationPickerCommand { get; }

    public void Populate(IEnumerable<RelatedProfileSummaryReadModel> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var materialized = summaries.ToList();
        ReconcileRelatedProfiles(materialized);
    }

    public Guid? ProfileId
    {
        get => _profileId;
        set
        {
            if (_profileId != value)
            {
                _profileId = value;
                unchecked
                {
                    _relatedLoadGeneration++;
                    _evidenceLoadGeneration++;
                }
                SelectedRelatedProfile = null;
                RelatedProfiles.Clear();
                EvidenceEntries.Clear();
                StatusMessage = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRelatedProfiles));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(EvidenceBreakdownText));
            }
        }
    }

    public ObservableCollection<RelatedProfileSummaryReadModel> RelatedProfiles { get; } = [];
    public bool HasRelatedProfiles => RelatedProfiles.Count > 0;
    public bool ShowEmptyState => !IsLoading && StatusMessage is null && !HasRelatedProfiles;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public ObservableCollection<RelatedProfileEvidenceEntry> EvidenceEntries { get; } = [];

    public string EvidenceBreakdownText => EvidenceEntries.Count == 0
        ? SurfaceText.Get("SurfaceText.No.related.Profiles.yet.BCE22603", "No related Profiles yet")
        : string.Join(" · ", EvidenceEntries
            .GroupBy(static entry => entry.EvidenceType)
            .OrderBy(static group => group.Key)
            .Select(group => DescribeEvidence(group.Key, group.Count())));

    private static string DescribeEvidence(RelatedProfileEvidence evidence, int count) => evidence switch
    {
        RelatedProfileEvidence.SharedMedia => count == 1
            ? SurfaceText.Get("Home.Evidence.Shared.One", "1 shared media item")
            : SurfaceText.Format("Home.Evidence.Shared.Many", "{0} shared media items", count),
        RelatedProfileEvidence.ConfirmedFace => count == 1
            ? SurfaceText.Get("Home.Evidence.Face.One", "1 confirmed face association")
            : SurfaceText.Format("Home.Evidence.Face.Many", "{0} confirmed face associations", count),
        RelatedProfileEvidence.Manual => SurfaceText.Get("Home.Evidence.Manual", "a manual relation"),
        _ => count.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    public RelatedProfileSummaryReadModel? SelectedRelatedProfile
    {
        get => _selectedRelatedProfile;
        set
        {
            if (_selectedRelatedProfile != value)
            {
                _selectedRelatedProfile = value;
                var generation = unchecked(++_evidenceLoadGeneration);
                OnPropertyChanged();
                if (value is null)
                {
                    EvidenceEntries.Clear();
                    OnPropertyChanged(nameof(EvidenceBreakdownText));
                }
                else
                {
                    TaskObserver.Observe(
                        LoadEvidenceForSelectedPairAsync(generation),
                        "RelatedProfilesViewModel.LoadEvidenceForSelectedPairAsync",
                        exception =>
                        {
                            Trace.TraceWarning("Related evidence load failed: {0}", exception.GetType().Name);
                            StatusMessage = RelatedFailureText();
                        });
                }
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatusMessage));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public async Task LoadRelatedProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _queries is null || _profileId is null || _profileId == Guid.Empty)
        {
            return;
        }

        var profileId = _profileId.Value;
        var generation = unchecked(++_relatedLoadGeneration);
        await UiDispatch.InvokeAsync(() =>
        {
            if (!_isDisposed && generation == _relatedLoadGeneration && _profileId == profileId)
            {
                IsLoading = true;
                StatusMessage = null;
            }
        }).ConfigureAwait(false);

        try
        {
            var summaries = await _queries.GetRelatedSummariesForProfileAsync(profileId, 24, cancellationToken).ConfigureAwait(false);
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && generation == _relatedLoadGeneration && _profileId == profileId)
                {
                    ReconcileRelatedProfiles(summaries);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _isDisposed)
        {
        }
        catch (Exception exception)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && generation == _relatedLoadGeneration && _profileId == profileId)
                {
                    Trace.TraceWarning("Related Profiles load failed: {0}", exception.GetType().Name);
                    StatusMessage = RelatedFailureText();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && generation == _relatedLoadGeneration && _profileId == profileId)
                {
                    IsLoading = false;
                }
            }).ConfigureAwait(false);
        }
    }

    private void ReconcileRelatedProfiles(IReadOnlyList<RelatedProfileSummaryReadModel> summaries)
    {
        var selectedId = SelectedRelatedProfile?.RelatedProfileId;
        RelatedProfiles.Clear();
        foreach (var summary in summaries)
        {
            RelatedProfiles.Add(summary);
        }

        OnPropertyChanged(nameof(HasRelatedProfiles));
        OnPropertyChanged(nameof(ShowEmptyState));

        var selected = selectedId.HasValue
            ? RelatedProfiles.FirstOrDefault(item => item.RelatedProfileId == selectedId.Value)
            : null;
        SelectedRelatedProfile = selected ?? RelatedProfiles.FirstOrDefault();
    }

    public Task LoadEvidenceForSelectedPairAsync(CancellationToken cancellationToken = default) =>
        LoadEvidenceForSelectedPairAsync(unchecked(++_evidenceLoadGeneration), cancellationToken);

    private async Task LoadEvidenceForSelectedPairAsync(
        int generation,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _queries is null || _profileId is null || _selectedRelatedProfile is null)
        {
            return;
        }

        var profileId = _profileId.Value;
        var relatedProfileId = _selectedRelatedProfile.RelatedProfileId;
        try
        {
            var entries = await _queries.GetEvidenceForPairAsync(profileId, relatedProfileId, cancellationToken).ConfigureAwait(false);
            await UiDispatch.InvokeAsync(() =>
            {
                if (_isDisposed || generation != _evidenceLoadGeneration
                    || _profileId != profileId || _selectedRelatedProfile?.RelatedProfileId != relatedProfileId)
                {
                    return;
                }

                EvidenceEntries.Clear();
                foreach (var entry in entries)
                {
                    EvidenceEntries.Add(entry);
                }
                StatusMessage = null;
                OnPropertyChanged(nameof(EvidenceBreakdownText));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _isDisposed)
        {
        }
        catch (Exception exception)
        {
            await UiDispatch.InvokeAsync(() =>
            {
                if (!_isDisposed && generation == _evidenceLoadGeneration
                    && _profileId == profileId && _selectedRelatedProfile?.RelatedProfileId == relatedProfileId)
                {
                    Trace.TraceWarning("Related evidence load failed: {0}", exception.GetType().Name);
                    StatusMessage = RelatedFailureText();
                }
            }).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        unchecked
        {
            _relatedLoadGeneration++;
            _evidenceLoadGeneration++;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
