using Forge.Core;
using Forge.Dashboard;
using Forge.Dashboard.Components;
using Forge.Dashboard.Live;
using Forge.Storage.Postgres;
using Forge.Storage.Redis;
using Prometheus;
using Serilog;
using StackExchange.Redis;

Log.Logger = LoggingSetup.Build("Forge.Dashboard").CreateLogger();

try
{
    Log.Information("Forge.Dashboard starting");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Options
    builder.Services.Configure<DashboardOptions>(
        builder.Configuration.GetSection("Dashboard"));

    // Storage — same connection strings as the other hosts
    var postgresConnStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

    var redisConnStr = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.");

    builder.Services.AddScoped<IJobRepository>(_ => new JobRepository(postgresConnStr));
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(redisConnStr));
    builder.Services.AddSingleton<IJobReadStore, RedisJobReadStore>();

    // Live broadcaster — singleton state + hosted service
    builder.Services.AddSingleton<LiveStateBroadcaster>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveStateBroadcaster>());

    // Modern Blazor Web App: Razor Components + Interactive Server render mode
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(); 

    var app = builder.Build();

    app.UseStaticFiles();
    app.MapStaticAssets();

    app.UseRouting();

    // /metrics middleware — instruments HTTP requests
    app.UseHttpMetrics();

    // Antiforgery must be after UseRouting and before endpoint mapping.
    // Razor Components require it; the framework refuses to serve any
    // interactive component endpoint without this in the pipeline.
    app.UseAntiforgery();

    app.MapMetrics();

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    // Front door — / redirects to /ops so the dashboard has a sensible
    // landing page instead of a 404.
    app.MapGet("/", () => Results.Redirect("/ops"));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Forge.Dashboard terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}