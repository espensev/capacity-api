namespace OllamaTelemetry.Api.Features.LlmUsage.Contracts;

public sealed record LlmUsageOverviewResponse(
    DateTimeOffset GeneratedAtUtc,
    OllamaStatusResponse Ollama,
    IReadOnlyList<LlmProviderSummaryResponse> Providers,
    LlmCostTotalsResponse Totals);

public sealed record OllamaStatusResponse(
    int MachineCount,
    int ReachableMachineCount,
    IReadOnlyList<OllamaMachineStatusResponse> Machines,
    IReadOnlyList<OllamaModelResponse> Models);

public sealed record OllamaMachineStatusResponse(
    string MachineId,
    string DisplayName,
    string Endpoint,
    bool IsReachable,
    DateTimeOffset? LastCheckUtc,
    string? LastError,
    int TotalModelCount,
    int LoadedModelCount);

public sealed record OllamaModelResponse(
    string MachineId,
    string DisplayName,
    string MachineModelId,
    string CanonicalModelId,
    string DisplayLabel,
    string ShortLabel,
    string ModelName,
    string Family,
    string FamilySlug,
    string? Tag,
    string ParameterSize,
    string QuantizationLevel,
    string SizeDisplay,
    string VramDisplay,
    int ContextLength,
    bool IsLoaded);

public sealed record LlmProviderSummaryResponse(
    string Provider,
    string? AssistantKind,
    string? MachineId,
    string? Model,
    int TotalSessions,
    int TotalToolCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCostUsd,
    DateTimeOffset? FirstSeenUtc,
    DateTimeOffset? LastSeenUtc);

public sealed record LlmCostTotalsResponse(
    double TotalCostUsd,
    long TotalInputTokens,
    long TotalOutputTokens,
    int TotalSessions,
    int TotalToolCalls);

public sealed record LlmUsageRecordResponse(
    string Provider,
    string? AssistantKind,
    string? MachineId,
    string RecordType,
    string SessionId,
    string? Model,
    DateTimeOffset Timestamp,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    double TotalCostUsd,
    int NumTurns,
    long DurationMs,
    string? ToolName,
    bool WasError);

public sealed record LlmSessionDetailResponse(
    string SessionId,
    IReadOnlyList<LlmUsageRecordResponse> Records,
    LlmSessionTotalsResponse Totals);

public sealed record LlmSessionTotalsResponse(
    int TotalInputTokens,
    int TotalOutputTokens,
    double TotalCostUsd,
    int ToolCalls,
    int Errors,
    long TotalDurationMs);

public sealed record OllamaInferenceRecordResponse(
    string MachineId,
    string DisplayName,
    string Endpoint,
    string MachineModelId,
    string CanonicalModelId,
    string DisplayLabel,
    string ModelName,
    DateTimeOffset Timestamp,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    long TotalDurationMs,
    long PromptEvalDurationMs,
    long EvalDurationMs);

// Ingest DTOs (from hook)

public sealed record LlmUsageIngestRequest(
    string Type,
    string Timestamp,
    string Provider,
    string? Session_id,
    string? Model,
    string? Machine_id,
    string? Assistant_kind,
    int? Input_tokens,
    int? Output_tokens,
    int? Cache_read_tokens,
    int? Cache_creation_tokens,
    double? Input_cost_usd,
    double? Output_cost_usd,
    double? Total_cost_usd,
    int? Num_turns,
    long? Duration_ms,
    string? Tool_name,
    int? Tool_input_size,
    int? Tool_output_size,
    bool? Was_error,
    string? Stop_reason,
    string? Cwd,
    string? Permission_mode,
    string? Source_event_id);
