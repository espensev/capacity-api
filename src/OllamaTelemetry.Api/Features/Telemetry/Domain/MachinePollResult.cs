namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public enum MachinePollStatus
{
    Success,
    Failure,
}

public sealed record MachinePollResult(
    MachineTelemetryTarget Target,
    MachinePollStatus Status,
    DateTimeOffset OccurredAtUtc,
    int LatencyMs,
    MachineCapacitySnapshot? Snapshot,
    string? ErrorMessage)
{
    public static MachinePollResult Failure(
        MachineTelemetryTarget target,
        DateTimeOffset occurredAtUtc,
        int latencyMs,
        string errorMessage)
        => new(target, MachinePollStatus.Failure, occurredAtUtc, latencyMs, null, errorMessage);
}
