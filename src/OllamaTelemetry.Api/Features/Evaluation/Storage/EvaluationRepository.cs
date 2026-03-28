using Microsoft.Data.Sqlite;
using OllamaTelemetry.Api.Features.Evaluation.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Storage;

namespace OllamaTelemetry.Api.Features.Evaluation.Storage;

public sealed class EvaluationRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task InsertRunAsync(
        EvaluationRunRecord run,
        IReadOnlyList<EvaluationCandidateRecord> candidates,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO evaluation_runs (
                    run_id, title, status, created_at_unix_ms, created_by, notes
                ) VALUES (
                    $runId, $title, $status, $createdAtUnixMs, $createdBy, $notes
                );
                """;
            command.Parameters.AddWithValue("$runId", run.RunId);
            command.Parameters.AddWithValue("$title", run.Title);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue("$createdAtUnixMs", run.CreatedAtUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$createdBy", (object?)run.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)run.Notes ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO evaluation_run_candidates (
                    candidate_id, run_id, sort_order, machine_id, display_name, endpoint,
                    provider, machine_model_id, canonical_model_id, display_label, short_label,
                    model_name, family, family_slug, model_tag, parameter_size,
                    quantization_level, context_length, is_loaded
                ) VALUES (
                    $candidateId, $runId, $sortOrder, $machineId, $displayName, $endpoint,
                    $provider, $machineModelId, $canonicalModelId, $displayLabel, $shortLabel,
                    $modelName, $family, $familySlug, $modelTag, $parameterSize,
                    $quantizationLevel, $contextLength, $isLoaded
                );
                """;
            BindCandidateParameters(command, candidate);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertCaseAsync(EvaluationCaseRecord testCase, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO evaluation_run_cases (
                case_id, run_id, sort_order, case_key, prompt_label, prompt_text, expected_notes, created_at_unix_ms
            ) VALUES (
                $caseId, $runId, $sortOrder, $caseKey, $promptLabel, $promptText, $expectedNotes, $createdAtUnixMs
            );
            """;
        command.Parameters.AddWithValue("$caseId", testCase.CaseId);
        command.Parameters.AddWithValue("$runId", testCase.RunId);
        command.Parameters.AddWithValue("$sortOrder", testCase.SortOrder);
        command.Parameters.AddWithValue("$caseKey", testCase.CaseKey);
        command.Parameters.AddWithValue("$promptLabel", testCase.PromptLabel);
        command.Parameters.AddWithValue("$promptText", testCase.PromptText);
        command.Parameters.AddWithValue("$expectedNotes", (object?)testCase.ExpectedNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUnixMs", testCase.CreatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EvaluationRunRecord>> GetRunsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, title, status, created_at_unix_ms, created_by, notes
            FROM evaluation_runs
            ORDER BY created_at_unix_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadRunsAsync(command, cancellationToken);
    }

    public async Task<EvaluationRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, title, status, created_at_unix_ms, created_by, notes
            FROM evaluation_runs
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        return (await ReadRunsAsync(command, cancellationToken)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<EvaluationCandidateRecord>> GetCandidatesAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_id, run_id, sort_order, machine_id, display_name, endpoint,
                   provider, machine_model_id, canonical_model_id, display_label, short_label,
                   model_name, family, family_slug, model_tag, parameter_size,
                   quantization_level, context_length, is_loaded
            FROM evaluation_run_candidates
            WHERE run_id = $runId
            ORDER BY sort_order ASC, candidate_id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<EvaluationCandidateRecord> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EvaluationCandidateRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetInt32(17),
                reader.GetInt32(18) == 1));
        }

        return rows;
    }

    public async Task<IReadOnlyList<EvaluationCaseRecord>> GetCasesAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT case_id, run_id, sort_order, case_key, prompt_label, prompt_text, expected_notes, created_at_unix_ms
            FROM evaluation_run_cases
            WHERE run_id = $runId
            ORDER BY sort_order ASC, case_id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<EvaluationCaseRecord> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EvaluationCaseRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));
        }

        return rows;
    }

    public async Task<IReadOnlyList<EvaluationCaseResultRecord>> GetResultsAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT result_id, run_id, case_id, candidate_id, machine_id, display_name, endpoint,
                   provider, machine_model_id, canonical_model_id, display_label, model_name,
                   started_at_unix_ms, completed_at_unix_ms, prompt_tokens, completion_tokens,
                   tokens_per_second, total_duration_ms, prompt_eval_duration_ms, eval_duration_ms,
                   response_text, was_error, error_text, executed_by, judged_by, score, verdict, judgment_notes
            FROM evaluation_case_results
            WHERE run_id = $runId
            ORDER BY completed_at_unix_ms ASC, candidate_id ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<EvaluationCaseResultRecord> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EvaluationCaseResultRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetDouble(16),
                reader.GetInt64(17),
                reader.GetInt64(18),
                reader.GetInt64(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetInt32(21) == 1,
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetDouble(25),
                reader.IsDBNull(26) ? null : reader.GetString(26),
                reader.IsDBNull(27) ? null : reader.GetString(27)));
        }

        return rows;
    }

    public async Task UpsertResultAsync(EvaluationCaseResultRecord result, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO evaluation_case_results (
                result_id, run_id, case_id, candidate_id, machine_id, display_name, endpoint,
                provider, machine_model_id, canonical_model_id, display_label, model_name,
                started_at_unix_ms, completed_at_unix_ms, prompt_tokens, completion_tokens,
                tokens_per_second, total_duration_ms, prompt_eval_duration_ms, eval_duration_ms,
                response_text, was_error, error_text, executed_by, judged_by, score, verdict,
                judgment_notes, updated_at_unix_ms
            ) VALUES (
                $resultId, $runId, $caseId, $candidateId, $machineId, $displayName, $endpoint,
                $provider, $machineModelId, $canonicalModelId, $displayLabel, $modelName,
                $startedAtUnixMs, $completedAtUnixMs, $promptTokens, $completionTokens,
                $tokensPerSecond, $totalDurationMs, $promptEvalDurationMs, $evalDurationMs,
                $responseText, $wasError, $errorText, $executedBy, $judgedBy, $score, $verdict,
                $judgmentNotes, $updatedAtUnixMs
            )
            ON CONFLICT(run_id, case_id, candidate_id) DO UPDATE SET
                result_id = excluded.result_id,
                machine_id = excluded.machine_id,
                display_name = excluded.display_name,
                endpoint = excluded.endpoint,
                provider = excluded.provider,
                machine_model_id = excluded.machine_model_id,
                canonical_model_id = excluded.canonical_model_id,
                display_label = excluded.display_label,
                model_name = excluded.model_name,
                started_at_unix_ms = excluded.started_at_unix_ms,
                completed_at_unix_ms = excluded.completed_at_unix_ms,
                prompt_tokens = excluded.prompt_tokens,
                completion_tokens = excluded.completion_tokens,
                tokens_per_second = excluded.tokens_per_second,
                total_duration_ms = excluded.total_duration_ms,
                prompt_eval_duration_ms = excluded.prompt_eval_duration_ms,
                eval_duration_ms = excluded.eval_duration_ms,
                response_text = excluded.response_text,
                was_error = excluded.was_error,
                error_text = excluded.error_text,
                executed_by = excluded.executed_by,
                judged_by = excluded.judged_by,
                score = excluded.score,
                verdict = excluded.verdict,
                judgment_notes = excluded.judgment_notes,
                updated_at_unix_ms = excluded.updated_at_unix_ms;
            """;
        BindResultParameters(command, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateJudgmentAsync(
        string runId,
        string caseId,
        string candidateId,
        string? judgedBy,
        double? score,
        string? verdict,
        string? judgmentNotes,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE evaluation_case_results
            SET judged_by = $judgedBy,
                score = $score,
                verdict = $verdict,
                judgment_notes = $judgmentNotes,
                updated_at_unix_ms = $updatedAtUnixMs
            WHERE run_id = $runId
              AND case_id = $caseId
              AND candidate_id = $candidateId;
            """;
        command.Parameters.AddWithValue("$judgedBy", (object?)judgedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("$verdict", (object?)verdict ?? DBNull.Value);
        command.Parameters.AddWithValue("$judgmentNotes", (object?)judgmentNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUnixMs", updatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$caseId", caseId);
        command.Parameters.AddWithValue("$candidateId", candidateId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<EvaluationRunRecord>> ReadRunsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<EvaluationRunRecord> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EvaluationRunRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static void BindCandidateParameters(SqliteCommand command, EvaluationCandidateRecord candidate)
    {
        command.Parameters.AddWithValue("$candidateId", candidate.CandidateId);
        command.Parameters.AddWithValue("$runId", candidate.RunId);
        command.Parameters.AddWithValue("$sortOrder", candidate.SortOrder);
        command.Parameters.AddWithValue("$machineId", candidate.MachineId);
        command.Parameters.AddWithValue("$displayName", candidate.DisplayName);
        command.Parameters.AddWithValue("$endpoint", candidate.Endpoint);
        command.Parameters.AddWithValue("$provider", candidate.Provider);
        command.Parameters.AddWithValue("$machineModelId", candidate.MachineModelId);
        command.Parameters.AddWithValue("$canonicalModelId", candidate.CanonicalModelId);
        command.Parameters.AddWithValue("$displayLabel", candidate.DisplayLabel);
        command.Parameters.AddWithValue("$shortLabel", candidate.ShortLabel);
        command.Parameters.AddWithValue("$modelName", candidate.ModelName);
        command.Parameters.AddWithValue("$family", candidate.Family);
        command.Parameters.AddWithValue("$familySlug", candidate.FamilySlug);
        command.Parameters.AddWithValue("$modelTag", (object?)candidate.Tag ?? DBNull.Value);
        command.Parameters.AddWithValue("$parameterSize", candidate.ParameterSize);
        command.Parameters.AddWithValue("$quantizationLevel", candidate.QuantizationLevel);
        command.Parameters.AddWithValue("$contextLength", candidate.ContextLength);
        command.Parameters.AddWithValue("$isLoaded", candidate.IsLoaded ? 1 : 0);
    }

    private static void BindResultParameters(SqliteCommand command, EvaluationCaseResultRecord result)
    {
        command.Parameters.AddWithValue("$resultId", result.ResultId);
        command.Parameters.AddWithValue("$runId", result.RunId);
        command.Parameters.AddWithValue("$caseId", result.CaseId);
        command.Parameters.AddWithValue("$candidateId", result.CandidateId);
        command.Parameters.AddWithValue("$machineId", result.MachineId);
        command.Parameters.AddWithValue("$displayName", result.DisplayName);
        command.Parameters.AddWithValue("$endpoint", result.Endpoint);
        command.Parameters.AddWithValue("$provider", result.Provider);
        command.Parameters.AddWithValue("$machineModelId", result.MachineModelId);
        command.Parameters.AddWithValue("$canonicalModelId", result.CanonicalModelId);
        command.Parameters.AddWithValue("$displayLabel", result.DisplayLabel);
        command.Parameters.AddWithValue("$modelName", result.ModelName);
        command.Parameters.AddWithValue("$startedAtUnixMs", result.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$completedAtUnixMs", result.CompletedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$promptTokens", result.PromptTokens);
        command.Parameters.AddWithValue("$completionTokens", result.CompletionTokens);
        command.Parameters.AddWithValue("$tokensPerSecond", result.TokensPerSecond);
        command.Parameters.AddWithValue("$totalDurationMs", result.TotalDurationMs);
        command.Parameters.AddWithValue("$promptEvalDurationMs", result.PromptEvalDurationMs);
        command.Parameters.AddWithValue("$evalDurationMs", result.EvalDurationMs);
        command.Parameters.AddWithValue("$responseText", (object?)result.ResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$wasError", result.WasError ? 1 : 0);
        command.Parameters.AddWithValue("$errorText", (object?)result.ErrorText ?? DBNull.Value);
        command.Parameters.AddWithValue("$executedBy", (object?)result.ExecutedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$judgedBy", (object?)result.JudgedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$score", (object?)result.Score ?? DBNull.Value);
        command.Parameters.AddWithValue("$verdict", (object?)result.Verdict ?? DBNull.Value);
        command.Parameters.AddWithValue("$judgmentNotes", (object?)result.JudgmentNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUnixMs", result.CompletedAtUtc.ToUnixTimeMilliseconds());
    }
}
