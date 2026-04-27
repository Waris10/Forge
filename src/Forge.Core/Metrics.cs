using Prometheus;
using System.Diagnostics.Metrics;

namespace Forge.Core;

/// <summary>
/// Forge's Prometheus metrics. Defined centrally so the metric names and
/// labels are consistent across processes; each process imports and
/// instruments only the metrics it owns.
///
/// Naming convention follows Prometheus best practice:
///   - prefix with namespace ("forge_")
///   - lowercase, snake_case
///   - counters end in "_total"
///   - histograms end in "_seconds" or "_bytes"
///   - gauges have no suffix (just the unit if applicable)
///
/// Per FORGE.md §11, the metrics that matter:
///   - jobs_submitted_total       (API)
///   - jobs_completed_total       (Worker, label: status)
///   - job_duration_seconds       (Worker, histogram)
///   - queue_depth                (Worker or scheduler, gauge)
///   - dlq_depth                  (gauge)
///   - workers_alive              (gauge, scraped from heartbeat keys)
/// </summary>
public static class Metrics
{
    /// <summary>
    /// Total jobs submitted via POST /jobs. Incremented in the API.
    /// </summary>
    public static readonly Counter JobsSubmitted = Prometheus.Metrics
        .CreateCounter(
            "forge_jobs_submitted_total",
            "Total jobs submitted via the API.",
            new CounterConfiguration
            {
                LabelNames = new[] { "job_type", "queue" }
            });

    /// <summary>
    /// Total jobs that reached a terminal state. Label "status" is one of:
    /// "succeeded", "failed", "dead". Incremented in the worker.
    /// </summary>
    public static readonly Counter JobsCompleted = Prometheus.Metrics
        .CreateCounter(
            "forge_jobs_completed_total",
            "Total jobs that reached a terminal state.",
            new CounterConfiguration
            {
                LabelNames = new[] { "job_type", "status" }
            });

    /// <summary>
    /// Total jobs recovered from dead workers by the janitor. Label
    /// "outcome" is "requeued" or "poisoned" (sent to DLQ).
    /// </summary>
    public static readonly Counter JobsRecovered = Prometheus.Metrics
        .CreateCounter(
            "forge_jobs_recovered_total",
            "Total jobs recovered from dead workers by the janitor.",
            new CounterConfiguration
            {
                LabelNames = new[] { "outcome" }
            });

    /// <summary>
    /// Histogram of job execution duration in seconds. Bucketed for
    /// queue workloads: most jobs are sub-second, but some legitimately
    /// take minutes. Buckets cover both ends.
    /// </summary>
    public static readonly Histogram JobDurationSeconds = Prometheus.Metrics
        .CreateHistogram(
            "forge_job_duration_seconds",
            "Job execution duration in seconds.",
            new HistogramConfiguration
            {
                LabelNames = new[] { "job_type", "status" },
                Buckets = new[] { 0.001, 0.01, 0.1, 0.5, 1, 5, 10, 30, 60, 300 }
            });

    /// <summary>
    /// Gauge of current depth of a named queue. Updated periodically by
    /// the worker (or any process that wants to scrape it). Label "queue"
    /// is the queue name.
    /// </summary>
    public static readonly Gauge QueueDepth = Prometheus.Metrics
        .CreateGauge(
            "forge_queue_depth",
            "Current depth of a job queue.",
            new GaugeConfiguration
            {
                LabelNames = new[] { "queue" }
            });

    /// <summary>
    /// Gauge of current DLQ depth. Updated alongside QueueDepth.
    /// </summary>
    public static readonly Gauge DlqDepth = Prometheus.Metrics
        .CreateGauge(
            "forge_dlq_depth",
            "Current depth of the dead letter queue.");

    /// <summary>
    /// Gauge of currently alive workers (workers with a non-expired
    /// heartbeat key in Redis). Updated by the janitor on each scan.
    /// </summary>
    public static readonly Gauge WorkersAlive = Prometheus.Metrics
        .CreateGauge(
            "forge_workers_alive",
            "Number of workers with a live heartbeat.");
}