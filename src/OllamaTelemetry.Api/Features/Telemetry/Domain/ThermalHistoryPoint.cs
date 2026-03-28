namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record ThermalHistoryPoint(
    DateTimeOffset CapturedAtUtc,
    double TemperatureC,
    double? MinTemperatureC,
    double? MaxTemperatureC);
