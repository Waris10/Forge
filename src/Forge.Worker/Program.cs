using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using Forge.Worker;
using Forge.Worker.Handlers;
using StackExchange.Redis;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);

// --- Configuration ---

var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not configured.");

var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Redis is not configured.");

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));

// --- Storage ---
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnStr));

builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddScoped<IJobRepository>(_ => new JobRepository(postgresConnStr));

// Dapper one-time global config (snake_case <-> PascalCase mapping).
DapperConfig.Configure();

// --- The in-process channel: puller -> executors ---

// Bounded gives us natural backpressure. If executors fall behind, the
// puller's WriteAsync will block once the channel hits capacity, which
// stops it from pulling more from Redis. Other workers will pick up the
// slack. No explicit throttling code anywhere.
//
// FullMode = Wait: the writer awaits a free slot. (Default is Wait, but
// being explicit makes the intent obvious to the next reader of this code.)
builder.Services.AddSingleton<Channel<Guid>>(_ =>
    Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity: 100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,  // multiple ExecutorService instances read
        SingleWriter = true    // exactly one PullerService writes
    }));

// --- Handlers ---

// Each handler is a singleton — they're stateless and reusable across jobs.
// We register them under their concrete type, then build a name->handler
// dictionary for the registry.
builder.Services.AddSingleton<NoOpHandler>();
builder.Services.AddSingleton<FlakyHandler>();
builder.Services.AddSingleton<SlowHandler>();

builder.Services.AddSingleton(sp =>
{
    var handlers = new Dictionary<string, IJobHandler>(StringComparer.OrdinalIgnoreCase)
    {
        ["NoOp"] = sp.GetRequiredService<NoOpHandler>(),
        ["Flaky"] = sp.GetRequiredService<FlakyHandler>(),
        ["Slow"] = sp.GetRequiredService<SlowHandler>(),

    };
    return new HandlerRegistry(handlers);
});

// --- Background services ---

// Order of registration is roughly the order Start is called, but they
// all run concurrently. The puller fills the channel; executors drain it.
builder.Services.AddHostedService<PullerService>();
builder.Services.AddHostedService<HeartbeatService>();

// Register N executors, where N comes from WorkerOptions.ExecutorCount.
// We need to read the option *now* (at registration time) to know how many
// to add. Each registered AddHostedService creates a separate instance.
{
    var workerOptions = new WorkerOptions();
    builder.Configuration.GetSection("Worker").Bind(workerOptions);

    for (var i = 0; i < workerOptions.ExecutorCount; i++)
    {
        builder.Services.AddHostedService<ExecutorService>();
    }
}

// --- Graceful shutdown timeout ---

// Default is 30s in newer .NET versions, but the spec calls for a generous
// shutdown window so in-flight jobs can finish. Set explicitly for clarity.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

var host = builder.Build();
host.Run();