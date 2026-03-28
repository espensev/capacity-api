namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record ThermalSensorSample(
    string SensorKey,
    string SensorName,
    string SensorPath,
    double TemperatureC,
    double? MinTemperatureC,
    double? MaxTemperatureC);
