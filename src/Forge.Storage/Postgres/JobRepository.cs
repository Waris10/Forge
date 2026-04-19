using System.Data;
using System.Text.Json;
using Dapper;
using Forge.Core;
using Npgsql;

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
}