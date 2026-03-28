using OllamaTelemetry.Api.Features.Telemetry.Storage;

namespace OllamaTelemetry.Api.Features.Evaluation.Storage;

public sealed class EvaluationDatabaseInitializer(SqliteConnectionFactory connectionFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS evaluation_runs (
                run_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at_unix_ms INTEGER NOT NULL,
                created_by TEXT NULL,
                notes TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS evaluation_run_candidates (
                candidate_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                machine_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                endpoint TEXT NOT NULL,
                provider TEXT NOT NULL,
                machine_model_id TEXT NOT NULL,
                canonical_model_id TEXT NOT NULL,
                display_label TEXT NOT NULL,
                short_label TEXT NOT NULL,
                model_name TEXT NOT NULL,
                family TEXT NOT NULL,
                family_slug TEXT NOT NULL,
                model_tag TEXT NULL,
                parameter_size TEXT NOT NULL,
                quantization_level TEXT NOT NULL,
                context_length INTEGER NOT NULL DEFAULT 0,
                is_loaded INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (run_id) REFERENCES evaluation_runs(run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_eval_candidates_run_order
                ON evaluation_run_candidates(run_id, sort_order);

            CREATE TABLE IF NOT EXISTS evaluation_run_cases (
                case_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                case_key TEXT NOT NULL,
                prompt_label TEXT NOT NULL,
                prompt_text TEXT NOT NULL,
                expected_notes TEXT NULL,
                created_at_unix_ms INTEGER NOT NULL,
                FOREIGN KEY (run_id) REFERENCES evaluation_runs(run_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_eval_cases_run_key
                ON evaluation_run_cases(run_id, case_key);

            CREATE TABLE IF NOT EXISTS evaluation_case_results (
                result_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                case_id TEXT NOT NULL,
                candidate_id TEXT NOT NULL,
                machine_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                endpoint TEXT NOT NULL,
                provider TEXT NOT NULL,
                machine_model_id TEXT NOT NULL,
                canonical_model_id TEXT NOT NULL,
                display_label TEXT NOT NULL,
                model_name TEXT NOT NULL,
                started_at_unix_ms INTEGER NOT NULL,
                completed_at_unix_ms INTEGER NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                tokens_per_second REAL NOT NULL DEFAULT 0,
                total_duration_ms INTEGER NOT NULL DEFAULT 0,
                prompt_eval_duration_ms INTEGER NOT NULL DEFAULT 0,
                eval_duration_ms INTEGER NOT NULL DEFAULT 0,
                response_text TEXT NULL,
                was_error INTEGER NOT NULL DEFAULT 0,
                error_text TEXT NULL,
                executed_by TEXT NULL,
                judged_by TEXT NULL,
                score REAL NULL,
                verdict TEXT NULL,
                judgment_notes TEXT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                FOREIGN KEY (run_id) REFERENCES evaluation_runs(run_id) ON DELETE CASCADE,
                FOREIGN KEY (case_id) REFERENCES evaluation_run_cases(case_id) ON DELETE CASCADE,
                FOREIGN KEY (candidate_id) REFERENCES evaluation_run_candidates(candidate_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_eval_results_case_candidate
                ON evaluation_case_results(run_id, case_id, candidate_id);

            CREATE INDEX IF NOT EXISTS idx_eval_results_run_case
                ON evaluation_case_results(run_id, case_id);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
