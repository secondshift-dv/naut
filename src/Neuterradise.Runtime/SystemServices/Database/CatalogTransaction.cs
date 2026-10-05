using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

public sealed class CatalogTransaction : IAsyncDisposable
{
    private readonly SqliteTransaction _transaction;
    private readonly CatalogWriteCoordinator? _writeCoordinator;
    private readonly List<CatalogInvalidation> _invalidations = new();
    private bool _completed;

    private CatalogTransaction(SqliteTransaction transaction, CatalogWriteCoordinator? writeCoordinator)
    {
        _transaction = transaction;
        _writeCoordinator = writeCoordinator;
    }

    public static CatalogTransaction Begin(SqliteConnection connection)
        => BeginCore(connection, null);

    public static CatalogTransaction Begin(SqliteConnection connection, CatalogWriteCoordinator writeCoordinator)
    {
        ArgumentNullException.ThrowIfNull(writeCoordinator);
        return BeginCore(connection, writeCoordinator);
    }

    private static CatalogTransaction BeginCore(SqliteConnection connection, CatalogWriteCoordinator? writeCoordinator)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new CatalogTransaction(connection.BeginTransaction(deferred: false), writeCoordinator);
    }

    public SqliteCommand CreateCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        var command = _transaction.Connection!.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }

    public void QueueInvalidation(CatalogInvalidation invalidation)
    {
        ArgumentNullException.ThrowIfNull(invalidation);
        if (_completed) throw new InvalidOperationException("The catalog transaction has already completed.");
        _invalidations.Add(invalidation);
    }

    public void QueueInvalidation(string domainKind, params Guid[] entityIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainKind);
        ArgumentNullException.ThrowIfNull(entityIds);
        QueueInvalidation(new CatalogInvalidation(Guid.Empty, entityIds, domainKind, 0));
    }

    public async Task<long> ExecuteOptimisticWriteAsync(SqliteCommand mutation, SqliteCommand currentVersionQuery, string aggregateLabel, long expectedRowVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(currentVersionQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateLabel);
        if (!ReferenceEquals(mutation.Transaction, _transaction) || !ReferenceEquals(currentVersionQuery.Transaction, _transaction))
            throw new ArgumentException("Optimistic-write commands must belong to this catalog transaction.");
        var next = NextRowVersion(expectedRowVersion);
        var affected = await mutation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 1) return next;
        if (affected > 1) throw new CatalogInvariantException($"{aggregateLabel} optimistic write affected {affected} rows; exactly one was required.");
        var current = await currentVersionQuery.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (current is null or DBNull) throw new CatalogInvariantException($"{aggregateLabel} does not exist.");
        long actual;
        try { actual = Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { throw new CatalogInvariantException($"{aggregateLabel} has an invalid persisted row_version.", ex); }
        throw new CatalogConcurrencyConflictException($"{aggregateLabel} changed concurrently: expected row_version {expectedRowVersion}, but found {actual}.");
    }

    public static long NextRowVersion(long expectedRowVersion)
    {
        if (expectedRowVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedRowVersion));
        try { return checked(expectedRowVersion + 1); }
        catch (OverflowException ex) { throw new CatalogInvariantException("The persisted row_version reached its signed 64-bit limit.", ex); }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("The catalog transaction has already completed.");
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
        if (_writeCoordinator is not null)
            foreach (var invalidation in _invalidations) _writeCoordinator.PublishAfterCommit(invalidation);
        _invalidations.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed) await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
    }
}
