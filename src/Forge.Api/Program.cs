using Forge.Api.Contracts;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using StackExchange.Redis;

Log.Logger = LoggingSetup.Build("Forge.Api").CreateLogger();
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "Forge.Api"))
    .WithTracing(t => t
        .AddSource(Forge.Core.TracingSetup.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri("http://localhost:4317");
        }));


// --- Configuration ---
var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.");

// --- DI registration ---
builder.Services.AddScoped<IJobRepository>(_ => new JobRepository(postgresConnStr));

// Connection multiplexer is a single shared instance for the whole app —
// it handles pipelining, multiplexing, and reconnection internally.
// Registering as singleton is correct and necessary.
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnStr));

builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();

// --- Dapper one-time global config ---
DapperConfig.Configure();

var app = builder.Build();




// --- Endpoints ---

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.UseHttpMetrics();   // tracks HTTP request count/duration on the API
app.MapMetrics();        // exposes /metrics

app.MapPost("/jobs", async (
    SubmitJobRequest req,
    IJobRepository repo,
    IJobQueue queue,                     // NEW: injected
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.JobType))
        return Results.BadRequest(new { error = "jobType is required" });
    if (req.Priority is < 1 or > 10)
        return Results.BadRequest(new { error = "priority must be between 1 and 10" });
    if (req.MaxAttempts is < 1 or > 100)
        return Results.BadRequest(new { error = "maxAttempts must be between 1 and 100" });
    if (req.DelaySeconds is < 0)
        return Results.BadRequest(new { error = "delaySeconds cannot be negative" });

    if (req.IdempotencyKey is not null)
    {
        var existing = await repo.FindByIdempotencyKey(req.IdempotencyKey, ct);
        if (existing is not null)
        {
            return Results.Ok(new SubmitJobResponse(
                existing.Id, existing.Status.ToString().ToLowerInvariant()));
        }
    }

    var scheduledFor = req.DelaySeconds is > 0
        ? DateTimeOffset.UtcNow.AddSeconds(req.DelaySeconds.Value)
        : (DateTimeOffset?)null;

    using var activity = Forge.Core.TracingSetup.Source.StartActivity("api.submit");
    activity?.SetTag("job.type", req.JobType);

    var job = Job.NewQueued(
        jobType: req.JobType,
        payload: req.Payload,
        queue: req.Queue ?? "default",
        priority: req.Priority ?? 5,
        maxAttempts: req.MaxAttempts ?? 5,
        idempotencyKey: req.IdempotencyKey,
        scheduledFor: scheduledFor);

    // Persist first, then enqueue. Order matters:
    //   - If Postgres insert fails, we never touch Redis. Clean.
    //   - If Redis enqueue fails after Postgres insert, the row exists but no
    //     worker will see it. We'd need a "reconciler" to catch this. For now,
    //     this is a known corner — in practice Redis failures are rare enough
    //     that it's acceptable for a portfolio project. Milestone 5's janitor
    //     could be extended to sweep for such orphans.
    await repo.Insert(job, ct);

    activity?.SetTag("job.id", job.Id.ToString());

    if (job.ScheduledFor is { } runAt) //Means if Schedule is not null then access it via the .Value prop capture it value into runAt
    {
        await queue.Schedule(job.Queue, job.Id, runAt, ct);
    }
    else
    {
        await queue.Enqueue(job.Queue, job.Id, ct);
    }

    Forge.Core.Metrics.JobsSubmitted
    .WithLabels(job.JobType, job.Queue)
    .Inc();

    return Results.Accepted(
        $"/jobs/{job.Id}",
        new SubmitJobResponse(job.Id, "queued"));
});

app.MapGet("/jobs/{id:guid}", async (
    Guid id,
    IJobRepository repo,
    CancellationToken ct) =>
{
    var job = await repo.Get(id, ct);
    return job is null
        ? Results.NotFound(new { error = $"job {id} not found" })
        : Results.Ok(job);
});

app.MapPost("/jobs/{id:guid}/retry", async (
    Guid id,
    IJobRepository repo,
    IJobQueue queue,
    CancellationToken ct) =>
{
    var job = await repo.Get(id, ct);
    if (job is null)
        return Results.NotFound(new { error = "Job not found" });

    if (job.Status is not (JobStatus.Failed or JobStatus.Dead))
        return Results.Conflict(new
        {
            error = "Only failed or dead jobs can be retried",
            currentStatus = job.Status.ToString().ToLowerInvariant()
        });

    await repo.MarkForRetry(id, ct);
    await queue.Enqueue(job.Queue, id, ct);

    return Results.Accepted($"/jobs/{id}", new { id, status = "queued" });
});

app.MapPost("/dlq/retry-all", async (
    BulkRetryRequest? req,
    IJobRepository repo,
    IJobQueue queue,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    // Resolve target ids: explicit list if provided, otherwise every dead job.
    Guid[] targetIds;
    if (req?.Ids is { Length: > 0 } explicitIds)
    {
        targetIds = explicitIds;
    }
    else
    {
        var all = await repo.ListDeadJobIdsAsync(ct);
        targetIds = all.ToArray();
    }

    if (targetIds.Length == 0)
        return Results.Ok(new BulkRetryResponse(0, 0, 0, Array.Empty<Guid>()));

    var retried = 0;
    var failedIds = new List<Guid>();

    foreach (var id in targetIds)
    {
        if (ct.IsCancellationRequested) break;

        try
        {
            var job = await repo.Get(id, ct);

            // Two reasons to skip: not found (already cleaned up?) or no longer
            // in a retryable state (someone retried it between list and loop).
            if (job is null ||
                job.Status is not (JobStatus.Failed or JobStatus.Dead))
            {
                failedIds.Add(id);
                continue;
            }

            await repo.MarkForRetry(id, ct);
            await queue.Enqueue(job.Queue, id, ct);
            retried++;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bulk retry failed for {JobId}", id);
            failedIds.Add(id);
            // Continue — partial-success semantics.
        }
    }

    return Results.Ok(new BulkRetryResponse(
        Requested: targetIds.Length,
        Retried: retried,
        Failed: failedIds.Count,
        FailedIds: failedIds.ToArray()));
});

app.Run();