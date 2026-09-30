using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kai.Engine.Storage;

/// <summary>
/// Queue/lease persistence. Every state transition of a leased job is fenced by <c>lease_owner</c>
/// so a stale runner (lost lease, crashed heartbeat) can never overwrite a newer owner's result.
/// </summary>
public sealed class JobStore
{
    private const string Columns = """
        sha256, queue_seq, job_id, file_name, source_relative_path, size_bytes, state, assigned_agent,
        requested_by, attempts, lease_owner, lease_expires_utc, not_before_utc, copilot_session_id,
        message, origin, created_utc, updated_utc, started_utc, completed_utc
        """;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly EngineDatabase _database;
    private readonly TimeProvider _time;

    public JobStore(EngineDatabase database, TimeProvider time)
    {
        _database = database;
        _time = time;
    }

    public async Task<JobRecord?> GetAsync(string sha256, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", sha256);
        return await ReadSingleAsync(command, ct);
    }

    /// <summary>Inserts a new job; returns false when a job with the same hash already exists.</summary>
    public async Task<bool> TryInsertAsync(NewJob job, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs (sha256, queue_seq, job_id, file_name, source_relative_path, size_bytes, state,
                              assigned_agent, requested_by, attempts, origin, message, created_utc, updated_utc,
                              completed_utc)
            VALUES ($sha, (SELECT COALESCE(MAX(queue_seq), 0) + 1 FROM jobs), $jobId, $fileName, $path, $size, $state,
                    $agent, $requestedBy, 0, $origin, $message, $now, $now,
                    CASE WHEN $state = 'completed' THEN $now END)
            ON CONFLICT (sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$sha", job.Sha256);
        command.Parameters.AddWithValue("$jobId", (object?)job.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileName", job.FileName);
        command.Parameters.AddWithValue("$path", job.SourceRelativePath);
        command.Parameters.AddWithValue("$size", job.SizeBytes);
        command.Parameters.AddWithValue("$state", ToText(job.State));
        command.Parameters.AddWithValue("$agent", job.AssignedAgent);
        command.Parameters.AddWithValue("$requestedBy", (object?)job.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", job.Origin);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        var inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        if (inserted)
        {
            await AppendEventAsync(connection, transaction, job.Sha256, "created", $"origin={job.Origin}; agent={job.AssignedAgent}; state={ToText(job.State)}", ct);
        }
        await transaction.CommitAsync(ct);
        return inserted;
    }

    /// <summary>
    /// Atomically claims the oldest eligible queued job for <paramref name="agentName"/>.
    /// A single UPDATE ... RETURNING statement guarantees two callers can never claim the same job.
    /// </summary>
    public async Task<JobRecord?> TryAcquireNextAsync(string agentName, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE jobs
            SET state = 'running', lease_owner = $owner, lease_expires_utc = $expires, attempts = attempts + 1,
                started_utc = COALESCE(started_utc, $now), updated_utc = $now, not_before_utc = NULL
            WHERE sha256 = (
                SELECT sha256 FROM jobs
                WHERE state = 'queued' AND assigned_agent = $agent COLLATE NOCASE
                  AND (not_before_utc IS NULL OR not_before_utc <= $now)
                ORDER BY queue_seq
                LIMIT 1)
              AND state = 'queued'
            RETURNING {Columns};
            """;
        command.Parameters.AddWithValue("$owner", leaseOwner);
        command.Parameters.AddWithValue("$expires", Format(now + leaseDuration));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$agent", agentName);
        var record = await ReadSingleAsync(command, ct);
        if (record is not null)
        {
            await AppendEventAsync(connection, transaction, record.Sha256, "acquired", $"agent={agentName}; owner={leaseOwner}; attempt={record.Attempts}", ct);
        }
        await transaction.CommitAsync(ct);
        return record;
    }

    public async Task<bool> RenewLeaseAsync(string sha256, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs SET lease_expires_utc = $expires, updated_utc = $now
            WHERE sha256 = $sha AND state = 'running' AND lease_owner = $owner;
            """;
        command.Parameters.AddWithValue("$expires", Format(now + leaseDuration));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$owner", leaseOwner);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<bool> SetJobIdAsync(string sha256, string leaseOwner, string jobId, CancellationToken ct = default) =>
        UpdateLeasedAsync(sha256, leaseOwner, "job_id = $value", jobId, null, ct);

    public Task<bool> SetCopilotSessionAsync(string sha256, string leaseOwner, string sessionId, CancellationToken ct = default) =>
        UpdateLeasedAsync(sha256, leaseOwner, "copilot_session_id = $value", sessionId, "session", ct);

    public Task<bool> SetProgressMessageAsync(string sha256, string leaseOwner, string message, CancellationToken ct = default) =>
        UpdateLeasedAsync(sha256, leaseOwner, "message = $value", message, null, ct);

    public async Task<bool> CompleteAsync(string sha256, string leaseOwner, string? message, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        return await TransitionLeasedAsync(sha256, leaseOwner, """
            state = 'completed', lease_owner = NULL, lease_expires_utc = NULL, not_before_utc = NULL,
            message = $message, completed_utc = $now, updated_utc = $now
            """, message, now, "completed", ct);
    }

    /// <summary>Records a failed attempt: requeues with backoff, or fails permanently when attempts are exhausted or <paramref name="terminal"/>.</summary>
    public async Task<JobRecordState?> FailAttemptAsync(string sha256, string leaseOwner, string message, int maxAttempts, TimeSpan backoff, bool terminal, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = CASE WHEN $terminal = 1 OR attempts >= $max THEN 'failed' ELSE 'queued' END,
                not_before_utc = CASE WHEN $terminal = 1 OR attempts >= $max THEN NULL ELSE $notBefore END,
                lease_owner = NULL, lease_expires_utc = NULL, message = $message, updated_utc = $now
            WHERE sha256 = $sha AND state = 'running' AND lease_owner = $owner
            RETURNING state;
            """;
        command.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        command.Parameters.AddWithValue("$max", maxAttempts);
        command.Parameters.AddWithValue("$notBefore", Format(now + backoff));
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$owner", leaseOwner);
        var result = await command.ExecuteScalarAsync(ct) as string;
        if (result is not null)
        {
            await AppendEventAsync(connection, transaction, sha256, result == "failed" ? "failed" : "requeued", message, ct);
        }
        await transaction.CommitAsync(ct);
        return result is null ? null : ParseState(result);
    }

    /// <summary>Returns a leased job to the queue without consuming an attempt (graceful shutdown).</summary>
    public async Task<bool> ReleaseAsync(string sha256, string leaseOwner, string reason, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        return await TransitionLeasedAsync(sha256, leaseOwner, """
            state = 'queued', lease_owner = NULL, lease_expires_utc = NULL,
            attempts = MAX(attempts - 1, 0), message = $message, updated_utc = $now
            """, reason, now, "released", ct);
    }

    /// <summary>
    /// Requeues running jobs whose lease belongs to another instance. The exclusive engine lock guarantees such
    /// an instance is dead. Jobs that already consumed all attempts become failed (poison-job protection).
    /// </summary>
    public async Task<int> RecoverOrphanedLeasesAsync(string currentInstanceId, int maxAttempts, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = CASE WHEN attempts >= $max THEN 'failed' ELSE 'queued' END,
                message = CASE WHEN attempts >= $max
                               THEN 'Run was interrupted and retry attempts are exhausted.'
                               ELSE 'Run was interrupted; job requeued.' END,
                lease_owner = NULL, lease_expires_utc = NULL, updated_utc = $now
            WHERE state = 'running'
              AND lease_owner NOT LIKE $instance || ':%'
            RETURNING sha256, state;
            """;
        command.Parameters.AddWithValue("$max", maxAttempts);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$instance", currentInstanceId);
        var recovered = new List<(string Sha, string State)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                recovered.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        foreach (var (sha, state) in recovered)
        {
            await AppendEventAsync(connection, transaction, sha, "lease-recovered", $"state={state}", ct);
        }
        await transaction.CommitAsync(ct);
        return recovered.Count;
    }

    /// <summary>Marks a non-running job completed when job.json already proves completion (crash healing).</summary>
    public async Task<bool> MarkCompletedFromPipelineAsync(string sha256, string jobId, string message, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = 'completed', job_id = $jobId, message = $message, not_before_utc = NULL,
                completed_utc = COALESCE(completed_utc, $now), updated_utc = $now
            WHERE sha256 = $sha AND state IN ('queued', 'failed');
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$sha", sha256);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            await AppendEventAsync(connection, transaction, sha256, "reconciled", message, ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    /// <summary>Re-upload of a failed job: reset attempts and put it back at its original queue position.</summary>
    public async Task<bool> RequeueFailedAsync(string sha256, string? agentName, CancellationToken ct = default)
    {
        var now = Format(_time.GetUtcNow());
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = 'queued', attempts = 0, not_before_utc = NULL, message = 'Requeued by re-upload.',
                assigned_agent = COALESCE($agent, assigned_agent), updated_utc = $now
            WHERE sha256 = $sha AND state = 'failed';
            """;
        command.Parameters.AddWithValue("$agent", (object?)agentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$sha", sha256);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            await AppendEventAsync(connection, transaction, sha256, "requeued", "re-upload", ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    /// <summary>1-based position among queued jobs of the same agent; 0 when the job is not queued.</summary>
    public async Task<int> GetQueuePositionAsync(JobRecord job, CancellationToken ct = default)
    {
        if (job.State != JobRecordState.Queued)
        {
            return 0;
        }
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM jobs
            WHERE state = 'queued' AND assigned_agent = $agent COLLATE NOCASE AND queue_seq <= $seq;
            """;
        command.Parameters.AddWithValue("$agent", job.AssignedAgent);
        command.Parameters.AddWithValue("$seq", job.QueueSeq);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobRecordState? state, long? beforeQueueSeq, int limit, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM jobs
            WHERE ($state IS NULL OR state = $state) AND ($before IS NULL OR queue_seq < $before)
            ORDER BY queue_seq DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", state is null ? DBNull.Value : ToText(state.Value));
        command.Parameters.AddWithValue("$before", (object?)beforeQueueSeq ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadManyAsync(command, ct);
    }

    public async Task<IReadOnlyList<JobRecord>> ListByStatesAsync(IReadOnlyCollection<JobRecordState> states, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var names = states.Select((s, i) => $"$s{i}").ToArray();
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE state IN ({string.Join(',', names)}) ORDER BY queue_seq;";
        var index = 0;
        foreach (var state in states)
        {
            command.Parameters.AddWithValue($"$s{index++}", ToText(state));
        }
        return await ReadManyAsync(command, ct);
    }

    public async Task<IReadOnlyDictionary<JobRecordState, int>> CountByStateAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM jobs GROUP BY state;";
        var counts = Enum.GetValues<JobRecordState>().ToDictionary(s => s, _ => 0);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            counts[ParseState(reader.GetString(0))] = reader.GetInt32(1);
        }
        return counts;
    }

    public async Task<IReadOnlyList<(DateTimeOffset At, string Kind, string? Detail)>> GetEventsAsync(string sha256, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT at_utc, kind, detail FROM job_events WHERE sha256 = $sha ORDER BY id;";
        command.Parameters.AddWithValue("$sha", sha256);
        var events = new List<(DateTimeOffset, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add((Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return events;
    }

    private async Task<bool> UpdateLeasedAsync(string sha256, string leaseOwner, string assignment, string value, string? eventKind, CancellationToken ct)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE jobs SET {assignment}, updated_utc = $now
            WHERE sha256 = $sha AND state = 'running' AND lease_owner = $owner;
            """;
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", Format(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$owner", leaseOwner);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed && eventKind is not null)
        {
            await AppendEventAsync(connection, transaction, sha256, eventKind, value, ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    private async Task<bool> TransitionLeasedAsync(string sha256, string leaseOwner, string assignments, string? message, string now, string eventKind, CancellationToken ct)
    {
        await using var connection = await _database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE jobs SET {assignments}
            WHERE sha256 = $sha AND state = 'running' AND lease_owner = $owner;
            """;
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$owner", leaseOwner);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            await AppendEventAsync(connection, transaction, sha256, eventKind, message, ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    private async Task AppendEventAsync(SqliteConnection connection, SqliteTransaction transaction, string sha256, string kind, string? detail, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO job_events (sha256, at_utc, kind, detail) VALUES ($sha, $at, $kind, $detail);";
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$at", Format(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$detail", detail is null ? DBNull.Value : detail.Length > 2000 ? detail[..2000] : detail);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<JobRecord?> ReadSingleAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static async Task<IReadOnlyList<JobRecord>> ReadManyAsync(SqliteCommand command, CancellationToken ct)
    {
        var list = new List<JobRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Map(reader));
        }
        return list;
    }

    private static JobRecord Map(SqliteDataReader r) => new(
        Sha256: r.GetString(0),
        QueueSeq: r.GetInt64(1),
        JobId: NullableString(r, 2),
        FileName: r.GetString(3),
        SourceRelativePath: r.GetString(4),
        SizeBytes: r.GetInt64(5),
        State: ParseState(r.GetString(6)),
        AssignedAgent: r.GetString(7),
        RequestedBy: NullableString(r, 8),
        Attempts: r.GetInt32(9),
        LeaseOwner: NullableString(r, 10),
        LeaseExpiresUtc: NullableTime(r, 11),
        NotBeforeUtc: NullableTime(r, 12),
        CopilotSessionId: NullableString(r, 13),
        Message: NullableString(r, 14),
        Origin: r.GetString(15),
        CreatedUtc: Parse(r.GetString(16)),
        UpdatedUtc: Parse(r.GetString(17)),
        StartedUtc: NullableTime(r, 18),
        CompletedUtc: NullableTime(r, 19));

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? NullableTime(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Parse(r.GetString(i));

    internal static string Format(DateTimeOffset value) => value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static string ToText(JobRecordState state) => state switch
    {
        JobRecordState.Queued => "queued",
        JobRecordState.Running => "running",
        JobRecordState.Completed => "completed",
        JobRecordState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static JobRecordState ParseState(string value) => value switch
    {
        "queued" => JobRecordState.Queued,
        "running" => JobRecordState.Running,
        "completed" => JobRecordState.Completed,
        "failed" => JobRecordState.Failed,
        _ => throw new InvalidDataException($"Unknown job state '{value}'."),
    };
}
