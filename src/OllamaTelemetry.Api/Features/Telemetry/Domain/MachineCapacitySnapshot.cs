namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record MachineCapacitySnapshot(
    string MachineId,
    string DisplayName,
    string SourceType,
    Uri Endpoint,
    DateTimeOffset CapturedAtUtc,
    int LatencyMs,
    IReadOnlyList<GpuMetrics> Gpus,
    CpuMetrics? Cpu,
    MemoryMetrics? Memory,
    IReadOnlyList<ThermalSensorSample> ThermalSensors,
    IReadOnlyList<LoadedModelInfo> LoadedModels)
{
    public long TotalVramFreeBytes => Gpus
        .Where(static g => g.VramTotalBytes.HasValue && g.VramUsedBytes.HasValue)
        .Sum(static g => g.VramTotalBytes!.Value - g.VramUsedBytes!.Value);

    public long TotalVramTotalBytes => Gpus
        .Where(static g => g.VramTotalBytes.HasValue)
        .Sum(static g => g.VramTotalBytes!.Value);

    public double? MaxGpuUtilizationPercent => Gpus.Any(static g => g.UtilizationPercent.HasValue)
        ? Gpus.Where(static g => g.UtilizationPercent.HasValue).Max(static g => g.UtilizationPercent!.Value)
        : null;
}

public sealed record GpuMetrics(
    int GpuIndex,
    string GpuName,
    double? UtilizationPercent,
    long? VramUsedBytes,
    long? VramTotalBytes,
    double? TemperatureC,
    double? PowerDrawWatts);

public sealed record CpuMetrics(
    double? TotalUtilizationPercent,
    double? TemperatureC,
    double? PackagePowerWatts);

public sealed record MemoryMetrics(
    long? UsedBytes,
    long? TotalBytes);

public sealed record LoadedModelInfo(
    string ModelName,
    long SizeVramBytes,
    int ContextLength);
