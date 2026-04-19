using Forge.Api.Contracts;
using Forge.Core;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

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

    // Scheduled jobs will go through a different path (ZADD) in Milestone 4.
    // For Milestone 2, we only handle the immediate-execution case.
    if (job.ScheduledFor is null)
    {
        await queue.Enqueue(job.Queue, job.Id, ct);
    }

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

app.Run();