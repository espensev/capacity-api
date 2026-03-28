using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Features.Telemetry.Source;

public interface IMachineMetricsSource
{
    string SourceType { get; }

    Task<MachineCapacitySnapshot> CollectAsync(MachineTelemetryTarget target, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscoveredTemperatureSensor>> DiscoverAsync(MachineTelemetryTarget target, CancellationToken cancellationToken);
}
