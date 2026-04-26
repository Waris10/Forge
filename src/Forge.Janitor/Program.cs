using Forge.Janitor;
using Forge.Storage.Redis;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

var redisConnStr = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Redis is not configured.");

builder.Services.Configure<JanitorOptions>(builder.Configuration.GetSection("Janitor"));

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConnStr));

builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddSingleton<RedisDistributedLock>();

builder.Services.AddHostedService<JanitorService>();

builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var host = builder.Build();
host.Run();