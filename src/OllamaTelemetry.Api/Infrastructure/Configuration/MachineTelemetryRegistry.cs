using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Infrastructure.Configuration;

public sealed class MachineTelemetryRegistry
{
    private readonly IReadOnlyList<MachineTelemetryTarget> _machines;
    private readonly IReadOnlyDictionary<string, MachineTelemetryTarget> _byId;

    public MachineTelemetryRegistry(IOptions<TelemetryOptions> options)
    {
        _machines = options.Value.Machines
            .Select(static machine => new MachineTelemetryTarget(
                machine.MachineId.Trim(),
                machine.DisplayName.Trim(),
                machine.SourceType.Trim(),
                new Uri(machine.Endpoint, UriKind.Absolute),
                SensorFilter.From(machine.Sensors)))
            .OrderBy(static machine => machine.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _byId = _machines.ToDictionary(static machine => machine.MachineId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MachineTelemetryTarget> All => _machines;

    public bool TryGet(string machineId, out MachineTelemetryTarget target)
        => _byId.TryGetValue(machineId, out target!);
}
