using Microsoft.Data.Sqlite;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Storage;

namespace OllamaTelemetry.Api.Features.LlmUsage.Storage;

public sealed class LlmUsageRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task InsertUsageRecordAsync(LlmUsageRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO llm_usage_records (
                provider, record_type, session_id, model,
                timestamp_unix_ms, machine_id, assistant_kind,
                input_tokens, output_tokens,
                cache_read_tokens, cache_creation_tokens,
                input_cost_usd, output_cost_usd, total_cost_usd,
                num_turns, duration_ms, tool_name,
                tool_input_size, tool_output_size, was_error,
                stop_reason, cwd, permission_mode, source_event_id
            ) VALUES (
                $provider, $recordType, $sessionId, $model,
                $timestampUnixMs, $machineId, $assistantKind,
                $inputTokens, $outputTokens,
                $cacheReadTokens, $cacheCreationTokens,
                $inputCostUsd, $outputCostUsd, $totalCostUsd,
                $numTurns, $durationMs, $toolName,
                $toolInputSize, $toolOutputSize, $wasError,
                $stopReason, $cwd, $permissionMode, $sourceEventId
            )
            ON CONFLICT DO UPDATE SET
                provider = excluded.provider,
                record_type = excluded.record_type,
                session_id = excluded.session_id,
                model = excluded.model,
                timestamp_unix_ms = excluded.timestamp_unix_ms,
                machine_id = excluded.machine_id,
                assistant_kind = excluded.assistant_kind,
                input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens,
                cache_read_tokens = excluded.cache_read_tokens,
                cache_creation_tokens = excluded.cache_creation_tokens,
                input_cost_usd = excluded.input_cost_usd,
                output_cost_usd = excluded.output_cost_usd,
                total_cost_usd = excluded.total_cost_usd,
                num_turns = excluded.num_turns,
                duration_ms = excluded.duration_ms,
                tool_name = excluded.tool_name,
                tool_input_size = excluded.tool_input_size,
                tool_output_size = excluded.tool_output_size,
                was_error = excluded.was_error,
                stop_reason = excluded.stop_reason,
                cwd = excluded.cwd,
                permission_mode = excluded.permission_mode;
            """;

        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$recordType", record.RecordType);
        command.Parameters.AddWithValue("$sessionId", record.SessionId);
        command.Parameters.AddWithValue("$model", (object?)record.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$timestampUnixMs", record.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$machineId", (object?)record.MachineId ?? DBNull.Value);
        command.Parameters.AddWithValue("$assistantKind", (object?)record.AssistantKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", record.InputTokens);
        command.Parameters.AddWithValue("$outputTokens", record.OutputTokens);
        command.Parameters.AddWithValue("$cacheReadTokens", record.CacheReadTokens);
        command.Parameters.AddWithValue("$cacheCreationTokens", record.CacheCreationTokens);
        command.Parameters.AddWithValue("$inputCostUsd", record.InputCostUsd);
        command.Parameters.AddWithValue("$outputCostUsd", record.OutputCostUsd);
        command.Parameters.AddWithValue("$totalCostUsd", record.TotalCostUsd);
        command.Parameters.AddWithValue("$numTurns", record.NumTurns);
        command.Parameters.AddWithValue("$durationMs", record.DurationMs);
        command.Parameters.AddWithValue("$toolName", (object?)record.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolInputSize", record.ToolInputSize);
        command.Parameters.AddWithValue("$toolOutputSize", record.ToolOutputSize);
        command.Parameters.AddWithValue("$wasError", record.WasError ? 1 : 0);
        command.Parameters.AddWithValue("$stopReason", (object?)record.StopReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$cwd", (object?)record.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue("$permissionMode", (object?)record.PermissionMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceEventId", (object?)record.SourceEventId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertOllamaSnapshotAsync(OllamaModelSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ollama_model_snapshots (
                machine_id, display_name, endpoint,
                model_name, family, parameter_size, quantization_level,
                size_bytes, size_vram_bytes, context_length,
                captured_at_unix_ms, is_loaded
            ) VALUES (
                $machineId, $displayName, $endpoint,
                $modelName, $family, $parameterSize, $quantizationLevel,
                $sizeBytes, $sizeVramBytes, $contextLength,
                $capturedAtUnixMs, $isLoaded
            );
            """;

        command.Parameters.AddWithValue("$machineId", snapshot.MachineId);
        command.Parameters.AddWithValue("$displayName", snapshot.DisplayName);
        command.Parameters.AddWithValue("$endpoint", snapshot.Endpoint);
        command.Parameters.AddWithValue("$modelName", snapshot.ModelName);
        command.Parameters.AddWithValue("$family", snapshot.Family);
        command.Parameters.AddWithValue("$parameterSize", snapshot.ParameterSize);
        command.Parameters.AddWithValue("$quantizationLevel", snapshot.QuantizationLevel);
        command.Parameters.AddWithValue("$sizeBytes", snapshot.SizeBytes);
        command.Parameters.AddWithValue("$sizeVramBytes", snapshot.SizeVramBytes);
        command.Parameters.AddWithValue("$contextLength", snapshot.ContextLength);
        command.Parameters.AddWithValue("$capturedAtUnixMs", snapshot.CapturedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$isLoaded", snapshot.IsLoaded ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertOllamaSnapshotBatchAsync(IReadOnlyList<OllamaModelSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ollama_model_snapshots (
                machine_id, display_name, endpoint,
                model_name, family, parameter_size, quantization_level,
                size_bytes, size_vram_bytes, context_length,
                captured_at_unix_ms, is_loaded
            ) VALUES (
                $machineId, $displayName, $endpoint,
                $modelName, $family, $parameterSize, $quantizationLevel,
                $sizeBytes, $sizeVramBytes, $contextLength,
                $capturedAtUnixMs, $isLoaded
            );
            """;

        var pMachineId = command.Parameters.Add("$machineId", SqliteType.Text);
        var pDisplayName = command.Parameters.Add("$displayName", SqliteType.Text);
        var pEndpoint = command.Parameters.Add("$endpoint", SqliteType.Text);
        var pModelName = command.Parameters.Add("$modelName", SqliteType.Text);
        var pFamily = command.Parameters.Add("$family", SqliteType.Text);
        var pParameterSize = command.Parameters.Add("$parameterSize", SqliteType.Text);
        var pQuantizationLevel = command.Parameters.Add("$quantizationLevel", SqliteType.Text);
        var pSizeBytes = command.Parameters.Add("$sizeBytes", SqliteType.Integer);
        var pSizeVramBytes = command.Parameters.Add("$sizeVramBytes", SqliteType.Integer);
        var pContextLength = command.Parameters.Add("$contextLength", SqliteType.Integer);
        var pCapturedAtUnixMs = command.Parameters.Add("$capturedAtUnixMs", SqliteType.Integer);
        var pIsLoaded = command.Parameters.Add("$isLoaded", SqliteType.Integer);

        foreach (var snapshot in snapshots)
        {
            pMachineId.Value = snapshot.MachineId;
            pDisplayName.Value = snapshot.DisplayName;
            pEndpoint.Value = snapshot.Endpoint;
            pModelName.Value = snapshot.ModelName;
            pFamily.Value = snapshot.Family;
            pParameterSize.Value = snapshot.ParameterSize;
            pQuantizationLevel.Value = snapshot.QuantizationLevel;
            pSizeBytes.Value = snapshot.SizeBytes;
            pSizeVramBytes.Value = snapshot.SizeVramBytes;
            pContextLength.Value = snapshot.ContextLength;
            pCapturedAtUnixMs.Value = snapshot.CapturedAtUtc.ToUnixTimeMilliseconds();
            pIsLoaded.Value = snapshot.IsLoaded ? 1 : 0;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertOllamaInferenceAsync(OllamaInferenceRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ollama_inference_records (
                machine_id, display_name, endpoint,
                model_name, timestamp_unix_ms, prompt_tokens, completion_tokens,
                prompt_eval_duration_ns, eval_duration_ns,
                tokens_per_second, total_duration_ns
            ) VALUES (
                $machineId, $displayName, $endpoint,
                $modelName, $timestampUnixMs, $promptTokens, $completionTokens,
                $promptEvalDurationNs, $evalDurationNs,
                $tokensPerSecond, $totalDurationNs
            );
            """;

        command.Parameters.AddWithValue("$machineId", record.MachineId);
        command.Parameters.AddWithValue("$displayName", record.DisplayName);
        command.Parameters.AddWithValue("$endpoint", record.Endpoint);
        command.Parameters.AddWithValue("$modelName", record.ModelName);
        command.Parameters.AddWithValue("$timestampUnixMs", record.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$promptTokens", record.PromptTokens);
        command.Parameters.AddWithValue("$completionTokens", record.CompletionTokens);
        command.Parameters.AddWithValue("$promptEvalDurationNs", record.PromptEvalDurationNs);
        command.Parameters.AddWithValue("$evalDurationNs", record.EvalDurationNs);
        command.Parameters.AddWithValue("$tokensPerSecond", record.TokensPerSecond);
        command.Parameters.AddWithValue("$totalDurationNs", record.TotalDurationNs);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LlmUsageSummary>> GetUsageSummaryAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                provider,
                assistant_kind,
                machine_id,
                model,
                COUNT(DISTINCT CASE WHEN record_type IN ('session_stop', 'session_end') THEN session_id END) as total_sessions,
                COUNT(CASE WHEN record_type = 'tool_use' THEN 1 END) as total_tool_calls,
                COALESCE(SUM(CASE WHEN record_type IN ('session_stop', 'session_end') THEN input_tokens ELSE 0 END), 0) as total_input_tokens,
                COALESCE(SUM(CASE WHEN record_type IN ('session_stop', 'session_end') THEN output_tokens ELSE 0 END), 0) as total_output_tokens,
                COALESCE(SUM(CASE WHEN record_type IN ('session_stop', 'session_end') THEN total_cost_usd ELSE 0 END), 0) as total_cost_usd,
                MIN(timestamp_unix_ms) as first_seen,
                MAX(timestamp_unix_ms) as last_seen
            FROM llm_usage_records
            WHERE timestamp_unix_ms >= $sinceUtc
            GROUP BY provider, assistant_kind, machine_id, model
            ORDER BY total_cost_usd DESC;
            """;
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToUnixTimeMilliseconds());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<LlmUsageSummary> summaries = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new LlmUsageSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10))));
        }

        return summaries;
    }

    public async Task<IReadOnlyList<LlmUsageRecord>> GetSessionRecordsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider, assistant_kind, machine_id, record_type, session_id, model,
                   timestamp_unix_ms, input_tokens, output_tokens,
                   cache_read_tokens, cache_creation_tokens,
                   input_cost_usd, output_cost_usd, total_cost_usd,
                   num_turns, duration_ms, tool_name,
                   tool_input_size, tool_output_size, was_error,
                   stop_reason, cwd, permission_mode, source_event_id
            FROM llm_usage_records
            WHERE session_id = $sessionId
            ORDER BY timestamp_unix_ms ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        return await ReadUsageRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<LlmUsageRecord>> GetRecentRecordsAsync(
        int limit,
        string? provider,
        string? recordType,
        string? machineId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var whereClause = "WHERE 1=1";
        if (provider is not null)
        {
            whereClause += " AND provider = $provider";
            command.Parameters.AddWithValue("$provider", provider);
        }
        if (recordType is not null)
        {
            whereClause += " AND record_type = $recordType";
            command.Parameters.AddWithValue("$recordType", recordType);
        }
        if (machineId is not null)
        {
            whereClause += " AND machine_id = $machineId";
            command.Parameters.AddWithValue("$machineId", machineId);
        }

        command.CommandText =
            $"""
            SELECT provider, assistant_kind, machine_id, record_type, session_id, model,
                   timestamp_unix_ms, input_tokens, output_tokens,
                   cache_read_tokens, cache_creation_tokens,
                   input_cost_usd, output_cost_usd, total_cost_usd,
                   num_turns, duration_ms, tool_name,
                   tool_input_size, tool_output_size, was_error,
                   stop_reason, cwd, permission_mode, source_event_id
            FROM llm_usage_records
            {whereClause}
            ORDER BY timestamp_unix_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadUsageRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<OllamaModelSnapshot>> GetLatestOllamaSnapshotsAsync(
        string? machineId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH latest_by_machine AS (
                SELECT
                    machine_id,
                    MAX(captured_at_unix_ms) AS latest_captured_at_unix_ms
                FROM ollama_model_snapshots
                WHERE $machineId IS NULL OR machine_id = $machineId
                GROUP BY machine_id
            )
            SELECT s.machine_id, s.display_name, s.endpoint,
                   s.model_name, s.family, s.parameter_size, s.quantization_level,
                   s.size_bytes, s.size_vram_bytes, s.context_length,
                   s.captured_at_unix_ms, s.is_loaded
            FROM ollama_model_snapshots s
            INNER JOIN latest_by_machine latest
                ON latest.machine_id = s.machine_id
               AND latest.latest_captured_at_unix_ms = s.captured_at_unix_ms
            WHERE $machineId IS NULL OR s.machine_id = $machineId
            ORDER BY s.machine_id ASC, s.model_name ASC;
            """;
        command.Parameters.AddWithValue("$machineId", (object?)machineId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<OllamaModelSnapshot> snapshots = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new OllamaModelSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt32(9),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                reader.GetInt32(11) == 1));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<OllamaModelSnapshot>> GetLatestLoadedModelsAsync(
        string machineId,
        CancellationToken cancellationToken)
    {
        var snapshots = await GetLatestOllamaSnapshotsAsync(machineId, cancellationToken);
        return snapshots
            .Where(static snapshot => snapshot.IsLoaded)
            .OrderBy(static snapshot => snapshot.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<OllamaInferenceRecord>> GetRecentOllamaInferenceAsync(
        DateTimeOffset sinceUtc,
        int limit,
        string? machineId,
        string? model,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var whereClause = "WHERE timestamp_unix_ms >= $sinceUtc";
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToUnixTimeMilliseconds());

        if (machineId is not null)
        {
            whereClause += " AND machine_id = $machineId";
            command.Parameters.AddWithValue("$machineId", machineId);
        }

        if (model is not null)
        {
            whereClause += " AND model_name = $model";
            command.Parameters.AddWithValue("$model", model);
        }

        command.CommandText =
            $"""
            SELECT machine_id, display_name, endpoint, model_name,
                   timestamp_unix_ms, prompt_tokens, completion_tokens,
                   prompt_eval_duration_ns, eval_duration_ns,
                   tokens_per_second, total_duration_ns
            FROM ollama_inference_records
            {whereClause}
            ORDER BY timestamp_unix_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<OllamaInferenceRecord> records = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new OllamaInferenceRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetDouble(9),
                reader.GetInt64(10)));
        }

        return records;
    }

    public async Task PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var cutoffMs = cutoffUtc.ToUnixTimeMilliseconds();

        await using var cmd1 = connection.CreateCommand();
        cmd1.Transaction = transaction;
        cmd1.CommandText = "DELETE FROM llm_usage_records WHERE timestamp_unix_ms < $cutoff;";
        cmd1.Parameters.AddWithValue("$cutoff", cutoffMs);
        await cmd1.ExecuteNonQueryAsync(cancellationToken);

        await using var cmd2 = connection.CreateCommand();
        cmd2.Transaction = transaction;
        cmd2.CommandText = "DELETE FROM ollama_model_snapshots WHERE captured_at_unix_ms < $cutoff;";
        cmd2.Parameters.AddWithValue("$cutoff", cutoffMs);
        await cmd2.ExecuteNonQueryAsync(cancellationToken);

        await using var cmd3 = connection.CreateCommand();
        cmd3.Transaction = transaction;
        cmd3.CommandText = "DELETE FROM ollama_inference_records WHERE timestamp_unix_ms < $cutoff;";
        cmd3.Parameters.AddWithValue("$cutoff", cutoffMs);
        await cmd3.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LlmUsageRecord>> ReadUsageRecordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<LlmUsageRecord> records = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new LlmUsageRecord(
                reader.GetString(0),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetDouble(11),
                reader.GetDouble(12),
                reader.GetDouble(13),
                reader.GetInt32(14),
                reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19) == 1,
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23)));
        }

        return records;
    }
}
