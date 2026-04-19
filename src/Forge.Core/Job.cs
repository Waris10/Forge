using System.Text.Json;

namespace Forge.Core;

/// <summary>
/// The canonical job record. Mirrors the jobs table in Postgres one-to-one.
///
/// This is a <c>record</c>, not a <c>class</c>, because jobs are value-like:
/// two jobs with the same contents are functionally the same job. Records give
/// us structural equality, a nice ToString, and init-only properties for free.
/// </summary>
public record Job(
    Guid Id,
    string JobType,
    JsonElement Payload,
    string Queue,
    int Priority,
    JobStatus Status,
    int Attempts,
    int MaxAttempts,
    string? LastError,
    string? IdempotencyKey,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs
)
{
    /// <summary>
    /// Factory for a freshly-submitted job. Generates a new id, stamps CreatedAt,
    /// and sets status to Queued with zero attempts.
    /// </summary>
    public static Job NewQueued(
        string jobType,
        JsonElement payload,
        string queue = "default",
        int priority = 5,
        int maxAttempts = 5,
        string? idempotencyKey = null,
        DateTimeOffset? scheduledFor = null)
    {
        return new Job(
            Id: Guid.NewGuid(),
            JobType: jobType,
            Payload: payload,
            Queue: queue,
            Priority: priority,
            Status: JobStatus.Queued,
            Attempts: 0,
            MaxAttempts: maxAttempts,
            LastError: null,
            IdempotencyKey: idempotencyKey,
            ScheduledFor: scheduledFor,
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null,
            DurationMs: null
        );
    }
}