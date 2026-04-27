using Forge.Core;
using Forge.Scheduler;
using Forge.Storage.Redis;
using Forge.Worker;
using Prometheus;
using Serilog;
using StackExchange.Redis;

Log.Logger = LoggingSetup.Build("Forge.Scheduler").CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();
// --- Configuration ---

var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Redis is not configured.");

builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("Scheduler"));

// --- Storage ---

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnStr));

builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();

// --- Lock primitive ---

builder.Services.AddSingleton<RedisDistributedLock>();

// --- The scheduler itself ---

builder.Services.AddHostedService<PromotionService>();
builder.Services.AddHostedService<MetricsServerHost>();


// --- Graceful shutdown timeout ---

builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton(new KestrelMetricServer(port: 9102));

var host = builder.Build();
host.Run();