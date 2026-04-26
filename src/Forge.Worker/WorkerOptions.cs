namespace Forge.Worker;

/// <summary>
/// Configuration for a single worker process. Bound from the "Worker" section
/// of appsettings.json via <c>builder.Services.Configure&lt;WorkerOptions&gt;</c>.
///
/// Every field has a sensible default so the worker can boot with an empty
/// config section. Override what you need.
/// </summary>
public class WorkerOptions
{
    /// <summary>
    /// The Redis queue this worker pulls from. Defaults to "default".
    /// Later we'll let one process pull from multiple queues; for now, one.
    /// </summary>
    public string Queue { get; set; } = "default";

    /// <summary>
    /// How many <see cref="ExecutorService"/> instances to run in parallel.
    /// Defaults to <see cref="Environment.ProcessorCount"/>. For I/O-bound
    /// handlers you can crank this much higher than CPU count — async all
    /// the way down means more parallelism doesn't fight for CPU.
    /// </summary>
    public int ExecutorCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Stable identifier for this worker process. Used as the suffix in
    /// <c>forge:processing:{workerId}</c>, <c>forge:heartbeat:{workerId}</c>
    /// etc. — anywhere we need to tell workers apart in Redis.
    ///
    /// Defaults to "worker-{machineName}-{processId}", which is unique enough
    /// for a single dev machine. In Docker you'd set this from the container
    /// hostname or an env var.
    /// </summary>
    public string WorkerId { get; set; } =
        $"worker-{Environment.MachineName.ToLowerInvariant()}-{Environment.ProcessId}";

    /// <summary>
    /// How long the puller blocks on a single <c>BlockingPull</c> call before
    /// looping. Lower = more responsive shutdown, higher = fewer Redis round
    /// trips on idle queues. 5 seconds is the spec's pick and a good default.
    /// </summary>
    public TimeSpan PullTimeout { get; set; } = TimeSpan.FromSeconds(5);
}