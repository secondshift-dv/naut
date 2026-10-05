using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Recovery;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Lifecycle;

public sealed class BootstrapContext : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    internal BootstrapContext(
        VaultPaths paths,
        VaultLock vaultLock,
        CatalogDb catalog,
        RecoveryResult recovery,
        int schemaVersion,
        Guid vaultId,
        Guid sessionId,
        Guid sessionGeneration,
        StartupState initialState = StartupState.Prewarming)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        VaultLock = vaultLock ?? throw new ArgumentNullException(nameof(vaultLock));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        SchemaVersion = schemaVersion;
        VaultId = vaultId != Guid.Empty
            ? vaultId
            : throw new ArgumentException("VaultId cannot be empty.", nameof(vaultId));
        SessionId = sessionId != Guid.Empty
            ? sessionId
            : throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));
        SessionGeneration = sessionGeneration != Guid.Empty
            ? sessionGeneration
            : throw new ArgumentException("SessionGeneration cannot be empty.", nameof(sessionGeneration));
        State = initialState;
    }

    public StartupState State { get; internal set; }

    public VaultPaths Paths { get; }

    public VaultLock VaultLock { get; }

    public CatalogDb Catalog { get; }

    public RecoveryResult Recovery { get; }

    public int SchemaVersion { get; }

    public Guid VaultId { get; }

    public Guid SessionId { get; }

    public Guid SessionGeneration { get; }

    public VaultSuccessfulBootstrapProof CreateSuccessfulBootstrapProof()
    {
        if (State != StartupState.Ready)
        {
            throw new InvalidOperationException("A successful-bootstrap proof requires a Ready bootstrap context.");
        }

        return new VaultSuccessfulBootstrapProof(Paths.Root, VaultId, SessionId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        VaultLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await VaultLock.DisposeAsync().ConfigureAwait(false);
    }
}
