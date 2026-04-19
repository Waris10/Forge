using Forge.Api.Contracts;
using Forge.Core;
using Forge.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not configured. " +
        "Set it in appsettings.json or via ConnectionStrings__Postgres env var.");

// --- DI registration ---
// Scoped so each HTTP request gets its own IJobRepository instance. Underneath,
// the Npgsql connection pool shares the actual TCP connections — so "scoped"
// here is a lightweight wrapper, not a per-request connection.
builder.Services.AddScoped<IJobRepository>(_ => new JobRepository(postgresConnStr));

// --- Dapper one-time global config ---
DapperConfig.Configure();

var app = builder.Build();









// --- Endpoints ---

// Liveness probe. Just "am I running?" — no dependency checks.
// Kubernetes / Fly.io hit this to know if the process should be restarted.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Submit a new job.
app.MapPost("/jobs", async (SubmitJobRequest req, IJobRepository repo, CancellationToken ct) =>
{
    // --- Validation (hand-rolled, per our "native" path choice) ---
    if (string.IsNullOrWhiteSpace(req.JobType))
        return Results.BadRequest(new { error = "jobType is required" });

    if (req.Priority is < 1 or > 10)
        return Results.BadRequest(new { error = "priority must be between 1 and 10" });

    if (req.MaxAttempts is < 1 or > 100)
        return Results.BadRequest(new { error = "maxAttempts must be between 1 and 100" });

    if (req.DelaySeconds is < 0)
        return Results.BadRequest(new { error = "delaySeconds cannot be negative" });

    // --- Idempotency short-circuit ---
    // If a client resubmits with the same idempotency key, return the existing
    // job. This is how you survive clients that retry on flaky networks.
    if (req.IdempotencyKey is not null)
    {
        var existing = await repo.FindByIdempotencyKey(req.IdempotencyKey, ct);
        if (existing is not null)
        {
            return Results.Ok(new SubmitJobResponse(
                existing.Id,
                existing.Status.ToString().ToLowerInvariant()));
        }
    }

    // --- Build the job ---
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

    // --- Persist ---
    // Note: no Redis yet. For Milestone 1 the job is just a row in Postgres;
    // nothing will actually execute it. Milestone 2 adds the Redis enqueue
    // right after this Insert call.
    await repo.Insert(job, ct);

    return Results.Accepted(
        $"/jobs/{job.Id}",
        new SubmitJobResponse(job.Id, "queued"));
});

// Fetch a job by id.
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