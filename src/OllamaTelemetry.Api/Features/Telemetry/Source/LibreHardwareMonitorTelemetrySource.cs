using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Source;

public sealed class LibreHardwareMonitorTelemetrySource(
    HttpClient httpClient,
    LibreHardwareJsonParser parser,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider) : IMachineMetricsSource
{
    private static readonly string[] JsonCandidatePaths = ["data.json", "json"];
    private readonly ConcurrentDictionary<string, Uri> _resolvedEndpoints = new(StringComparer.OrdinalIgnoreCase);

    public string SourceType => "LibreHardwareMonitor";

    public async Task<MachineCapacitySnapshot> CollectAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
    {
        var (document, resolvedEndpoint, startedAtUtc, latencyMs) = await LoadDocumentAsync(target, cancellationToken);

        using (document)
        {
            var sensors = parser.ParseTemperatures(document, target.SensorFilter);
            var metrics = parser.ParseMetrics(document);

            return new MachineCapacitySnapshot(
                target.MachineId,
                target.DisplayName,
                target.SourceType,
                resolvedEndpoint,
                startedAtUtc,
                latencyMs,
                metrics.Gpus,
                metrics.Cpu,
                metrics.Memory,
                sensors,
                []);
        }
    }

    public async Task<IReadOnlyList<DiscoveredTemperatureSensor>> DiscoverAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
    {
        var (document, _, _, _) = await LoadDocumentAsync(target, cancellationToken);

        using (document)
        {
            return parser.DiscoverTemperatures(document, target.SensorFilter);
        }
    }

    private async Task<(JsonDocument Document, Uri ResolvedEndpoint, DateTimeOffset StartedAtUtc, int LatencyMs)> LoadDocumentAsync(
        MachineTelemetryTarget target,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        foreach (var candidate in GetCandidateEndpoints(target))
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(options.Value.Source.RequestTimeoutSeconds));

            var startedAtUtc = timeProvider.GetUtcNow();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, timeoutCancellation.Token);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCancellation.Token);
                _resolvedEndpoints[target.MachineId] = candidate;

                return (document, candidate, startedAtUtc, (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"Timed out after {options.Value.Source.RequestTimeoutSeconds} second(s) while requesting '{candidate}' for '{target.MachineId}'.",
                    ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"Unable to resolve LibreHardwareMonitor JSON for '{target.MachineId}' starting from '{target.Endpoint}'.",
            lastException);
    }

    private IEnumerable<Uri> GetCandidateEndpoints(MachineTelemetryTarget target)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (_resolvedEndpoints.TryGetValue(target.MachineId, out var cachedEndpoint) && seen.Add(cachedEndpoint.AbsoluteUri))
        {
            yield return cachedEndpoint;
        }

        foreach (var candidate in ExpandCandidates(target.Endpoint))
        {
            if (seen.Add(candidate.AbsoluteUri))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<Uri> ExpandCandidates(Uri configuredEndpoint)
    {
        yield return configuredEndpoint;

        var authorityRoot = new Uri($"{configuredEndpoint.Scheme}://{configuredEndpoint.Authority}/");
        if (configuredEndpoint != authorityRoot)
        {
            yield return authorityRoot;
        }

        if (IsLikelyJsonEndpoint(configuredEndpoint))
        {
            yield break;
        }

        foreach (var candidatePath in JsonCandidatePaths)
        {
            yield return new Uri(authorityRoot, candidatePath);
        }
    }

    private static bool IsLikelyJsonEndpoint(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.Trim('/');
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "data", StringComparison.OrdinalIgnoreCase);
    }
}
