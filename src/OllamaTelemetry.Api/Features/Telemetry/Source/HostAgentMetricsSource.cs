using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Source;

/// <summary>
/// Metrics source that consumes the C++ host_agent's /v1/* HTTP API.
/// Each machine runs its own host_agent on a known port; the C# API
/// simply reads the pre-aggregated snapshot rather than scraping raw
/// LibreHardwareMonitor JSON.
/// </summary>
public sealed class HostAgentMetricsSource(
    HttpClient httpClient,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<HostAgentMetricsSource> logger) : IMachineMetricsSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public string SourceType => "HostAgent";

    public async Task<MachineCapacitySnapshot> CollectAsync(
        MachineTelemetryTarget target,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Value.Source.RequestTimeoutSeconds));

        var snapshotUri = new Uri(target.Endpoint, "/v1/snapshot");

        JsonDocument doc;
        try
        {
            using var response = await httpClient.GetAsync(
                snapshotUri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {options.Value.Source.RequestTimeoutSeconds}s requesting '{snapshotUri}' for '{target.MachineId}'.",
                ex);
        }

        using (doc)
        {
            var latencyMs = (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue);
            return ParseSnapshot(doc.RootElement, target, snapshotUri, startedAtUtc, latencyMs);
        }
    }

    public async Task<IReadOnlyList<DiscoveredTemperatureSensor>> DiscoverAsync(
        MachineTelemetryTarget target,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Value.Source.RequestTimeoutSeconds));

        var catalogUri = new Uri(target.Endpoint, "/v1/catalog?sensor_type=Temperature");

        try
        {
            using var response = await httpClient.GetAsync(
                catalogUri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            return ParseDiscoveredSensors(doc.RootElement, target.SensorFilter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "Sensor discovery failed for host_agent at {Endpoint}.", target.Endpoint);
            return [];
        }
    }

    // ── Snapshot parsing ────────────────────────────────────────────────

    private static MachineCapacitySnapshot ParseSnapshot(
        JsonElement root,
        MachineTelemetryTarget target,
        Uri endpoint,
        DateTimeOffset capturedAtUtc,
        int latencyMs)
    {
        var gpus = ParseGpus(root);
        var cpu = ParseCpu(root);
        var memory = ParseMemory(root);
        var thermals = BuildThermalSensors(root, gpus, target.SensorFilter);

        return new MachineCapacitySnapshot(
            target.MachineId,
            target.DisplayName,
            "HostAgent",
            endpoint,
            capturedAtUtc,
            latencyMs,
            gpus,
            cpu,
            memory,
            thermals,
            []);
    }

    private static IReadOnlyList<GpuMetrics> ParseGpus(JsonElement root)
    {
        if (!root.TryGetProperty("gpus", out var gpusEl) || gpusEl.ValueKind != JsonValueKind.Array)
            return [];

        var gpus = new List<GpuMetrics>();
        foreach (var g in gpusEl.EnumerateArray())
        {
            gpus.Add(new GpuMetrics(
                GpuIndex: g.TryGetProperty("gpu_index", out var idx) ? idx.GetInt32() : gpus.Count,
                GpuName: g.TryGetProperty("gpu_name", out var name) ? name.GetString() ?? $"GPU {gpus.Count}" : $"GPU {gpus.Count}",
                UtilizationPercent: GetOptionalDouble(g, "util_gpu_pct"),
                VramUsedBytes: GetOptionalInt64(g, "vram_used_bytes"),
                VramTotalBytes: GetOptionalInt64(g, "vram_total_bytes"),
                TemperatureC: GetOptionalDouble(g, "core_c"),
                PowerDrawWatts: GetOptionalDouble(g, "power_w")));
        }

        return gpus;
    }

    private static CpuMetrics? ParseCpu(JsonElement root)
    {
        if (!root.TryGetProperty("system", out var sys))
            return null;

        var util = GetOptionalDouble(sys, "cpu_total_load_pct");
        var temp = GetOptionalDouble(sys, "cpu_package_c");

        if (util is null && temp is null)
            return null;

        return new CpuMetrics(util, temp, PackagePowerWatts: null);
    }

    private static MemoryMetrics? ParseMemory(JsonElement root)
    {
        if (!root.TryGetProperty("system", out var sys))
            return null;

        var used = GetOptionalInt64(sys, "memory_used_bytes");
        var total = GetOptionalInt64(sys, "memory_total_bytes");

        if (used is null && total is null)
            return null;

        return new MemoryMetrics(used, total);
    }

    private static IReadOnlyList<ThermalSensorSample> BuildThermalSensors(
        JsonElement root,
        IReadOnlyList<GpuMetrics> gpus,
        SensorFilter filter)
    {
        var sensors = new List<ThermalSensorSample>();

        // GPU core temperatures
        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC.HasValue)
            {
                var sensorName = $"{gpu.GpuName} Core";
                var sensorPath = $"GPU {gpu.GpuIndex} / {gpu.GpuName} / Core";
                if (filter.Matches(sensorName) || filter.Matches(sensorPath))
                {
                    sensors.Add(new ThermalSensorSample(
                        $"gpu-{gpu.GpuIndex}-core",
                        sensorName,
                        sensorPath,
                        gpu.TemperatureC.Value,
                        null, null));
                }
            }
        }

        // GPU hotspot temperatures (from raw snapshot JSON)
        if (root.TryGetProperty("gpus", out var gpusEl) && gpusEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gpusEl.EnumerateArray())
            {
                var hotspot = GetOptionalDouble(g, "hotspot_c");
                if (hotspot is null) continue;

                var gpuIndex = g.TryGetProperty("gpu_index", out var idx) ? idx.GetInt32() : 0;
                var gpuName = g.TryGetProperty("gpu_name", out var n) ? n.GetString() ?? $"GPU {gpuIndex}" : $"GPU {gpuIndex}";
                var sensorName = $"{gpuName} Hot Spot";
                var sensorPath = $"GPU {gpuIndex} / {gpuName} / Hot Spot";

                if (filter.Matches(sensorName) || filter.Matches(sensorPath))
                {
                    sensors.Add(new ThermalSensorSample(
                        $"gpu-{gpuIndex}-hotspot",
                        sensorName,
                        sensorPath,
                        hotspot.Value,
                        null, null));
                }
            }
        }

        // CPU package temperature
        if (root.TryGetProperty("system", out var sys))
        {
            var cpuTemp = GetOptionalDouble(sys, "cpu_package_c");
            if (cpuTemp.HasValue)
            {
                const string sensorName = "CPU Package";
                const string sensorPath = "CPU / Package Temperature";
                if (filter.Matches(sensorName) || filter.Matches(sensorPath))
                {
                    sensors.Add(new ThermalSensorSample(
                        "cpu-package",
                        sensorName,
                        sensorPath,
                        cpuTemp.Value,
                        null, null));
                }
            }
        }

        return sensors;
    }

    // ── Catalog discovery parsing ───────────────────────────────────────

    private static IReadOnlyList<DiscoveredTemperatureSensor> ParseDiscoveredSensors(
        JsonElement root,
        SensorFilter filter)
    {
        if (!root.TryGetProperty("sensors", out var sensorsEl) || sensorsEl.ValueKind != JsonValueKind.Array)
            return [];

        var sensors = new List<DiscoveredTemperatureSensor>();
        foreach (var s in sensorsEl.EnumerateArray())
        {
            var sensorUid = s.TryGetProperty("sensor_uid", out var uid) ? uid.GetString() ?? "" : "";
            var sensorName = s.TryGetProperty("sensor_name", out var sn) ? sn.GetString() ?? "" : "";
            var hardwareName = s.TryGetProperty("hardware_name", out var hw) ? hw.GetString() ?? "" : "";
            var sensorPath = $"{hardwareName} / {sensorName}";

            if (filter.Matches(sensorName) || filter.Matches(sensorPath))
            {
                sensors.Add(new DiscoveredTemperatureSensor(sensorUid, sensorName, sensorPath));
            }
        }

        return sensors;
    }

    // ── JSON helpers ────────────────────────────────────────────────────

    private static double? GetOptionalDouble(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var val))
            return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetDouble() : null;
    }

    private static long? GetOptionalInt64(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var val))
            return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt64() : null;
    }
}
