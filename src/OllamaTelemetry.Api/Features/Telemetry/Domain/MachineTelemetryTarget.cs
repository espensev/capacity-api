using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Domain;

public sealed record MachineTelemetryTarget(
    string MachineId,
    string DisplayName,
    string SourceType,
    Uri Endpoint,
    SensorFilter SensorFilter);

public sealed record SensorFilter(
    IReadOnlyList<string> IncludeKeywords,
    IReadOnlyList<string> ExcludeKeywords)
{
    public static SensorFilter From(SensorFilterOptions options)
        => new(
            options.IncludeKeywords
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(static keyword => keyword.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            options.ExcludeKeywords
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(static keyword => keyword.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    public bool Matches(string value)
    {
        if (ExcludeKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return IncludeKeywords.Count == 0
            || IncludeKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
