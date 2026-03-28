using System.Text.Json;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Api;

public static class IngestEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ingest");

        group.MapPost("/snapshot", async Task<IResult> (
            HttpRequest request,
            MachineTelemetryRegistry registry,
            LatestTelemetryCache cache,
            TimeProvider timeProvider,
            ILogger<Program> logger) =>
        {
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(request.Body);
            }
            catch (JsonException)
            {
                return TypedResults.BadRequest(new { error = "Invalid JSON" });
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (!root.TryGetProperty("machine_id", out var machineIdEl)
                    || machineIdEl.GetString() is not { Length: > 0 } machineId)
                {
                    return TypedResults.BadRequest(new { error = "machine_id is required" });
                }

                if (!registry.TryGet(machineId, out var target))
                {
                    return TypedResults.NotFound(new { error = $"Unknown machine '{machineId}'" });
                }

                var now = timeProvider.GetUtcNow();
                var snapshot = ParseSnapshot(root, target, now);
                cache.UpdateSuccess(target, snapshot);

                logger.LogDebug("Ingested snapshot from {MachineId}: {GpuCount} GPU(s).",
                    machineId, snapshot.Gpus.Count);

                return TypedResults.Ok(new { machine_id = machineId, accepted = true });
            }
        });

        return endpoints;
    }

    private static MachineCapacitySnapshot ParseSnapshot(
        JsonElement root,
        MachineTelemetryTarget target,
        DateTimeOffset capturedAtUtc)
    {
        var gpus = ParseGpus(root);
        var cpu = ParseCpu(root);
        var memory = ParseMemory(root);
        var thermals = BuildThermalSensors(root, gpus, target.SensorFilter);

        return new MachineCapacitySnapshot(
            target.MachineId,
            target.DisplayName,
            target.SourceType,
            target.Endpoint,
            capturedAtUtc,
            LatencyMs: 0,
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

        return util is null && temp is null ? null : new CpuMetrics(util, temp, null);
    }

    private static MemoryMetrics? ParseMemory(JsonElement root)
    {
        if (!root.TryGetProperty("system", out var sys))
            return null;

        var used = GetOptionalInt64(sys, "memory_used_bytes");
        var total = GetOptionalInt64(sys, "memory_total_bytes");

        return used is null && total is null ? null : new MemoryMetrics(used, total);
    }

    private static IReadOnlyList<ThermalSensorSample> BuildThermalSensors(
        JsonElement root,
        IReadOnlyList<GpuMetrics> gpus,
        SensorFilter filter)
    {
        var sensors = new List<ThermalSensorSample>();

        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC.HasValue)
            {
                var sensorName = $"{gpu.GpuName} Core";
                var sensorPath = $"GPU {gpu.GpuIndex} / {gpu.GpuName} / Core";
                if (filter.Matches(sensorName) || filter.Matches(sensorPath))
                    sensors.Add(new ThermalSensorSample($"gpu-{gpu.GpuIndex}-core", sensorName, sensorPath, gpu.TemperatureC.Value, null, null));
            }
        }

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
                    sensors.Add(new ThermalSensorSample($"gpu-{gpuIndex}-hotspot", sensorName, sensorPath, hotspot.Value, null, null));
            }
        }

        if (root.TryGetProperty("system", out var sys))
        {
            var cpuTemp = GetOptionalDouble(sys, "cpu_package_c");
            if (cpuTemp.HasValue)
            {
                const string sensorName = "CPU Package";
                const string sensorPath = "CPU / Package Temperature";
                if (filter.Matches(sensorName) || filter.Matches(sensorPath))
                    sensors.Add(new ThermalSensorSample("cpu-package", sensorName, sensorPath, cpuTemp.Value, null, null));
            }
        }

        return sensors;
    }

    private static double? GetOptionalDouble(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetDouble() : null;
    }

    private static long? GetOptionalInt64(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt64() : null;
    }
}
