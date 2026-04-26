using Forge.Storage.Redis;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Forge.Worker;

/// <summary>
/// Pulls job ids from Redis and writes them into the in-process
/// <see cref="Channel{T}"/> that the executors read from.
///
/// One puller per worker process. Runs as a <see cref="BackgroundService"/>
/// so the .NET host owns its lifecycle: <see cref="ExecuteAsync"/> is called
/// at startup, <c>stoppingToken</c> is signalled on SIGTERM, and the host
/// waits for this method to return before continuing shutdown.
///
/// The channel writer is completed on exit so executors know to drain and
/// stop — that's how graceful shutdown propagates from "no more work coming"
/// to "executors finish their last job and exit."
/// </summary>
public class PullerService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly ChannelWriter<Guid> _writer;
    private readonly WorkerOptions _options;
    private readonly ILogger<PullerService> _logger;

    public PullerService(
        IJobQueue queue,
        Channel<Guid> channel,
        IOptions<WorkerOptions> options,
        ILogger<PullerService> logger)
    {
        _queue = queue;
        _writer = channel.Writer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Puller started. WorkerId={WorkerId} Queue={Queue} PullTimeout={PullTimeout}",
            _options.WorkerId, _options.Queue, _options.PullTimeout);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var jobId = await _queue.BlockingPull(
                        queueName: _options.Queue,
                        workerId: _options.WorkerId,
                        timeout: _options.PullTimeout,
                        ct: stoppingToken);

                    if (jobId is null)
                        continue;  // timeout, no job — loop and try again

                    _logger.LogDebug("Pulled job {JobId} into channel", jobId);
                    await _writer.WriteAsync(jobId.Value, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown. Fall out of the loop.
                    break;
                }
                catch (Exception ex)
                {
                    // Don't crash the loop on transient errors (Redis blip, etc.).
                    // Back off briefly so we don't spin.
                    _logger.LogError(ex, "Puller loop error, backing off 1s");
                    try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            // Tell executors no more work is coming. Their await foreach over
            // ChannelReader.ReadAllAsync will complete naturally.
            _writer.Complete();
            _logger.LogInformation("Puller stopped. Channel completed.");
        }
    }
}