using System.Collections.Concurrent;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;

namespace OllamaTelemetry.Api.Features.LlmUsage.Collector;

public sealed class OllamaStatusCache
{
    private readonly ConcurrentDictionary<string, OllamaStatus> _machines = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OllamaStatus> Current
        => _machines.Values
            .OrderBy(static machine => machine.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Update(
        string machineId,
        string displayName,
        string endpoint,
        IReadOnlyList<OllamaModelSnapshot> snapshots,
        DateTimeOffset capturedAtUtc)
    {
        _machines[machineId] = new OllamaStatus(
            machineId,
            displayName,
            endpoint,
            true,
            capturedAtUtc,
            null,
            snapshots);
    }

    public void MarkUnreachable(
        string machineId,
        string displayName,
        string endpoint,
        DateTimeOffset attemptedAtUtc,
        string error)
    {
        _machines[machineId] = new OllamaStatus(
            machineId,
            displayName,
            endpoint,
            false,
            attemptedAtUtc,
            error,
            []);
    }

    public void PruneExcept(IReadOnlyCollection<string> machineIds)
    {
        foreach (var machineId in _machines.Keys)
        {
            if (!machineIds.Contains(machineId, StringComparer.OrdinalIgnoreCase))
            {
                _machines.TryRemove(machineId, out _);
            }
        }
    }
}

public sealed record OllamaStatus(
    string MachineId,
    string DisplayName,
    string Endpoint,
    bool IsReachable,
    DateTimeOffset? LastCheckUtc,
    string? LastError,
    IReadOnlyList<OllamaModelSnapshot> Models)
{
    public static OllamaStatus Unknown(string machineId, string displayName, string endpoint)
        => new(machineId, displayName, endpoint, false, null, null, []);
}
