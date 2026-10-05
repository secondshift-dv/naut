using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Neuterradise.App.SystemServices.Database;

public static class SqlitePragmas
{
    public const int BusyTimeoutMilliseconds = 5000;
    public const string JournalMode = "WAL";
    public const string SynchronousMode = "FULL";

    public static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        EnsureOpen(connection);
        // journal_mode is persistent database state and is negotiated during catalog initialization.
        // Ordinary pooled opens only apply connection-scoped policy.
        await ExecuteAsync(
            connection,
            $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}; PRAGMA synchronous = {SynchronousMode};",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task InitializeAndValidateDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        EnsureOpen(connection);
        var journalMode = Convert.ToString(await ExecuteScalarAsync(connection, $"PRAGMA journal_mode = {JournalMode};", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA synchronous = {SynchronousMode};", cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadInt32Async(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false);
        var synchronous = await ReadInt32Async(connection, "PRAGMA synchronous;", cancellationToken).ConfigureAwait(false);
        var busyTimeout = await ReadInt32Async(connection, "PRAGMA busy_timeout;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(journalMode, JournalMode, StringComparison.OrdinalIgnoreCase) || foreignKeys != 1 || synchronous != 2 || busyTimeout != BusyTimeoutMilliseconds)
            throw new InvalidOperationException("SQLite catalog policy validation failed. WAL, synchronous=FULL, foreign keys, and the busy timeout are mandatory.");
    }

    public static async Task<WalCheckpointResult> CheckpointWalPassiveAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        EnsureOpen(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new(false, 0, 0);
        return new(reader.GetInt32(0) != 0, reader.GetInt32(1), reader.GetInt32(2));
    }

    private static void EnsureOpen(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open) throw new InvalidOperationException("SQLite PRAGMAs require an open connection.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToInt32(await ExecuteScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
}

public sealed record WalCheckpointResult(bool Blocked, int LogFrames, int CheckpointedFrames)
{
    public bool FullyDrained => !Blocked && LogFrames == CheckpointedFrames;
}
