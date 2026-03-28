using System.Text.Json;
using Microsoft.Data.Sqlite;
using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Features.Telemetry.Storage;

public sealed class TelemetryRepository(SqliteConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task RecordSuccessfulPollAsync(
        MachineCapacitySnapshot snapshot,
        IReadOnlyList<ThermalSensorSample> persistedSensors,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var capturedAtUnixMs = snapshot.CapturedAtUtc.ToUnixTimeMilliseconds();

        var pollRunId = await InsertPollRunAsync(
            connection,
            transaction,
            snapshot.MachineId,
            snapshot.SourceType,
            snapshot.Endpoint.ToString(),
            capturedAtUnixMs,
            MachinePollStatus.Success,
            snapshot.LatencyMs,
            snapshot.ThermalSensors.Count,
            persistedSensors.Count,
            null,
            cancellationToken);

        if (persistedSensors.Count > 0)
        {
            await InsertSensorReadingsBatchAsync(connection, transaction, pollRunId, snapshot.MachineId, capturedAtUnixMs, persistedSensors, cancellationToken);
        }

        await InsertCapacitySnapshotAsync(connection, transaction, snapshot, capturedAtUnixMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordFailedPollAsync(
        MachineTelemetryTarget target,
        DateTimeOffset occurredAtUtc,
        int latencyMs,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await InsertPollRunAsync(
            connection,
            transaction,
            target.MachineId,
            target.SourceType,
            target.Endpoint.ToString(),
            occurredAtUtc.ToUnixTimeMilliseconds(),
            MachinePollStatus.Failure,
            latencyMs,
            0,
            0,
            errorMessage,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MachineCapacitySnapshot?> GetLatestSuccessfulSnapshotAsync(
        MachineTelemetryTarget target,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT poll_run_id, endpoint, captured_at_unix_ms, latency_ms
            FROM machine_poll_runs
            WHERE machine_id = $machineId AND poll_status = $status
            ORDER BY captured_at_unix_ms DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$machineId", target.MachineId);
        command.Parameters.AddWithValue("$status", MachinePollStatus.Success.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var pollRunId = reader.GetInt64(0);
        var endpoint = new Uri(reader.GetString(1), UriKind.Absolute);
        var capturedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var latencyMs = reader.GetInt32(3);
        var sensors = await GetSensorReadingsForPollRunAsync(connection, pollRunId, cancellationToken);
        var capacity = await GetCapacityStateAsync(connection, target.MachineId, capturedAtUtc.ToUnixTimeMilliseconds(), cancellationToken);

        return new MachineCapacitySnapshot(
            target.MachineId,
            target.DisplayName,
            target.SourceType,
            endpoint,
            capturedAtUtc,
            latencyMs,
            capacity.Gpus,
            capacity.Cpu,
            capacity.Memory,
            sensors,
            capacity.LoadedModels);
    }

    public async Task<IReadOnlyList<ThermalHistoryPoint>> GetSensorHistoryAsync(
        string machineId,
        string sensorKey,
        DateTimeOffset sinceUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT captured_at_unix_ms, temperature_c, min_temperature_c, max_temperature_c
            FROM (
                SELECT captured_at_unix_ms, temperature_c, min_temperature_c, max_temperature_c
                FROM thermal_sensor_readings
                WHERE machine_id = $machineId
                    AND sensor_key = $sensorKey
                    AND captured_at_unix_ms >= $sinceUtc
                ORDER BY captured_at_unix_ms DESC
                LIMIT $limit
            )
            ORDER BY captured_at_unix_ms ASC;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$sensorKey", sensorKey);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<ThermalHistoryPoint> points = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new ThermalHistoryPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3)));
        }

        return points;
    }

    public async Task PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM machine_poll_runs
            WHERE captured_at_unix_ms < $cutoffUtc;
            """;
        command.Parameters.AddWithValue("$cutoffUtc", cutoffUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertPollRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string machineId,
        string sourceType,
        string endpoint,
        long capturedAtUnixMs,
        MachinePollStatus pollStatus,
        int latencyMs,
        int sensorCount,
        int persistedSensorCount,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO machine_poll_runs (
                machine_id,
                source_type,
                endpoint,
                captured_at_unix_ms,
                poll_status,
                latency_ms,
                sensor_count,
                persisted_sensor_count,
                error_message
            )
            VALUES (
                $machineId,
                $sourceType,
                $endpoint,
                $capturedAtUnixMs,
                $pollStatus,
                $latencyMs,
                $sensorCount,
                $persistedSensorCount,
                $errorMessage
            );

            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$sourceType", sourceType);
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$capturedAtUnixMs", capturedAtUnixMs);
        command.Parameters.AddWithValue("$pollStatus", pollStatus.ToString());
        command.Parameters.AddWithValue("$latencyMs", latencyMs);
        command.Parameters.AddWithValue("$sensorCount", sensorCount);
        command.Parameters.AddWithValue("$persistedSensorCount", persistedSensorCount);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task InsertSensorReadingsBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long pollRunId,
        string machineId,
        long capturedAtUnixMs,
        IReadOnlyList<ThermalSensorSample> sensors,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO thermal_sensor_readings (
                poll_run_id,
                machine_id,
                captured_at_unix_ms,
                sensor_key,
                sensor_name,
                sensor_path,
                temperature_c,
                min_temperature_c,
                max_temperature_c
            )
            VALUES (
                $pollRunId,
                $machineId,
                $capturedAtUnixMs,
                $sensorKey,
                $sensorName,
                $sensorPath,
                $temperatureC,
                $minTemperatureC,
                $maxTemperatureC
            );
            """;

        var pPollRunId = command.Parameters.Add("$pollRunId", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pMachineId = command.Parameters.Add("$machineId", Microsoft.Data.Sqlite.SqliteType.Text);
        var pCapturedAt = command.Parameters.Add("$capturedAtUnixMs", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pSensorKey = command.Parameters.Add("$sensorKey", Microsoft.Data.Sqlite.SqliteType.Text);
        var pSensorName = command.Parameters.Add("$sensorName", Microsoft.Data.Sqlite.SqliteType.Text);
        var pSensorPath = command.Parameters.Add("$sensorPath", Microsoft.Data.Sqlite.SqliteType.Text);
        var pTempC = command.Parameters.Add("$temperatureC", Microsoft.Data.Sqlite.SqliteType.Real);
        var pMinTempC = command.Parameters.Add("$minTemperatureC", Microsoft.Data.Sqlite.SqliteType.Real);
        var pMaxTempC = command.Parameters.Add("$maxTemperatureC", Microsoft.Data.Sqlite.SqliteType.Real);

        pPollRunId.Value = pollRunId;
        pMachineId.Value = machineId;
        pCapturedAt.Value = capturedAtUnixMs;

        foreach (var sensor in sensors)
        {
            pSensorKey.Value = sensor.SensorKey;
            pSensorName.Value = sensor.SensorName;
            pSensorPath.Value = sensor.SensorPath;
            pTempC.Value = sensor.TemperatureC;
            pMinTempC.Value = (object?)sensor.MinTemperatureC ?? DBNull.Value;
            pMaxTempC.Value = (object?)sensor.MaxTemperatureC ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<ThermalSensorSample>> GetSensorReadingsForPollRunAsync(
        SqliteConnection connection,
        long pollRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sensor_key, sensor_name, sensor_path, temperature_c, min_temperature_c, max_temperature_c
            FROM thermal_sensor_readings
            WHERE poll_run_id = $pollRunId
            ORDER BY sensor_key ASC;
            """;
        command.Parameters.AddWithValue("$pollRunId", pollRunId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<ThermalSensorSample> sensors = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            sensors.Add(new ThermalSensorSample(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }

        return sensors;
    }

    private static async Task InsertCapacitySnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MachineCapacitySnapshot snapshot,
        long capturedAtUnixMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO machine_capacity_snapshots (
                machine_id,
                captured_at_unix_ms,
                gpu_count,
                gpu_max_utilization_pct,
                gpu_vram_used_bytes,
                gpu_vram_total_bytes,
                gpu_max_temperature_c,
                gpu_total_power_watts,
                cpu_utilization_pct,
                cpu_temperature_c,
                cpu_power_watts,
                ram_used_bytes,
                ram_total_bytes,
                gpu_details_json,
                loaded_models_json,
                poll_status
            )
            VALUES (
                $machineId,
                $capturedAtUnixMs,
                $gpuCount,
                $gpuMaxUtil,
                $gpuVramUsed,
                $gpuVramTotal,
                $gpuMaxTemp,
                $gpuTotalPower,
                $cpuUtil,
                $cpuTemp,
                $cpuPower,
                $ramUsed,
                $ramTotal,
                $gpuDetailsJson,
                $loadedModelsJson,
                $pollStatus
            );
            """;

        var gpuMaxTemp = snapshot.Gpus
            .Where(static g => g.TemperatureC.HasValue)
            .Select(static g => g.TemperatureC!.Value)
            .DefaultIfEmpty()
            .Max();

        var gpuTotalPower = snapshot.Gpus
            .Where(static g => g.PowerDrawWatts.HasValue)
            .Sum(static g => g.PowerDrawWatts!.Value);

        var gpuVramUsed = snapshot.Gpus
            .Where(static g => g.VramUsedBytes.HasValue)
            .Sum(static g => g.VramUsedBytes!.Value);

        var gpuVramTotal = snapshot.TotalVramTotalBytes;

        command.Parameters.AddWithValue("$machineId", snapshot.MachineId);
        command.Parameters.AddWithValue("$capturedAtUnixMs", capturedAtUnixMs);
        command.Parameters.AddWithValue("$gpuCount", snapshot.Gpus.Count);
        command.Parameters.AddWithValue("$gpuMaxUtil", (object?)snapshot.MaxGpuUtilizationPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$gpuVramUsed", snapshot.Gpus.Count > 0 ? gpuVramUsed : DBNull.Value);
        command.Parameters.AddWithValue("$gpuVramTotal", gpuVramTotal > 0 ? gpuVramTotal : DBNull.Value);
        command.Parameters.AddWithValue("$gpuMaxTemp", gpuMaxTemp > 0 ? gpuMaxTemp : DBNull.Value);
        command.Parameters.AddWithValue("$gpuTotalPower", gpuTotalPower > 0 ? gpuTotalPower : DBNull.Value);
        command.Parameters.AddWithValue("$cpuUtil", (object?)snapshot.Cpu?.TotalUtilizationPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$cpuTemp", (object?)snapshot.Cpu?.TemperatureC ?? DBNull.Value);
        command.Parameters.AddWithValue("$cpuPower", (object?)snapshot.Cpu?.PackagePowerWatts ?? DBNull.Value);
        command.Parameters.AddWithValue("$ramUsed", (object?)snapshot.Memory?.UsedBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$ramTotal", (object?)snapshot.Memory?.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$gpuDetailsJson", snapshot.Gpus.Count > 0
            ? JsonSerializer.Serialize(snapshot.Gpus, JsonOptions)
            : DBNull.Value);
        command.Parameters.AddWithValue("$loadedModelsJson", snapshot.LoadedModels.Count > 0
            ? JsonSerializer.Serialize(snapshot.LoadedModels, JsonOptions)
            : DBNull.Value);
        command.Parameters.AddWithValue("$pollStatus", MachinePollStatus.Success.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PersistedCapacityState> GetCapacityStateAsync(
        SqliteConnection connection,
        string machineId,
        long capturedAtUnixMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT gpu_details_json,
                   loaded_models_json,
                   cpu_utilization_pct,
                   cpu_temperature_c,
                   cpu_power_watts,
                   ram_used_bytes,
                   ram_total_bytes
            FROM machine_capacity_snapshots
            WHERE machine_id = $machineId
              AND captured_at_unix_ms = $capturedAtUnixMs
              AND poll_status = $pollStatus
            ORDER BY snapshot_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$capturedAtUnixMs", capturedAtUnixMs);
        command.Parameters.AddWithValue("$pollStatus", MachinePollStatus.Success.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return PersistedCapacityState.Empty;
        }

        var gpus = reader.IsDBNull(0)
            ? Array.Empty<GpuMetrics>()
            : JsonSerializer.Deserialize<IReadOnlyList<GpuMetrics>>(reader.GetString(0), JsonOptions)
                ?? Array.Empty<GpuMetrics>();

        var loadedModels = reader.IsDBNull(1)
            ? Array.Empty<LoadedModelInfo>()
            : JsonSerializer.Deserialize<IReadOnlyList<LoadedModelInfo>>(reader.GetString(1), JsonOptions)
                ?? Array.Empty<LoadedModelInfo>();

        CpuMetrics? cpu = null;
        if (!reader.IsDBNull(2) || !reader.IsDBNull(3) || !reader.IsDBNull(4))
        {
            cpu = new CpuMetrics(
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4));
        }

        MemoryMetrics? memory = null;
        if (!reader.IsDBNull(5) || !reader.IsDBNull(6))
        {
            memory = new MemoryMetrics(
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6));
        }

        return new PersistedCapacityState(gpus, cpu, memory, loadedModels);
    }

    private sealed record PersistedCapacityState(
        IReadOnlyList<GpuMetrics> Gpus,
        CpuMetrics? Cpu,
        MemoryMetrics? Memory,
        IReadOnlyList<LoadedModelInfo> LoadedModels)
    {
        public static PersistedCapacityState Empty { get; } = new([], null, null, []);
    }
}
