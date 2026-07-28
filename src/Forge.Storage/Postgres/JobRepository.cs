using Dapper;
using Forge.Core;
using Npgsql;
using System.Text.Json;

namespace Forge.Storage.Postgres;

/// <summary>
/// Dapper-based implementation of <see cref="IJobRepository"/>.
/// Opens a fresh <see cref="NpgsqlConnection"/> per call — Npgsql pools connections
/// internally, so this is efficient and keeps connections out of the request flow
/// any longer than necessary.
/// </summary>
public class JobRepository : IJobRepository
{
    private readonly string _connectionString;

    public JobRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task Insert(Job job, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO jobs (
                id, job_type, payload, queue, priority, status,
                attempts, max_attempts, last_error, idempotency_key,
                scheduled_for, created_at, started_at, completed_at, duration_ms
            ) VALUES (
                @Id, @JobType, @Payload::jsonb, @Queue, @Priority, @Status,
                @Attempts, @MaxAttempts, @LastError, @IdempotencyKey,
                @ScheduledFor, @CreatedAt, @StartedAt, @CompletedAt, @DurationMs
            );";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            parameters: new
            {
                job.Id,
                job.JobType,
                // Serialize JsonElement to string for the @Payload::jsonb parameter.
                Payload = job.Payload.GetRawText(),
                job.Queue,
                job.Priority,
                Status = job.Status.ToString().ToLowerInvariant(),
                job.Attempts,
                job.MaxAttempts,
                job.LastError,
                job.IdempotencyKey,
                job.ScheduledFor,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                job.DurationMs
            },
            cancellationToken: ct));
    }

    public async Task<Job?> Get(Guid id, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, job_type, payload::text AS payload, queue, priority, status,
                   attempts, max_attempts, last_error, idempotency_key,
                   scheduled_for, created_at, started_at, completed_at, duration_ms
            FROM jobs
            WHERE id = @Id
            LIMIT 1;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            sql, new { Id = id }, cancellationToken: ct));

        return row?.ToJob();
    }

    public async Task<Job?> FindByIdempotencyKey(string key, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, job_type, payload::text AS payload, queue, priority, status,
                   attempts, max_attempts, last_error, idempotency_key,
                   scheduled_for, created_at, started_at, completed_at, duration_ms
            FROM jobs
            WHERE idempotency_key = @Key
            LIMIT 1;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            sql, new { Key = key }, cancellationToken: ct));

        return row?.ToJob();
    }

    /// <summary>
    /// Intermediate DTO Dapper can hydrate directly. We convert to Job afterwards
    /// because Job's Payload is a JsonElement, which Dapper can't map from text.
    /// </summary>
    private class JobRow
    {
        public Guid Id { get; set; }
        public string JobType { get; set; } = "";
        public string Payload { get; set; } = "";  // JSON text from payload::text
        public string Queue { get; set; } = "";
        public int Priority { get; set; }
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        public int MaxAttempts { get; set; }
        public string? LastError { get; set; }
        public string? IdempotencyKey { get; set; }
        public DateTimeOffset? ScheduledFor { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int? DurationMs { get; set; }

        public Job ToJob()
        {
            using var doc = JsonDocument.Parse(Payload);
            // Clone() detaches the element from the document so it survives disposal.
            var payload = doc.RootElement.Clone();

            return new Job(
                Id,
                JobType,
                payload,
                Queue,
                Priority,
                Enum.Parse<JobStatus>(Status, ignoreCase: true),
                Attempts,
                MaxAttempts,
                LastError,
                IdempotencyKey,
                ScheduledFor,
                CreatedAt,
                StartedAt,
                CompletedAt,
                DurationMs);
        }
    }

    public async Task MarkRunning(Guid id, CancellationToken ct)
    {
        const string sql = """
        UPDATE jobs
        SET status     = 'running',
            attempts   = attempts + 1,
            started_at = now()
        WHERE id = @Id
        """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task MarkSucceeded(Guid id, int durationMs, CancellationToken ct)
    {
        const string sql = """
        UPDATE jobs
        SET status       = 'succeeded',
            completed_at = now(),
            duration_ms  = @DurationMs
        WHERE id = @Id
        """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = id, DurationMs = durationMs }, cancellationToken: ct));
    }

    public async Task MarkFailed(Guid id, string error, int durationMs, CancellationToken ct)
    {
        const string sql = """
        UPDATE jobs
        SET status       = 'failed',
            completed_at = now(),
            duration_ms  = @DurationMs,
            last_error   = @Error
        WHERE id = @Id
        """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = id, Error = error, DurationMs = durationMs }, cancellationToken: ct));
    }

    public async Task MarkRetrying(
    Guid id,
    DateTimeOffset nextRunAt,
    string error,
    CancellationToken ct)
    {
        const string sql = """
        UPDATE jobs
        SET status        = 'queued',
            last_error    = @Error,
            scheduled_for = @NextRunAt,
            started_at    = NULL,
            completed_at  = NULL,
            duration_ms   = NULL
        WHERE id = @Id
        """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = id, NextRunAt = nextRunAt, Error = error },
            cancellationToken: ct));
    }

    public async Task MarkDead(Guid id, string error, CancellationToken ct)
    {
        const string sql = """
        UPDATE jobs
        SET status       = 'dead',
            last_error   = @Error,
            completed_at = now()
        WHERE id = @Id
        """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = id, Error = error },
            cancellationToken: ct));
    }


    public async Task<IReadOnlyList<Job>> ListRecentAsync(
      JobStatus? status,
      int limit,
      CancellationToken ct)
    {
        const string sqlAll = @"
        SELECT id, job_type, payload::text AS payload, queue, priority, status,
               attempts, max_attempts, last_error, idempotency_key,
               scheduled_for, created_at, started_at, completed_at, duration_ms
        FROM jobs
        ORDER BY created_at DESC
        LIMIT @Limit";

        const string sqlByStatus = @"
        SELECT id, job_type, payload::text AS payload, queue, priority, status,
               attempts, max_attempts, last_error, idempotency_key,
               scheduled_for, created_at, started_at, completed_at, duration_ms
        FROM jobs
        WHERE status = @Status
        ORDER BY created_at DESC
        LIMIT @Limit";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = status is null
            ? await conn.QueryAsync<JobRow>(new CommandDefinition(
                sqlAll, new { Limit = limit }, cancellationToken: ct))
            : await conn.QueryAsync<JobRow>(new CommandDefinition(
                sqlByStatus,
                new { Status = status.Value.ToString().ToLowerInvariant(), Limit = limit },
                cancellationToken: ct));

        return rows.Select(r => r.ToJob()).ToList();
    }


    public async Task MarkForRetry(Guid id, CancellationToken ct)
    {
        const string sql = """
    UPDATE jobs
    SET status        = 'queued',
        last_error    = NULL,
        started_at    = NULL,
        completed_at  = NULL,
        duration_ms   = NULL,
        scheduled_for = NULL,
        max_attempts  = attempts + 1
    WHERE id = @Id
      AND status IN ('failed', 'dead')
    """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> ListDeadJobIdsAsync(CancellationToken ct)
    {
        const string sql = @"
        SELECT id
        FROM jobs
        WHERE status = 'dead'
        ORDER BY created_at DESC";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var ids = await conn.QueryAsync<Guid>(new CommandDefinition(
            sql, cancellationToken: ct));

        return ids.ToList();
    }
}