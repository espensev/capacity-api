using OllamaTelemetry.Api.Features.LlmUsage.Collector;
using OllamaTelemetry.Api.Features.LlmUsage.Contracts;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;

namespace OllamaTelemetry.Api.Features.LlmUsage.Api;

public sealed class LlmUsageQueryService(
    LlmUsageRepository repository,
    OllamaStatusCache ollamaStatusCache,
    TimeProvider timeProvider)
{
    public async Task<LlmUsageOverviewResponse> GetOverviewAsync(int hours, CancellationToken cancellationToken)
    {
        var sinceUtc = timeProvider.GetUtcNow().AddHours(-hours);
        var summaries = await repository.GetUsageSummaryAsync(sinceUtc, cancellationToken);
        var ollamaStatuses = ollamaStatusCache.Current;

        var providerResponses = summaries.Select(s => new LlmProviderSummaryResponse(
            s.Provider,
            s.AssistantKind,
            s.MachineId,
            s.Model,
            s.TotalSessions,
            s.TotalToolCalls,
            s.TotalInputTokens,
            s.TotalOutputTokens,
            s.TotalCostUsd,
            s.FirstSeenUtc,
            s.LastSeenUtc)).ToArray();

        var totals = new LlmCostTotalsResponse(
            summaries.Sum(s => s.TotalCostUsd),
            summaries.Sum(s => s.TotalInputTokens),
            summaries.Sum(s => s.TotalOutputTokens),
            summaries.Sum(s => s.TotalSessions),
            summaries.Sum(s => s.TotalToolCalls));

        return new LlmUsageOverviewResponse(
            timeProvider.GetUtcNow(),
            ToOllamaStatusResponse(ollamaStatuses),
            providerResponses,
            totals);
    }

    public async Task<LlmSessionDetailResponse?> GetSessionDetailAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var records = await repository.GetSessionRecordsAsync(sessionId, cancellationToken);
        if (records.Count == 0) return null;

        var sessionStops = records.Where(r =>
            r.RecordType is "session_stop" or "session_end").ToList();
        var toolCalls = records.Where(r => r.RecordType == "tool_use").ToList();

        var totals = new LlmSessionTotalsResponse(
            sessionStops.Sum(r => r.InputTokens),
            sessionStops.Sum(r => r.OutputTokens),
            sessionStops.Sum(r => r.TotalCostUsd),
            toolCalls.Count,
            toolCalls.Count(r => r.WasError),
            sessionStops.Sum(r => r.DurationMs));

        return new LlmSessionDetailResponse(
            sessionId,
            records.Select(ToRecordResponse).ToArray(),
            totals);
    }

    public async Task<IReadOnlyList<LlmUsageRecordResponse>> GetRecentAsync(
        int limit,
        string? provider,
        string? recordType,
        string? machineId,
        CancellationToken cancellationToken)
    {
        var records = await repository.GetRecentRecordsAsync(limit, provider, recordType, machineId, cancellationToken);
        return records.Select(ToRecordResponse).ToArray();
    }

    public async Task<IReadOnlyList<OllamaInferenceRecordResponse>> GetRecentOllamaInferenceAsync(
        int hours,
        int limit,
        string? machineId,
        string? model,
        CancellationToken cancellationToken)
    {
        var sinceUtc = timeProvider.GetUtcNow().AddHours(-hours);
        var records = await repository.GetRecentOllamaInferenceAsync(sinceUtc, limit, machineId, model, cancellationToken);

        return records.Select(static r =>
        {
            var identity = OllamaModelIdentityParser.Parse(new OllamaModelSnapshot(
                r.MachineId,
                r.DisplayName,
                r.Endpoint,
                r.ModelName,
                "unknown",
                "unknown",
                "unknown",
                0,
                0,
                0,
                r.Timestamp,
                false));

            return new OllamaInferenceRecordResponse(
                r.MachineId,
                r.DisplayName,
                r.Endpoint,
                identity.MachineModelId,
                identity.CanonicalModelId,
                identity.DisplayLabel,
                r.ModelName,
                r.Timestamp,
                r.PromptTokens,
                r.CompletionTokens,
                r.TokensPerSecond,
                r.TotalDurationNs / 1_000_000,
                r.PromptEvalDurationNs / 1_000_000,
                r.EvalDurationNs / 1_000_000);
        }).ToArray();
    }

    public async Task<IReadOnlyList<OllamaModelResponse>> GetOllamaModelsAsync(
        string? machineId,
        bool loadedOnly,
        CancellationToken cancellationToken)
    {
        var snapshots = await repository.GetLatestOllamaSnapshotsAsync(machineId, cancellationToken);
        return snapshots
            .Where(snapshot => !loadedOnly || snapshot.IsLoaded)
            .Select(ToOllamaModelResponse)
            .OrderBy(static snapshot => snapshot.MachineId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static snapshot => snapshot.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LlmUsageRecordResponse ToRecordResponse(LlmUsageRecord r) => new(
        r.Provider,
        r.AssistantKind,
        r.MachineId,
        r.RecordType,
        r.SessionId,
        r.Model,
        r.Timestamp,
        r.InputTokens,
        r.OutputTokens,
        r.CacheReadTokens,
        r.CacheCreationTokens,
        r.TotalCostUsd,
        r.NumTurns,
        r.DurationMs,
        r.ToolName,
        r.WasError);

    private static OllamaStatusResponse ToOllamaStatusResponse(IReadOnlyList<OllamaStatus> statuses)
        => new(
            statuses.Count,
            statuses.Count(static status => status.IsReachable),
            statuses.Select(static status => new OllamaMachineStatusResponse(
                status.MachineId,
                status.DisplayName,
                status.Endpoint,
                status.IsReachable,
                status.LastCheckUtc,
                status.LastError,
                status.Models.Count,
                status.Models.Count(static model => model.IsLoaded))).ToArray(),
            statuses
                .SelectMany(static status => status.Models.Select(ToOllamaModelResponse))
                .OrderBy(static model => model.MachineId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static OllamaModelResponse ToOllamaModelResponse(OllamaModelSnapshot model)
    {
        var identity = OllamaModelIdentityParser.Parse(model);
        return new OllamaModelResponse(
            model.MachineId,
            model.DisplayName,
            identity.MachineModelId,
            identity.CanonicalModelId,
            identity.DisplayLabel,
            identity.ShortLabel,
            model.ModelName,
            identity.Family,
            identity.FamilySlug,
            identity.Tag,
            identity.ParameterSize,
            identity.QuantizationLevel,
            FormatBytes(model.SizeBytes),
            FormatBytes(model.SizeVramBytes),
            model.ContextLength,
            model.IsLoaded);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}
