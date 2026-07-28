namespace Forge.Core;

/// <summary>
/// Lightweight read-model for list views. Excludes payload, idempotency_key,
/// and other detail-only fields. Dapper-friendly: plain settable properties
/// so the column-to-property mapping is unambiguous and doesn't require a
/// constructor-signature match against Postgres column types.
/// </summary>
public class JobSummary
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = "";
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
}