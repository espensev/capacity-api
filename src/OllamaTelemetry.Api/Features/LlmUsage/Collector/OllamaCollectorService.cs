using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.LlmUsage.Collector;

public sealed class OllamaCollectorService(
    HttpClient httpClient,
    LlmUsageRepository repository,
    OllamaStatusCache statusCache,
    IOptions<LlmUsageOptions> options,
    TimeProvider timeProvider,
    ILogger<OllamaCollectorService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private DateTimeOffset _nextPurgeUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredTargets = options.Value.Ollama.ResolveTargets();
        if (configuredTargets.Count == 0)
        {
            logger.LogInformation("Ollama collector is disabled.");
            statusCache.PruneExcept([]);
            return;
        }

        // Initial collection
        await CollectAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.Ollama.PollIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CollectAsync(stoppingToken);
        }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        var configuredTargets = options.Value.Ollama.ResolveTargets();
        statusCache.PruneExcept(configuredTargets.Select(static target => target.MachineId).ToArray());

        foreach (var target in configuredTargets)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                var displayName = ResolveDisplayName(target);
                var models = await FetchAvailableModelsAsync(target.Endpoint, cancellationToken);
                var running = await FetchRunningModelsAsync(target.Endpoint, cancellationToken);

                var runningNames = running.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                List<OllamaModelSnapshot> snapshots = [];

                foreach (var model in models)
                {
                    var runningModel = running.FirstOrDefault(r =>
                        string.Equals(r.Name, model.Name, StringComparison.OrdinalIgnoreCase));

                    snapshots.Add(new OllamaModelSnapshot(
                        target.MachineId,
                        displayName,
                        target.Endpoint,
                        model.Name,
                        model.Details?.Family ?? "unknown",
                        model.Details?.ParameterSize ?? "unknown",
                        model.Details?.QuantizationLevel ?? "unknown",
                        model.Size,
                        runningModel?.SizeVram ?? 0,
                        runningModel?.ContextLength ?? 0,
                        now,
                        runningNames.Contains(model.Name)));
                }

                await repository.InsertOllamaSnapshotBatchAsync(snapshots, cancellationToken);
                statusCache.Update(target.MachineId, displayName, target.Endpoint, snapshots, now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(
                    "Ollama unreachable for machine {MachineId} at {Endpoint}: {Message}",
                    target.MachineId,
                    target.Endpoint,
                    ex.Message);
                statusCache.MarkUnreachable(
                    target.MachineId,
                    ResolveDisplayName(target),
                    target.Endpoint,
                    timeProvider.GetUtcNow(),
                    ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ollama collection failed for machine {MachineId}.", target.MachineId);
                statusCache.MarkUnreachable(
                    target.MachineId,
                    ResolveDisplayName(target),
                    target.Endpoint,
                    timeProvider.GetUtcNow(),
                    ex.Message);
            }
        }

        await PurgeIfNeededAsync(cancellationToken);
    }

    private async Task PurgeIfNeededAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextPurgeUtc)
        {
            return;
        }

        try
        {
            await repository.PurgeOlderThanAsync(now.AddDays(-options.Value.RetentionDays), cancellationToken);
            _nextPurgeUtc = now.AddHours(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM usage purge failed.");
            _nextPurgeUtc = now.AddMinutes(5);
        }
    }

    private static string ResolveDisplayName(OllamaMachineOptions target)
        => string.IsNullOrWhiteSpace(target.DisplayName) ? target.MachineId : target.DisplayName.Trim();

    private static Uri OllamaUri(string endpoint, string path)
    {
        var baseUrl = endpoint.TrimEnd('/');
        return new Uri($"{baseUrl}/{path}");
    }

    private async Task<List<OllamaTagModel>> FetchAvailableModelsAsync(string endpoint, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>(
            OllamaUri(endpoint, "api/tags"), JsonOptions, cancellationToken);
        return response?.Models ?? [];
    }

    private async Task<List<OllamaPsModel>> FetchRunningModelsAsync(string endpoint, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<OllamaPsResponse>(
            OllamaUri(endpoint, "api/ps"), JsonOptions, cancellationToken);
        return response?.Models ?? [];
    }
}

// Ollama API response models

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagModel> Models { get; set; } = [];
}

internal sealed class OllamaTagModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

internal sealed class OllamaPsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaPsModel> Models { get; set; } = [];
}

internal sealed class OllamaPsModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("size_vram")]
    public long SizeVram { get; set; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

internal sealed class OllamaModelDetails
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
}
