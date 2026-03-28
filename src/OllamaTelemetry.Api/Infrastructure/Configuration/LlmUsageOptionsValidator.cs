using Microsoft.Extensions.Options;

namespace OllamaTelemetry.Api.Infrastructure.Configuration;

public sealed class LlmUsageOptionsValidator : IValidateOptions<LlmUsageOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmUsageOptions options)
    {
        List<string> errors = [];
        HashSet<string> seenMachineIds = new(StringComparer.OrdinalIgnoreCase);

        if (options.RetentionDays < 1)
        {
            errors.Add("LlmUsage:RetentionDays must be greater than zero.");
        }

        var ollamaTargets = options.Ollama.ResolveTargets();

        foreach (var machine in ollamaTargets)
        {
            if (!seenMachineIds.Add(machine.MachineId))
            {
                errors.Add($"Duplicate Ollama machine id '{machine.MachineId}'.");
            }

            if (!Uri.TryCreate(machine.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Ollama machine '{machine.MachineId}' must define an absolute http/https Endpoint.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
