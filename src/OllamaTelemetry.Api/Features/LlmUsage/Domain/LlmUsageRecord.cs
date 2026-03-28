namespace OllamaTelemetry.Api.Features.LlmUsage.Domain;

public sealed record LlmUsageRecord(
    string Provider,
    string RecordType,
    string SessionId,
    string? Model,
    DateTimeOffset Timestamp,
    string? MachineId,
    string? AssistantKind,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    double InputCostUsd,
    double OutputCostUsd,
    double TotalCostUsd,
    int NumTurns,
    long DurationMs,
    string? ToolName,
    int ToolInputSize,
    int ToolOutputSize,
    bool WasError,
    string? StopReason,
    string? Cwd,
    string? PermissionMode,
    string? SourceEventId);

public sealed record OllamaModelSnapshot(
    string MachineId,
    string DisplayName,
    string Endpoint,
    string ModelName,
    string Family,
    string ParameterSize,
    string QuantizationLevel,
    long SizeBytes,
    long SizeVramBytes,
    int ContextLength,
    DateTimeOffset CapturedAtUtc,
    bool IsLoaded);

public sealed record OllamaInferenceRecord(
    string MachineId,
    string DisplayName,
    string Endpoint,
    string ModelName,
    DateTimeOffset Timestamp,
    int PromptTokens,
    int CompletionTokens,
    long PromptEvalDurationNs,
    long EvalDurationNs,
    double TokensPerSecond,
    long TotalDurationNs);

public sealed record LlmUsageSummary(
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
