using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Collector;

public sealed class ReadingPersistencePolicy
{
    private readonly ConcurrentDictionary<string, PersistedSensorState> _persistedSensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _minimumDeltaCelsius;
    private readonly TimeSpan _forceWriteInterval;

    public ReadingPersistencePolicy(IOptions<TelemetryOptions> options)
    {
        _minimumDeltaCelsius = options.Value.Persistence.MinimumDeltaCelsius;
        _forceWriteInterval = TimeSpan.FromSeconds(options.Value.Persistence.ForceWriteIntervalSeconds);
    }

    public IReadOnlyList<ThermalSensorSample> SelectSensorsToPersist(MachineCapacitySnapshot snapshot)
    {
        List<ThermalSensorSample> sensorsToPersist = [];

        foreach (var sensor in snapshot.ThermalSensors.OrderBy(static sensor => sensor.SensorKey, StringComparer.Ordinal))
        {
            var stateKey = $"{snapshot.MachineId}:{sensor.SensorKey}";
            var shouldPersist = !_persistedSensors.TryGetValue(stateKey, out var persisted)
                || Math.Abs(persisted.TemperatureC - sensor.TemperatureC) >= _minimumDeltaCelsius
                || snapshot.CapturedAtUtc - persisted.PersistedAtUtc >= _forceWriteInterval;

            if (!shouldPersist)
            {
                continue;
            }

            _persistedSensors[stateKey] = new PersistedSensorState(snapshot.CapturedAtUtc, sensor.TemperatureC);
            sensorsToPersist.Add(sensor);
        }

        return sensorsToPersist;
    }

    private sealed record PersistedSensorState(DateTimeOffset PersistedAtUtc, double TemperatureC);
}
