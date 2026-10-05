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
    IReadOnlyList<ImportIntakeUnitResult> Units);

public sealed class ImportIntakeCoordinator
{
    private readonly CatalogDb _catalog;
    private readonly ImportAdmissionWrites _admissionWrites;
    private readonly VerificationOperations _verification;
    private readonly VaultPaths _paths;
    private readonly TimeProvider _timeProvider;

    public ImportIntakeCoordinator(CatalogDb catalog, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths = catalog.Paths;
        _admissionWrites = new ImportAdmissionWrites(catalog, _timeProvider);
        _verification = new VerificationOperations(catalog, _timeProvider);
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
        var unitResults = new List<ImportIntakeUnitResult>();
        var totalDiscovered = 0;
        var totalAllocated = 0;

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
                    SourceIdentityJson: SourceIdentityHelper.CaptureIdentity(item.FullSourcePath)));
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

            var candidateMediaIds = (await _admissionWrites
                .AdmitUnitAsync(registration, allocations, cancellationToken)
                .ConfigureAwait(false)).ToList();
            totalAllocated += candidateMediaIds.Count;

            // "Add media" from a Profile is explicit human intent. Persist it after atomic admission;
            // normal picker/drop imports intentionally remain unresolved.
            if (plan.SuggestedDestinationProfileId is { } explicitProfileId
                && explicitProfileId != Guid.Empty)
            {
                var model = await _verification
                    .LoadVerificationReadModelAsync(unitPlan.ImportUnitId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new CatalogInvariantException(
                        $"ImportUnit {unitPlan.ImportUnitId:D} vanished during intake.");
                var draft = VerificationDraftV1.FromJson(
                    model.VerificationDraftJson,
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) with
                {
                    CurrentStep = (int)ImportWizardStep.Details,
                    Destination = new VerificationDestinationDraft(
                        DestinationKind.ExistingNormal,
                        explicitProfileId,
                        NewProfile: null),
                };
                await _verification.UpdateDraftAsync(
                    unitPlan.ImportUnitId,
                    draft,
                    model.RowVersion,
                    cancellationToken).ConfigureAwait(false);
            }

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
            unitResults);
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
