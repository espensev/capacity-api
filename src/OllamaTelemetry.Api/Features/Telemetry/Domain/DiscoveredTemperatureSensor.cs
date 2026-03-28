namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record DiscoveredTemperatureSensor(
    string SensorKey,
    string SensorName,
    string SensorPath);
