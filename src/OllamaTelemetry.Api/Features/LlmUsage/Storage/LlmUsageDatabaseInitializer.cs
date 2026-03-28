using Microsoft.Data.Sqlite;
using OllamaTelemetry.Api.Features.Telemetry.Storage;

namespace OllamaTelemetry.Api.Features.LlmUsage.Storage;

public sealed class LlmUsageDatabaseInitializer(SqliteConnectionFactory connectionFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS llm_usage_records (
                record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                provider TEXT NOT NULL,
                record_type TEXT NOT NULL,
                session_id TEXT NOT NULL,
                model TEXT NULL,
                timestamp_unix_ms INTEGER NOT NULL,
                machine_id TEXT NULL,
                assistant_kind TEXT NULL,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                input_cost_usd REAL NOT NULL DEFAULT 0,
                output_cost_usd REAL NOT NULL DEFAULT 0,
                total_cost_usd REAL NOT NULL DEFAULT 0,
                num_turns INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                tool_name TEXT NULL,
                tool_input_size INTEGER NOT NULL DEFAULT 0,
                tool_output_size INTEGER NOT NULL DEFAULT 0,
                was_error INTEGER NOT NULL DEFAULT 0,
                stop_reason TEXT NULL,
                cwd TEXT NULL,
                permission_mode TEXT NULL,
                source_event_id TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_llm_usage_provider_time
                ON llm_usage_records(provider, timestamp_unix_ms DESC);

            CREATE INDEX IF NOT EXISTS idx_llm_usage_session
                ON llm_usage_records(session_id, timestamp_unix_ms DESC);

            CREATE INDEX IF NOT EXISTS idx_llm_usage_type
                ON llm_usage_records(record_type, timestamp_unix_ms DESC);

            CREATE TABLE IF NOT EXISTS ollama_model_snapshots (
                snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL DEFAULT 'ollama',
                display_name TEXT NOT NULL DEFAULT '',
                endpoint TEXT NOT NULL DEFAULT '',
                model_name TEXT NOT NULL,
                family TEXT NOT NULL,
                parameter_size TEXT NOT NULL,
                quantization_level TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                size_vram_bytes INTEGER NOT NULL DEFAULT 0,
                context_length INTEGER NOT NULL DEFAULT 0,
                captured_at_unix_ms INTEGER NOT NULL,
                is_loaded INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_ollama_snapshots_model_time
                ON ollama_model_snapshots(model_name, captured_at_unix_ms DESC);

            CREATE TABLE IF NOT EXISTS ollama_inference_records (
                inference_id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL DEFAULT 'ollama',
                display_name TEXT NOT NULL DEFAULT '',
                endpoint TEXT NOT NULL DEFAULT '',
                model_name TEXT NOT NULL,
                timestamp_unix_ms INTEGER NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                prompt_eval_duration_ns INTEGER NOT NULL DEFAULT 0,
                eval_duration_ns INTEGER NOT NULL DEFAULT 0,
                tokens_per_second REAL NOT NULL DEFAULT 0,
                total_duration_ns INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_ollama_inference_model_time
                ON ollama_inference_records(model_name, timestamp_unix_ms DESC);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "llm_usage_records", "machine_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "llm_usage_records", "assistant_kind", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "llm_usage_records", "source_event_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_model_snapshots", "machine_id", "TEXT NOT NULL DEFAULT 'ollama'", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_model_snapshots", "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_model_snapshots", "endpoint", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_inference_records", "machine_id", "TEXT NOT NULL DEFAULT 'ollama'", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_inference_records", "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "ollama_inference_records", "endpoint", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureIndexAsync(connection, "idx_llm_usage_machine_time",
            "CREATE INDEX IF NOT EXISTS idx_llm_usage_machine_time ON llm_usage_records(machine_id, timestamp_unix_ms DESC);",
            cancellationToken);
        await EnsureIndexAsync(connection, "idx_llm_usage_source_event",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_llm_usage_source_event ON llm_usage_records(source_event_id) WHERE source_event_id IS NOT NULL;",
            cancellationToken);
        await EnsureIndexAsync(connection, "idx_ollama_snapshots_machine_time",
            "CREATE INDEX IF NOT EXISTS idx_ollama_snapshots_machine_time ON ollama_model_snapshots(machine_id, captured_at_unix_ms DESC);",
            cancellationToken);
        await EnsureIndexAsync(connection, "idx_ollama_inference_machine_time",
            "CREATE INDEX IF NOT EXISTS idx_ollama_inference_machine_time ON ollama_inference_records(machine_id, timestamp_unix_ms DESC);",
            cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, tableName, columnName, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureIndexAsync(
        SqliteConnection connection,
        string indexName,
        string createSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index' AND name = $indexName;
            """;
        command.Parameters.AddWithValue("$indexName", indexName);

        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        if (exists)
        {
            return;
        }

        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = createSql;
        await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
