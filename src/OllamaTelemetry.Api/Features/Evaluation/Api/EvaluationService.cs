using OllamaTelemetry.Api.Features.Evaluation.Contracts;
using OllamaTelemetry.Api.Features.Evaluation.Domain;
using OllamaTelemetry.Api.Features.Evaluation.Storage;
using OllamaTelemetry.Api.Features.LlmUsage.Api;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;

namespace OllamaTelemetry.Api.Features.Evaluation.Api;

public sealed class EvaluationService(
    EvaluationRepository repository,
    LlmUsageRepository llmUsageRepository,
    LlmUsageQueryService llmUsageQueryService,
    OllamaExecutionClient executionClient,
    TimeProvider timeProvider)
{
    public async Task<EvaluationModelCatalogResponse> GetModelCatalogAsync(
        string? machineId,
        bool loadedOnly,
        CancellationToken cancellationToken)
    {
        var models = await llmUsageQueryService.GetOllamaModelsAsync(machineId, loadedOnly, cancellationToken);
        return new EvaluationModelCatalogResponse(timeProvider.GetUtcNow(), models);
    }

    public async Task<EvaluationRunDetailResponse> CreateRunAsync(
        CreateEvaluationRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0)
        {
            throw new InvalidOperationException("Evaluation runs require at least one candidate.");
        }

        var createdAtUtc = timeProvider.GetUtcNow();
        var runId = BuildRunId(request.Title, createdAtUtc);
        var run = new EvaluationRunRecord(
            runId,
            request.Title.Trim(),
            "open",
            createdAtUtc,
            request.CreatedBy,
            request.Notes);

        var snapshots = await llmUsageRepository.GetLatestOllamaSnapshotsAsync(null, cancellationToken);
        var candidates = request.Candidates.Select((candidate, index) =>
        {
            var snapshot = snapshots.FirstOrDefault(snapshot =>
                string.Equals(snapshot.MachineId, candidate.MachineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.ModelName, candidate.ModelName, StringComparison.OrdinalIgnoreCase));

            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    $"Candidate '{candidate.MachineId}/{candidate.ModelName}' is not present in the latest Ollama catalog.");
            }

            var identity = OllamaModelIdentityParser.Parse(snapshot);
            return new EvaluationCandidateRecord(
                BuildCandidateId(index + 1, snapshot.MachineId, snapshot.ModelName),
                runId,
                index,
                snapshot.MachineId,
                snapshot.DisplayName,
                snapshot.Endpoint,
                identity.Provider,
                identity.MachineModelId,
                identity.CanonicalModelId,
                identity.DisplayLabel,
                identity.ShortLabel,
                snapshot.ModelName,
                identity.Family,
                identity.FamilySlug,
                identity.Tag,
                identity.ParameterSize,
                identity.QuantizationLevel,
                snapshot.ContextLength,
                snapshot.IsLoaded);
        }).ToArray();

        await repository.InsertRunAsync(run, candidates, cancellationToken);
        return await GetRunDetailAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("Created evaluation run could not be reloaded.");
    }

    public async Task<IReadOnlyList<EvaluationRunSummaryResponse>> GetRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var runs = await repository.GetRunsAsync(limit, cancellationToken);
        List<EvaluationRunSummaryResponse> summaries = [];

        foreach (var run in runs)
        {
            var candidates = await repository.GetCandidatesAsync(run.RunId, cancellationToken);
            var cases = await repository.GetCasesAsync(run.RunId, cancellationToken);
            var results = await repository.GetResultsAsync(run.RunId, cancellationToken);
            summaries.Add(new EvaluationRunSummaryResponse(
                run.RunId,
                run.Title,
                run.Status,
                run.CreatedAtUtc,
                run.CreatedBy,
                candidates.Count,
                cases.Count,
                results.Count));
        }

        return summaries;
    }

    public async Task<EvaluationRunDetailResponse?> GetRunDetailAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await repository.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var candidates = await repository.GetCandidatesAsync(runId, cancellationToken);
        var cases = await repository.GetCasesAsync(runId, cancellationToken);
        var results = await repository.GetResultsAsync(runId, cancellationToken);

        return new EvaluationRunDetailResponse(
            run.RunId,
            run.Title,
            run.Status,
            run.CreatedAtUtc,
            run.CreatedBy,
            run.Notes,
            candidates.Select(static candidate => new EvaluationCandidateResponse(
                candidate.CandidateId,
                candidate.MachineId,
                candidate.DisplayName,
                candidate.Endpoint,
                candidate.Provider,
                candidate.MachineModelId,
                candidate.CanonicalModelId,
                candidate.DisplayLabel,
                candidate.ShortLabel,
                candidate.ModelName,
                candidate.Family,
                candidate.FamilySlug,
                candidate.Tag,
                candidate.ParameterSize,
                candidate.QuantizationLevel,
                candidate.ContextLength,
                candidate.IsLoaded)).ToArray(),
            cases.Select(testCase => new EvaluationCaseResponse(
                testCase.CaseId,
                testCase.CaseKey,
                testCase.PromptLabel,
                testCase.PromptText,
                testCase.ExpectedNotes,
                results
                    .Where(result => string.Equals(result.CaseId, testCase.CaseId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(result => candidates.First(candidate => candidate.CandidateId == result.CandidateId).SortOrder)
                    .Select(static result => new EvaluationCaseResultResponse(
                        result.ResultId,
                        result.CandidateId,
                        result.MachineId,
                        result.DisplayName,
                        result.Endpoint,
                        result.Provider,
                        result.MachineModelId,
                        result.CanonicalModelId,
                        result.DisplayLabel,
                        result.ModelName,
                        result.StartedAtUtc,
                        result.CompletedAtUtc,
                        result.PromptTokens,
                        result.CompletionTokens,
                        result.TokensPerSecond,
                        result.TotalDurationMs,
                        result.PromptEvalDurationMs,
                        result.EvalDurationMs,
                        result.ResponseText,
                        result.WasError,
                        result.ErrorText,
                        result.ExecutedBy,
                        result.JudgedBy,
                        result.Score,
                        result.Verdict,
                        result.JudgmentNotes)).ToArray())).ToArray());
    }

    public async Task<EvaluationCaseResponse?> AddCaseAsync(
        string runId,
        CreateEvaluationCaseRequest request,
        CancellationToken cancellationToken)
    {
        if (await repository.GetRunAsync(runId, cancellationToken) is null)
        {
            return null;
        }

        var existingCases = await repository.GetCasesAsync(runId, cancellationToken);
        var caseKey = string.IsNullOrWhiteSpace(request.CaseKey)
            ? $"case-{existingCases.Count + 1}"
            : request.CaseKey.Trim();

        var testCase = new EvaluationCaseRecord(
            BuildCaseId(caseKey),
            runId,
            existingCases.Count,
            caseKey,
            request.PromptLabel.Trim(),
            request.PromptText,
            request.ExpectedNotes,
            timeProvider.GetUtcNow());

        await repository.InsertCaseAsync(testCase, cancellationToken);
        var detail = await GetRunDetailAsync(runId, cancellationToken);
        return detail?.Cases.SingleOrDefault(caseItem =>
            string.Equals(caseItem.CaseId, testCase.CaseId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EvaluationCaseResponse?> ExecuteCaseAsync(
        string runId,
        string caseId,
        ExecuteEvaluationCaseRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await GetRunDetailAsync(runId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var testCase = detail.Cases.SingleOrDefault(caseItem =>
            string.Equals(caseItem.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
        if (testCase is null)
        {
            return null;
        }

        var existingResults = testCase.Results.ToDictionary(static result => result.CandidateId, StringComparer.OrdinalIgnoreCase);
        var overwrite = request.OverwriteExisting ?? false;

        foreach (var candidate in detail.Candidates)
        {
            if (!overwrite && existingResults.ContainsKey(candidate.CandidateId))
            {
                continue;
            }

            var startedAtUtc = timeProvider.GetUtcNow();

            try
            {
                var execution = await executionClient.GenerateAsync(
                    candidate.MachineId,
                    candidate.DisplayName,
                    candidate.Endpoint,
                    candidate.ModelName,
                    testCase.PromptText,
                    cancellationToken);

                var completedAtUtc = timeProvider.GetUtcNow();
                var prior = existingResults.TryGetValue(candidate.CandidateId, out var priorResult) ? priorResult : null;
                await repository.UpsertResultAsync(new EvaluationCaseResultRecord(
                    BuildResultId(runId, caseId, candidate.CandidateId),
                    runId,
                    caseId,
                    candidate.CandidateId,
                    candidate.MachineId,
                    candidate.DisplayName,
                    candidate.Endpoint,
                    candidate.Provider,
                    candidate.MachineModelId,
                    candidate.CanonicalModelId,
                    candidate.DisplayLabel,
                    candidate.ModelName,
                    startedAtUtc,
                    completedAtUtc,
                    execution.PromptTokens,
                    execution.CompletionTokens,
                    execution.TokensPerSecond,
                    execution.TotalDurationNs / 1_000_000,
                    execution.PromptEvalDurationNs / 1_000_000,
                    execution.EvalDurationNs / 1_000_000,
                    execution.Response,
                    false,
                    null,
                    request.ExecutedBy,
                    prior?.JudgedBy,
                    prior?.Score,
                    prior?.Verdict,
                    prior?.JudgmentNotes), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                var completedAtUtc = timeProvider.GetUtcNow();
                var prior = existingResults.TryGetValue(candidate.CandidateId, out var priorResult) ? priorResult : null;
                await repository.UpsertResultAsync(new EvaluationCaseResultRecord(
                    BuildResultId(runId, caseId, candidate.CandidateId),
                    runId,
                    caseId,
                    candidate.CandidateId,
                    candidate.MachineId,
                    candidate.DisplayName,
                    candidate.Endpoint,
                    candidate.Provider,
                    candidate.MachineModelId,
                    candidate.CanonicalModelId,
                    candidate.DisplayLabel,
                    candidate.ModelName,
                    startedAtUtc,
                    completedAtUtc,
                    0,
                    0,
                    0,
                    (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
                    0,
                    0,
                    null,
                    true,
                    ex.Message,
                    request.ExecutedBy,
                    prior?.JudgedBy,
                    prior?.Score,
                    prior?.Verdict,
                    prior?.JudgmentNotes), cancellationToken);
            }
        }

        var refreshed = await GetRunDetailAsync(runId, cancellationToken);
        return refreshed?.Cases.SingleOrDefault(caseItem =>
            string.Equals(caseItem.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EvaluationCaseResultResponse?> RecordJudgmentAsync(
        string runId,
        string caseId,
        string candidateId,
        RecordEvaluationJudgmentRequest request,
        CancellationToken cancellationToken)
    {
        await repository.UpdateJudgmentAsync(
            runId,
            caseId,
            candidateId,
            request.JudgedBy,
            request.Score,
            request.Verdict,
            request.JudgmentNotes,
            timeProvider.GetUtcNow(),
            cancellationToken);

        var detail = await GetRunDetailAsync(runId, cancellationToken);
        return detail?.Cases
            .Where(caseItem => string.Equals(caseItem.CaseId, caseId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static caseItem => caseItem.Results)
            .SingleOrDefault(result => string.Equals(result.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRunId(string title, DateTimeOffset createdAtUtc)
        => $"{createdAtUtc:yyyyMMdd-HHmmss}-{ToSlug(title)}";

    private static string BuildCandidateId(int order, string machineId, string modelName)
        => $"cand-{order:D2}-{ToSlug(machineId)}-{ToSlug(modelName)}";

    private static string BuildCaseId(string caseKey)
        => $"case-{ToSlug(caseKey)}";

    private static string BuildResultId(string runId, string caseId, string candidateId)
        => $"res-{ToSlug(runId)}-{ToSlug(caseId)}-{ToSlug(candidateId)}";

    private static string ToSlug(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && length > 0)
                {
                    buffer[length++] = '-';
                }

                buffer[length++] = char.ToLowerInvariant(character);
                pendingSeparator = false;
                continue;
            }

            pendingSeparator = true;
        }

        return length == 0 ? "item" : new string(buffer[..length]);
    }
}
