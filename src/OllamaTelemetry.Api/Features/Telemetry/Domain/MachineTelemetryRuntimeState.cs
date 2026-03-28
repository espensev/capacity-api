namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record MachineTelemetryRuntimeState(
    string MachineId,
    string DisplayName,
    string SourceType,
    Uri Endpoint,
    DateTimeOffset? LastAttemptedAtUtc,
    DateTimeOffset? LastSuccessfulAtUtc,
    int? LastLatencyMs,
    string? LastError,
    IReadOnlyList<ThermalSensorSample> CurrentSensors,
    IReadOnlyList<GpuMetrics> Gpus,
    CpuMetrics? Cpu,
    MemoryMetrics? Memory,
    IReadOnlyList<LoadedModelInfo> LoadedModels)
{
    public static MachineTelemetryRuntimeState Empty(MachineTelemetryTarget target)
        => new(
            target.MachineId,
            target.DisplayName,
            target.SourceType,
            target.Endpoint,
            null,
            null,
            null,
            null,
            Array.Empty<ThermalSensorSample>(),
            Array.Empty<GpuMetrics>(),
            null,
            null,
            Array.Empty<LoadedModelInfo>());

    public bool IsStale(TimeSpan staleAfter, TimeProvider timeProvider)
        => LastSuccessfulAtUtc is null || timeProvider.GetUtcNow() - LastSuccessfulAtUtc.Value > staleAfter;
}
