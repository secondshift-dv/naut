using System.IO;
using System.Security.Cryptography;
using System.Text;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Import.Intake;

public sealed record ImportIntakeUnitResult(
    Guid UnitId,
    string SourceKind,
    string SourceDisplayName,
    string? SourcePathOrReference,
    int DiscoveredItemCount,
    int AllocatedCandidateCount,
    IReadOnlyList<Guid> CandidateMediaIds);

public sealed record ImportIntakeResult(
    Guid SessionId,
    int TotalDiscoveredItemCount,
    int TotalAllocatedCandidateCount,
    IReadOnlyList<ImportIntakeUnitResult> Units,
    IReadOnlyList<SourceIntakeIssue> Issues);

public sealed class ImportIntakeCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly ImportAdmissionWrites _admissionWrites;
    private readonly VaultPaths _paths;
    private readonly TimeProvider _timeProvider;

    public ImportIntakeCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths = catalog.Paths;
        _admissionWrites = new ImportAdmissionWrites(catalog, _timeProvider);
    }

    public async Task<ImportIntakeResult> IntakeAsync(
        ImportIntakeRequest request,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        using var mutationAdmission = _catalog.MutationAdmission.Enter(nameof(IntakeAsync));
        ArgumentNullException.ThrowIfNull(request);
        EnsureVaultIsReadyForImport();

        if (request.SourceEntries.Count == 0)
        {
            throw new ArgumentException("Source entries cannot be empty.", nameof(request));
        }

        var plan = IntakeMapper.MapIntake(request, sessionId, cancellationToken);
        // Independent media units let an exceptional item wait for attention without blocking
        // other Profile additions. Each uses the same admission, jobs, commit and recovery graph.
        if (plan.Origin == IntakeOrigin.ProfileAddMedia && plan.SuggestedDestinationProfileId is { } profileId && profileId != Guid.Empty)
        {
            plan = plan with
            {
                Units = plan.Units.SelectMany(unit => unit.Items.Select(item => new MappedUnitPlan(
                    DeriveDeterministicId(unit.ImportUnitId, "profile-media:" + item.FullSourcePath),
                    unit.SourceKind, Path.GetFileName(item.FullSourcePath), item.FullSourcePath, [item]))).ToArray(),
            };
        }
        var unitResults = new List<ImportIntakeUnitResult>();
        var totalDiscovered = 0;
        var totalAllocated = 0;
        var issues = new List<SourceIntakeIssue>();

        foreach (var unitPlan in plan.Units)
        {
            var allocations = new List<CandidateAllocationRequest>();
            foreach (var item in unitPlan.Items)
            {
                totalDiscovered++;

                // Classification alone is not admission. A supported path that cannot be read must
                // stay outside durable import work instead of becoming a Candidate that later stalls.
                if (!item.Exists || !item.IsReadable || !item.Classification.HasValue)
                {
                    issues.Add(new(item.FullSourcePath, item.Issue ?? SourceIntakeIssueKind.Unreadable));
                    continue;
                }

                var identity = item.SourceIdentityJson;
                if (identity is null)
                {
                    issues.Add(new(item.FullSourcePath, SourceIntakeIssueKind.Unreadable));
                    continue;
                }
                allocations.Add(new CandidateAllocationRequest(
                    DeriveDeterministicId(unitPlan.ImportUnitId, "item:" + item.FullSourcePath),
                    unitPlan.ImportUnitId,
                    DeriveDeterministicId(unitPlan.ImportUnitId, "asset:" + item.FullSourcePath),
                    item.FullSourcePath,
                    Path.GetFileName(item.FullSourcePath),
                    item.Classification.Value,
                    item.ByteLength,
                    DbTime.FormatOrNull(item.LastWriteUtc),
                    SourceIdentityJson: identity));
            }

            // Zero-valid intake has no durable Import Unit. Unit registration and every initial
            // Candidate allocation below share one transaction, so allocation failure rolls the unit
            // back instead of leaving a ghost scheduler/history row.
            if (allocations.Count == 0)
            {
                continue;
            }

            var registration = new ImportUnitRegistration(
                plan.ImportSessionId,
                unitPlan.ImportUnitId,
                unitPlan.SourceKind,
                unitPlan.SourceDisplayName,
                SessionState: ImportSessionState.Open,
                UnitState: ImportUnitState.Intake,
                SourcePathOrReference: unitPlan.SourcePathOrReference);

            var initialDraft = plan.SuggestedDestinationProfileId is { } explicitProfileId && explicitProfileId != Guid.Empty
                ? VerificationDraftV1.CreateDefault(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) with
                {
                    CurrentStep = (int)ImportWizardStep.Details,
                    ImportRequested = plan.Origin == IntakeOrigin.ProfileAddMedia ? true : null,
                    PublishWhenReady = plan.Origin == IntakeOrigin.ProfileAddMedia,
                    Destination = new VerificationDestinationDraft(DestinationKind.ExistingNormal, explicitProfileId, null),
                }
                : null;
            var candidateMediaIds = (await _admissionWrites
                .AdmitUnitAsync(registration, allocations, initialDraft, cancellationToken)
                .ConfigureAwait(false)).ToList();
            totalAllocated += candidateMediaIds.Count;

            unitResults.Add(new ImportIntakeUnitResult(
                unitPlan.ImportUnitId,
                unitPlan.SourceKind,
                unitPlan.SourceDisplayName,
                unitPlan.SourcePathOrReference,
                unitPlan.Items.Count,
                candidateMediaIds.Count,
                candidateMediaIds));
        }

        return new ImportIntakeResult(
            plan.ImportSessionId,
            totalDiscovered,
            totalAllocated,
            unitResults, issues);
    }

    private void EnsureVaultIsReadyForImport()
    {
        if (!Path.IsPathFullyQualified(_paths.Root)
            || !Directory.Exists(_paths.Root)
            || !Directory.Exists(_paths.SystemPath)
            || !File.Exists(_paths.CatalogDbPath))
        {
            throw new InvalidOperationException(
                "Choose and initialize a Vault location before importing media.");
        }
    }

    public static Guid DeriveDeterministicId(Guid scopeId, string uniqueKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);

        // Preserve the exact normalized source spelling supplied by intake. Case folding here made
        // distinct paths collide on filesystems/directories whose semantics permit case distinction.
        var input = $"{scopeId:D}:{uniqueKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
