using Forge.Scheduler;
using Forge.Storage.Redis;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

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

// --- Graceful shutdown timeout ---

builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var host = builder.Build();
host.Run();