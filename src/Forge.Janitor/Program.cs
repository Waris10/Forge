using Forge.Core;
using Forge.Janitor;
using Forge.Storage.Redis;
using Forge.Worker;
using Prometheus;
using Serilog;
using StackExchange.Redis;


Log.Logger = LoggingSetup.Build("Forge.Janitor").CreateLogger();
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog();

var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Redis is not configured.");

builder.Services.Configure<JanitorOptions>(builder.Configuration.GetSection("Janitor"));

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnStr));

builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddSingleton<RedisDistributedLock>();

builder.Services.AddHostedService<JanitorService>();
builder.Services.AddHostedService<MetricsServerHost>();


builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton(new KestrelMetricServer(port: 9103));

var host = builder.Build();
host.Run();