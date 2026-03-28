using System.Collections.Concurrent;
using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Features.Telemetry.Collector;

public sealed class LatestTelemetryCache
{
    private readonly ConcurrentDictionary<string, MachineTelemetryRuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);

    public void UpdateSuccess(MachineTelemetryTarget target, MachineCapacitySnapshot snapshot)
    {
        var state = new MachineTelemetryRuntimeState(
            target.MachineId,
            target.DisplayName,
            target.SourceType,
            snapshot.Endpoint,
            snapshot.CapturedAtUtc,
            snapshot.CapturedAtUtc,
            snapshot.LatencyMs,
            null,
            snapshot.ThermalSensors,
            snapshot.Gpus,
            snapshot.Cpu,
            snapshot.Memory,
            snapshot.LoadedModels);

        _states[target.MachineId] = state;
    }

    public void UpdateFailure(MachineTelemetryTarget target, DateTimeOffset occurredAtUtc, int latencyMs, string errorMessage)
    {
        var current = _states.GetOrAdd(target.MachineId, _ => MachineTelemetryRuntimeState.Empty(target));
        _states[target.MachineId] = current with
        {
            LastAttemptedAtUtc = occurredAtUtc,
            LastLatencyMs = latencyMs > 0 ? latencyMs : current.LastLatencyMs,
            LastError = errorMessage,
        };
    }

    public bool TryGet(string machineId, out MachineTelemetryRuntimeState state)
        => _states.TryGetValue(machineId, out state!);
}
