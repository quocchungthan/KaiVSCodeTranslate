using Kai.Engine.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kai.Engine.Storage;

/// <summary>Small SQLite database holding only engine queue/status metadata (never pipeline state).</summary>
public sealed class EngineDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public EngineDatabase(RepositoryLayout layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(layout.DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            // Microsoft.Data.Sqlite retries SQLITE_BUSY until the command timeout elapses.
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = FULL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);

                CREATE TABLE IF NOT EXISTS jobs (
                    sha256               TEXT    NOT NULL PRIMARY KEY CHECK (length(sha256) = 64),
                    queue_seq            INTEGER NOT NULL UNIQUE,
                    job_id               TEXT    NULL,
                    file_name            TEXT    NOT NULL,
                    source_relative_path TEXT    NOT NULL,
                    size_bytes           INTEGER NOT NULL,
                    state                TEXT    NOT NULL CHECK (state IN ('queued','running','completed','failed')),
                    assigned_agent       TEXT    NOT NULL,
                    requested_by         TEXT    NULL,
                    attempts             INTEGER NOT NULL DEFAULT 0,
                    lease_owner          TEXT    NULL,
                    lease_expires_utc    TEXT    NULL,
                    not_before_utc       TEXT    NULL,
                    copilot_session_id   TEXT    NULL,
                    message              TEXT    NULL,
                    origin               TEXT    NOT NULL DEFAULT 'upload',
                    created_utc          TEXT    NOT NULL,
                    updated_utc          TEXT    NOT NULL,
                    started_utc          TEXT    NULL,
                    completed_utc        TEXT    NULL,
                    CHECK (state <> 'running' OR (lease_owner IS NOT NULL AND lease_expires_utc IS NOT NULL))
                );

                CREATE INDEX IF NOT EXISTS ix_jobs_agent_state_seq ON jobs (assigned_agent, state, queue_seq);
                CREATE INDEX IF NOT EXISTS ix_jobs_state_seq ON jobs (state, queue_seq);

                CREATE TABLE IF NOT EXISTS job_events (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    sha256  TEXT NOT NULL,
                    at_utc  TEXT NOT NULL,
                    kind    TEXT NOT NULL,
                    detail  TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_job_events_sha ON job_events (sha256, id);
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM schema_info LIMIT 1;";
            var existing = await version.ExecuteScalarAsync(cancellationToken);
            if (existing is null)
            {
                version.CommandText = "INSERT INTO schema_info (version) VALUES ($v);";
                version.Parameters.AddWithValue("$v", SchemaVersion);
                await version.ExecuteNonQueryAsync(cancellationToken);
            }
            else if (Convert.ToInt32(existing) > SchemaVersion)
            {
                throw new InvalidOperationException($"Engine database schema {existing} is newer than supported version {SchemaVersion}.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
