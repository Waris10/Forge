using Forge.Storage.Redis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forge.Worker;

/// <summary>
/// Periodically writes this worker's heartbeat key with a TTL. The janitor
/// uses absence of the heartbeat to detect dead workers and recover their
/// in-flight jobs.
///
/// The relationship between <see cref="WorkerOptions.HeartbeatInterval"/>
/// and <see cref="WorkerOptions.HeartbeatTtl"/> matters: TTL must be
/// noticeably longer than interval, so a missed tick doesn't immediately
/// look like death. We use 10s interval / 30s TTL — three writes per TTL
/// window, like the scheduler's lock refresh.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly WorkerOptions _options;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IJobQueue queue,
        IOptions<WorkerOptions> options,
        ILogger<HeartbeatService> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Heartbeat started. WorkerId={WorkerId} Interval={Interval} Ttl={Ttl}",
            _options.WorkerId, _options.HeartbeatInterval, _options.HeartbeatTtl);

        // First write before the loop, so the worker is "alive" immediately
        // rather than only after the first interval elapses.
        try
        {
            await _queue.Heartbeat(_options.WorkerId, _options.HeartbeatTtl, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial heartbeat failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await _queue.Heartbeat(_options.WorkerId, _options.HeartbeatTtl, stoppingToken);
            }
            catch (Exception ex)
            {
                // Don't crash the loop on transient Redis errors — a few
                // missed heartbeats just shorten the window before the
                // janitor would consider this worker dead, which is fine.
                _logger.LogWarning(ex, "Heartbeat write failed; will retry next interval.");
            }
        }

        _logger.LogInformation("Heartbeat stopped.");

        // Note: we deliberately do NOT delete the heartbeat key on shutdown.
        // Reasoning: if shutdown takes 30s for in-flight jobs to finish, the
        // janitor would otherwise see "no heartbeat + non-empty processing
        // list" and recover jobs that are perfectly fine. Letting the TTL
        // expire naturally gives in-flight jobs a chance to finish first.
    }
}