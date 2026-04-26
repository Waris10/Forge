using System.Diagnostics;
using System.Threading.Channels;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using Forge.Worker.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forge.Worker;

/// <summary>
/// Reads job ids from the in-process channel, loads each job from Postgres,
/// resolves and runs the registered handler, then acks Redis and writes the
/// terminal status back to Postgres.
///
/// Multiple ExecutorService instances run per worker process — one per slot
/// configured by <see cref="WorkerOptions.ExecutorCount"/>. They all read
/// from the same channel; <see cref="Channel{T}"/> is thread-safe and any
/// given job id is delivered to exactly one reader.
///
/// Failure handling for Milestone 2: log + MarkFailed + Ack. Real retry
/// logic (exponential backoff, reschedule, DLQ on exhaustion) lands in M3.
/// The shape of this method stays the same; only the catch block changes.
/// </summary>
public class ExecutorService : BackgroundService
{
    private readonly ChannelReader<Guid> _reader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobQueue _queue;
    private readonly HandlerRegistry _handlers;
    private readonly WorkerOptions _options;
    private readonly ILogger<ExecutorService> _logger;

    public ExecutorService(
        Channel<Guid> channel,
        IServiceScopeFactory scopeFactory,
        IJobQueue queue,
        HandlerRegistry handlers,
        IOptions<WorkerOptions> options,
        ILogger<ExecutorService> logger)
    {
        _reader = channel.Reader;
        _scopeFactory = scopeFactory;
        _queue = queue;
        _handlers = handlers;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Executor started.");

        try
        {
            await foreach (var jobId in _reader.ReadAllAsync(stoppingToken))
            {
                await ProcessOne(jobId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Executor stopped.");
    }

    private async Task ProcessOne(Guid jobId, CancellationToken stoppingToken)
    {
        // Per-job DI scope. IJobRepository is registered as scoped, so each
        // job gets its own connection from the Npgsql pool. Without this,
        // we'd be resolving a scoped service from the singleton root scope
        // and DI would (rightly) throw.
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = await repo.Get(jobId, stoppingToken);
        if (job is null)
        {
            // Postgres has no record of this id. Either it was deleted, or
            // someone pushed garbage onto the queue. Ack it so it doesn't
            // sit in our processing list forever.
            _logger.LogWarning("Job {JobId} not found in Postgres. Acking and skipping.", jobId);
            await _queue.Ack(_options.WorkerId, jobId, stoppingToken);
            return;
        }

        await repo.MarkRunning(jobId, stoppingToken);

        var sw = Stopwatch.StartNew();
        try
        {
            var handler = _handlers.Resolve(job.JobType);
            if (handler is null)
            {
                throw new InvalidOperationException(
                    $"No handler registered for job type '{job.JobType}'.");
            }

            await handler.Handle(job.Payload, stoppingToken);

            sw.Stop();
            await _queue.Ack(_options.WorkerId, jobId, stoppingToken);
            await repo.MarkSucceeded(jobId, (int)sw.ElapsedMilliseconds, stoppingToken);

            _logger.LogInformation(
                "Job {JobId} ({JobType}) succeeded in {DurationMs}ms",
                jobId, job.JobType, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Worker is shutting down mid-execution. Don't mark failed —
            // the janitor (M5) will requeue it from the processing list.
            // For M2 with no janitor, the row stays "running" until manual
            // cleanup. That's fine; shutdown mid-job is a known M2 limit.
            _logger.LogWarning("Job {JobId} interrupted by shutdown.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Job {JobId} ({JobType}) failed after {DurationMs}ms: {Message}",
                jobId, job.JobType, sw.ElapsedMilliseconds, ex.Message);

            // M2 failure path: terminal. M3 replaces this with retry logic.
            await _queue.Ack(_options.WorkerId, jobId, CancellationToken.None);
            await repo.MarkFailed(
                jobId,
                ex.Message,
                (int)sw.ElapsedMilliseconds,
                CancellationToken.None);
        }
    }
}