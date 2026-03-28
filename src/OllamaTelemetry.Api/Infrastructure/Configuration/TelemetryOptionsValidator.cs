using Microsoft.Extensions.Options;

namespace OllamaTelemetry.Api.Infrastructure.Configuration;

public sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        List<string> errors = [];
        HashSet<string> seenMachineIds = new(StringComparer.OrdinalIgnoreCase);

        if (options.Machines.Count == 0)
        {
            errors.Add("Telemetry:Machines must contain at least one machine.");
        }

        if (options.RefreshAfterSeconds > options.StaleAfterSeconds)
        {
            errors.Add("Telemetry:RefreshAfterSeconds must be less than or equal to Telemetry:StaleAfterSeconds.");
        }

        foreach (var machine in options.Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.MachineId))
            {
                errors.Add("Each telemetry machine must define a non-empty MachineId.");
            }
            else if (!seenMachineIds.Add(machine.MachineId))
            {
                errors.Add($"Duplicate telemetry machine id '{machine.MachineId}'.");
            }

            if (string.IsNullOrWhiteSpace(machine.DisplayName))
            {
                errors.Add($"Telemetry machine '{machine.MachineId}' must define DisplayName.");
            }

            if (string.Equals(machine.SourceType, "Nvml", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(machine.Endpoint)
                    || !Uri.TryCreate(machine.Endpoint, UriKind.Absolute, out _))
                {
                    errors.Add($"Telemetry machine '{machine.MachineId}' must define a valid absolute Endpoint URI.");
                }
            }
            else if (string.Equals(machine.SourceType, "LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(machine.Endpoint, UriKind.Absolute, out var endpoint)
                    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"Telemetry machine '{machine.MachineId}' must define an absolute http/https Endpoint.");
                }
            }
            else
            {
                errors.Add($"Telemetry machine '{machine.MachineId}' has unsupported SourceType '{machine.SourceType}'.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
