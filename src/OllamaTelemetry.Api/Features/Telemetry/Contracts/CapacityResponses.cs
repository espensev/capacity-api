namespace OllamaTelemetry.Api.Features.Telemetry.Contracts;

public sealed record MachineCapacityResponse(
    string MachineId,
    string DisplayName,
    string SourceType,
    string Endpoint,
    DateTimeOffset? CapturedAtUtc,
    int? LatencyMs,
    bool IsStale,
    string Verdict,
    GpuCapacitySummaryResponse? GpuSummary,
    IReadOnlyList<GpuDetailResponse> Gpus,
    CpuCapacityResponse? Cpu,
    MemoryCapacityResponse? Memory,
    IReadOnlyList<LoadedModelResponse> LoadedModels,
    string? LastError);

public sealed record GpuCapacitySummaryResponse(
    int GpuCount,
    double? MaxUtilizationPercent,
    long VramUsedBytes,
    long VramTotalBytes,
    long VramFreeBytes,
    double? MaxTemperatureC,
    double? TotalPowerWatts);

public sealed record GpuDetailResponse(
    int GpuIndex,
    string GpuName,
    double? UtilizationPercent,
    long? VramUsedBytes,
    long? VramTotalBytes,
    double? TemperatureC,
    double? PowerDrawWatts);

public sealed record CpuCapacityResponse(
    double? TotalUtilizationPercent,
    double? TemperatureC,
    double? PackagePowerWatts);

public sealed record MemoryCapacityResponse(
    long? UsedBytes,
    long? TotalBytes,
    long? FreeBytes);

public sealed record LoadedModelResponse(
    string ModelName,
    long SizeVramBytes,
    int ContextLength);

public sealed record BestFitResponse(
    DateTimeOffset GeneratedAtUtc,
    long RequestedVramBytes,
    IReadOnlyList<MachineFitCandidate> Candidates);

public sealed record MachineFitCandidate(
    string MachineId,
    string DisplayName,
    string Verdict,
    long VramFreeBytes,
    long VramTotalBytes,
    double? MaxGpuUtilizationPercent);
