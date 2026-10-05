using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Database;

public sealed class CatalogDb
{
    public static bool IsDatabaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException) return false;
            if (current is SqliteException) return true;
        }
        return false;
    }

    private readonly CatalogSchemaInitializer _schemaInitializer;
    private readonly TimeProvider _timeProvider;

    public CatalogDb(VaultPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Paths = paths;
        ConnectionFactory = new CatalogConnectionFactory(paths);
        WriteCoordinator = new CatalogWriteCoordinator();
        MutationAdmission = new CatalogMutationAdmissionGate();
        ImportUnitMutations = new ImportUnitMutationCoordinator();
        _schemaInitializer = new CatalogSchemaInitializer(ConnectionFactory, WriteCoordinator);
    }

    public VaultPaths Paths { get; }

    public CatalogConnectionFactory ConnectionFactory { get; }

    public CatalogWriteCoordinator WriteCoordinator { get; }

    public CatalogMutationAdmissionGate MutationAdmission { get; }

    public ImportUnitMutationCoordinator ImportUnitMutations { get; }

    public int? SchemaVersion { get; private set; }

    public async Task<CatalogInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _schemaInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        SchemaVersion = result.SchemaVersion;
        TaxonomyNormalization = await new SettingsWrites(this)
            .ReconcileTaxonomyNormalizationAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public TaxonomyNormalizationResult? TaxonomyNormalization { get; private set; }

    internal async Task<CatalogInitializationResult> InitializeAsync(
        Action databaseOpened,
        CancellationToken cancellationToken = default)
    {
        var result = await _schemaInitializer.InitializeAsync(databaseOpened, cancellationToken).ConfigureAwait(false);
        SchemaVersion = result.SchemaVersion;
        TaxonomyNormalization = await new SettingsWrites(this)
            .ReconcileTaxonomyNormalizationAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        ConnectionFactory.OpenConnectionAsync(cancellationToken);

    public GalleryReads GalleryReads => new(this);
    public ProfileReads ProfileReads => new(this);
    public MediaReads MediaReads => new(this);
    public ImportReads ImportReads => new(this);
    public FaceReads FaceReads => new(this);
    public RelatedReads RelatedReads => new(this);
    public SchedulerReads SchedulerReads => new(this);
    public SettingsReads SettingsReads => new(this);
    public TrashReads TrashReads => new(this);
    public HealthReads HealthReads => new(this);
    public MediaAssetReads MediaAssetReads => new(this);

    public async Task<WalCheckpointResult> CheckpointWalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await SqlitePragmas.CheckpointWalPassiveAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }
    public ActivityReads ActivityReads => new(this);

    public ProfileWrites ProfileWrites => new(this, _timeProvider);
    public MediaWrites MediaWrites => new(this, _timeProvider);
    public MediaAssetWrites MediaAssetWrites => new(this, timeProvider: _timeProvider);
    public ImportWrites ImportWrites => new(this, _timeProvider);
    public SettingsWrites SettingsWrites => new(this, _timeProvider);
    public TrashWrites TrashWrites => new(this);
    public MaintenanceWrites MaintenanceWrites => new(this);
    public ActivityWrites ActivityWrites => new(this);
}
