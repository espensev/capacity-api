using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Infrastructure.Health;

public sealed class TelemetryCollectorHealthCheck(
    MachineTelemetryRegistry machineRegistry,
    LatestTelemetryCache latestTelemetryCache,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var staleAfter = TimeSpan.FromSeconds(options.Value.StaleAfterSeconds);
        var staleMachines = new List<string>();
        var observedMachines = 0;

        foreach (var machine in machineRegistry.All)
        {
            if (!latestTelemetryCache.TryGet(machine.MachineId, out var state))
            {
                continue;
            }

            observedMachines++;

            if (state.IsStale(staleAfter, timeProvider))
            {
                staleMachines.Add(machine.MachineId);
            }
        }

        if (observedMachines == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Telemetry has not been refreshed yet."));
        }

        if (staleMachines.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Telemetry is current for all refreshed machines."));
        }

        if (staleMachines.Count < observedMachines)
        {
            return Task.FromResult(HealthCheckResult.Degraded($"Telemetry is stale for: {string.Join(", ", staleMachines)}"));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy($"Telemetry is stale for all refreshed machines: {string.Join(", ", staleMachines)}"));
    }
}
