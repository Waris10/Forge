using Microsoft.Extensions.Hosting;
using Prometheus;

namespace Forge.Worker;

/// <summary>
/// Starts and stops a standalone Kestrel-backed HTTP server exposing
/// /metrics for Prometheus scraping. Workers are not web hosts otherwise,
/// so we run this side server purely for observability.
/// </summary>
public class MetricsServerHost : IHostedService
{
    private readonly KestrelMetricServer _server;
    public MetricsServerHost(KestrelMetricServer server) => _server = server;
    public Task StartAsync(CancellationToken ct) { _server.Start(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => _server.StopAsync();
}