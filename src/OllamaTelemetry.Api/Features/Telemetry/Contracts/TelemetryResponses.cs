namespace OllamaTelemetry.Api.Features.Telemetry.Contracts;

public sealed record TelemetryOverviewResponse(
    DateTimeOffset GeneratedAtUtc,
    int StaleAfterSeconds,
    IReadOnlyList<MachineTelemetryStateResponse> Machines);

public sealed record MachineTelemetryStateResponse(
    string MachineId,
    string DisplayName,
    string SourceType,
    string Endpoint,
    DateTimeOffset? LastAttemptedAtUtc,
    DateTimeOffset? LastSuccessfulAtUtc,
    int? LastLatencyMs,
    bool IsStale,
    string? LastError,
    string? HottestSensorKey,
    double? HottestTemperatureC,
    double? MaxGpuUtilizationPercent,
    long? VramFreeBytes,
    long? VramTotalBytes,
    double? CpuUtilizationPercent,
    long? RamUsedBytes,
    long? RamTotalBytes,
    IReadOnlyList<ThermalSensorResponse> Sensors);

public sealed record ThermalSensorResponse(
    string SensorKey,
    string SensorName,
    string SensorPath,
    double TemperatureC,
    double? MinTemperatureC,
    double? MaxTemperatureC);

public sealed record ThermalHistoryResponse(
    string MachineId,
    string SensorKey,
    DateTimeOffset FromUtc,
    IReadOnlyList<ThermalHistoryPointResponse> Points);

public sealed record ThermalHistoryPointResponse(
    DateTimeOffset CapturedAtUtc,
    double TemperatureC,
    double? MinTemperatureC,
    double? MaxTemperatureC);

public sealed record SensorDiscoveryResponse(
    string MachineId,
    DateTimeOffset DiscoveredAtUtc,
    IReadOnlyList<DiscoveredTemperatureSensorResponse> Sensors);

public sealed record DiscoveredTemperatureSensorResponse(
    string SensorKey,
    string SensorName,
    string SensorPath);
