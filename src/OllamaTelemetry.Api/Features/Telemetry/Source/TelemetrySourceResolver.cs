namespace OllamaTelemetry.Api.Features.Telemetry.Source;

public sealed class TelemetrySourceResolver(IEnumerable<IMachineMetricsSource> sources)
{
    private readonly IReadOnlyDictionary<string, IMachineMetricsSource> _sources = sources
        .ToDictionary(static source => source.SourceType, StringComparer.OrdinalIgnoreCase);

    public IMachineMetricsSource Resolve(string sourceType)
    {
        if (_sources.TryGetValue(sourceType, out var source))
        {
            return source;
        }

        throw new InvalidOperationException($"No telemetry source is registered for source type '{sourceType}'.");
    }
}
