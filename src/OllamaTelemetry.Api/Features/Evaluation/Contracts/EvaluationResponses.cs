using OllamaTelemetry.Api.Features.LlmUsage.Contracts;

namespace OllamaTelemetry.Api.Features.Evaluation.Contracts;

public sealed record EvaluationRunSummaryResponse(
    string RunId,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    int CandidateCount,
    int CaseCount,
    int ResultCount);

public sealed record EvaluationRunDetailResponse(
    string RunId,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    string? Notes,
    IReadOnlyList<EvaluationCandidateResponse> Candidates,
    IReadOnlyList<EvaluationCaseResponse> Cases);

public sealed record EvaluationCandidateResponse(
    string CandidateId,
    string MachineId,
    string DisplayName,
    string Endpoint,
    string Provider,
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
    int ContextLength,
    bool IsLoaded);

public sealed record EvaluationCaseResponse(
    string CaseId,
    string CaseKey,
    string PromptLabel,
    string PromptText,
    string? ExpectedNotes,
    IReadOnlyList<EvaluationCaseResultResponse> Results);

public sealed record EvaluationCaseResultResponse(
    string ResultId,
    string CandidateId,
    string MachineId,
    string DisplayName,
    string Endpoint,
    string Provider,
    string MachineModelId,
    string CanonicalModelId,
    string DisplayLabel,
    string ModelName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    long TotalDurationMs,
    long PromptEvalDurationMs,
    long EvalDurationMs,
    string? ResponseText,
    bool WasError,
    string? ErrorText,
    string? ExecutedBy,
    string? JudgedBy,
    double? Score,
    string? Verdict,
    string? JudgmentNotes);

public sealed record CreateEvaluationRunRequest(
    string Title,
    string? CreatedBy,
    string? Notes,
    IReadOnlyList<CreateEvaluationCandidateRequest> Candidates);

public sealed record CreateEvaluationCandidateRequest(
    string MachineId,
    string ModelName);

public sealed record CreateEvaluationCaseRequest(
    string CaseKey,
    string PromptLabel,
    string PromptText,
    string? ExpectedNotes);

public sealed record ExecuteEvaluationCaseRequest(
    string? ExecutedBy,
    bool? OverwriteExisting);

public sealed record RecordEvaluationJudgmentRequest(
    string? JudgedBy,
    double? Score,
    string? Verdict,
    string? JudgmentNotes);

public sealed record EvaluationModelCatalogResponse(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<OllamaModelResponse> Models);
