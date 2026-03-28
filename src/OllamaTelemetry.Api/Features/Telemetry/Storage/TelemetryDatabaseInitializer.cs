namespace OllamaTelemetry.Api.Features.Telemetry.Storage;

public sealed class TelemetryDatabaseInitializer(SqliteConnectionFactory connectionFactory)
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

            CREATE TABLE IF NOT EXISTS machine_poll_runs (
                poll_run_id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL,
                source_type TEXT NOT NULL,
                endpoint TEXT NOT NULL,
                captured_at_unix_ms INTEGER NOT NULL,
                poll_status TEXT NOT NULL,
                latency_ms INTEGER NOT NULL,
                sensor_count INTEGER NOT NULL,
                persisted_sensor_count INTEGER NOT NULL,
                error_message TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS thermal_sensor_readings (
                poll_run_id INTEGER NOT NULL,
                machine_id TEXT NOT NULL,
                captured_at_unix_ms INTEGER NOT NULL,
                sensor_key TEXT NOT NULL,
                sensor_name TEXT NOT NULL,
                sensor_path TEXT NOT NULL,
                temperature_c REAL NOT NULL,
                min_temperature_c REAL NULL,
                max_temperature_c REAL NULL,
                PRIMARY KEY (poll_run_id, sensor_key),
                FOREIGN KEY (poll_run_id) REFERENCES machine_poll_runs(poll_run_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_machine_poll_runs_machine_time
                ON machine_poll_runs(machine_id, captured_at_unix_ms DESC);

            CREATE INDEX IF NOT EXISTS idx_thermal_sensor_readings_machine_sensor_time
                ON thermal_sensor_readings(machine_id, sensor_key, captured_at_unix_ms DESC);

            CREATE TABLE IF NOT EXISTS machine_capacity_snapshots (
                snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_id TEXT NOT NULL,
                captured_at_unix_ms INTEGER NOT NULL,
                gpu_count INTEGER NOT NULL DEFAULT 0,
                gpu_max_utilization_pct REAL NULL,
                gpu_vram_used_bytes INTEGER NULL,
                gpu_vram_total_bytes INTEGER NULL,
                gpu_max_temperature_c REAL NULL,
                gpu_total_power_watts REAL NULL,
                cpu_utilization_pct REAL NULL,
                cpu_temperature_c REAL NULL,
                cpu_power_watts REAL NULL,
                ram_used_bytes INTEGER NULL,
                ram_total_bytes INTEGER NULL,
                gpu_details_json TEXT NULL,
                loaded_models_json TEXT NULL,
                poll_status TEXT NOT NULL,
                error_message TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_capacity_machine_time
                ON machine_capacity_snapshots(machine_id, captured_at_unix_ms DESC);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
