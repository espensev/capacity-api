using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Features.Telemetry.Contracts;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Source;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Api;

public sealed class TelemetryQueryService(
    MachineTelemetryRegistry machineRegistry,
    LatestTelemetryCache latestTelemetryCache,
    TelemetryRepository telemetryRepository,
    TelemetryRefreshService telemetryRefreshService,
    LlmUsageRepository llmUsageRepository,
    TelemetrySourceResolver sourceResolver,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider)
{
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(options.Value.StaleAfterSeconds);

    public async Task<TelemetryOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken)
    {
        await telemetryRefreshService.EnsureAllFreshAsync(cancellationToken);

        List<MachineTelemetryStateResponse> machines = [];

        foreach (var machine in machineRegistry.All)
        {
            machines.Add(await GetMachineStateAsync(machine, cancellationToken));
        }

        return new TelemetryOverviewResponse(timeProvider.GetUtcNow(), options.Value.StaleAfterSeconds, machines);
    }

    public async Task<MachineTelemetryStateResponse?> GetMachineLatestAsync(string machineId, CancellationToken cancellationToken)
    {
        if (!machineRegistry.TryGet(machineId, out var machine))
        {
            return null;
        }

        await telemetryRefreshService.EnsureMachineFreshAsync(machineId, cancellationToken);
        return await GetMachineStateAsync(machine, cancellationToken);
    }

    public async Task<ThermalHistoryResponse?> GetHistoryAsync(
        string machineId,
        string sensorKey,
        int hours,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!machineRegistry.TryGet(machineId, out _))
        {
            return null;
        }

        var sinceUtc = timeProvider.GetUtcNow().AddHours(-hours);
        var points = await telemetryRepository.GetSensorHistoryAsync(machineId, sensorKey, sinceUtc, limit, cancellationToken);

        return new ThermalHistoryResponse(
            machineId,
            sensorKey,
            sinceUtc,
            points.Select(static point => new ThermalHistoryPointResponse(
                point.CapturedAtUtc,
                point.TemperatureC,
                point.MinTemperatureC,
                point.MaxTemperatureC)).ToArray());
    }

    public async Task<SensorDiscoveryResponse?> DiscoverAsync(string machineId, CancellationToken cancellationToken)
    {
        if (!machineRegistry.TryGet(machineId, out var machine))
        {
            return null;
        }

        var source = sourceResolver.Resolve(machine.SourceType);
        var sensors = await source.DiscoverAsync(machine, cancellationToken);

        return new SensorDiscoveryResponse(
            machine.MachineId,
            timeProvider.GetUtcNow(),
            sensors.Select(static sensor => new DiscoveredTemperatureSensorResponse(sensor.SensorKey, sensor.SensorName, sensor.SensorPath)).ToArray());
    }

    private async Task<MachineTelemetryStateResponse> GetMachineStateAsync(
        MachineTelemetryTarget machine,
        CancellationToken cancellationToken)
    {
        if (latestTelemetryCache.TryGet(machine.MachineId, out var runtimeState))
        {
            return ToResponse(await AttachLoadedModelsAsync(runtimeState, cancellationToken));
        }

        var latestSnapshot = await telemetryRepository.GetLatestSuccessfulSnapshotAsync(machine, cancellationToken);
        if (latestSnapshot is null)
        {
            return CreateEmptyState(machine);
        }

        var state = new MachineTelemetryRuntimeState(
            machine.MachineId,
            machine.DisplayName,
            machine.SourceType,
            latestSnapshot.Endpoint,
            latestSnapshot.CapturedAtUtc,
            latestSnapshot.CapturedAtUtc,
            latestSnapshot.LatencyMs,
            null,
            latestSnapshot.ThermalSensors,
            latestSnapshot.Gpus,
            latestSnapshot.Cpu,
            latestSnapshot.Memory,
            latestSnapshot.LoadedModels);

        return ToResponse(await AttachLoadedModelsAsync(state, cancellationToken));
    }

    private MachineTelemetryStateResponse CreateEmptyState(MachineTelemetryTarget machine)
        => ToResponse(MachineTelemetryRuntimeState.Empty(machine));

    private MachineTelemetryStateResponse ToResponse(MachineTelemetryRuntimeState state)
    {
        var hottestSensor = state.CurrentSensors
            .OrderByDescending(static sensor => sensor.TemperatureC)
            .ThenBy(static sensor => sensor.SensorKey, StringComparer.Ordinal)
            .FirstOrDefault();

        var vramFree = state.Gpus.Any(static g => g.VramTotalBytes.HasValue && g.VramUsedBytes.HasValue)
            ? state.Gpus.Where(static g => g.VramTotalBytes.HasValue && g.VramUsedBytes.HasValue)
                .Sum(static g => g.VramTotalBytes!.Value - g.VramUsedBytes!.Value)
            : (long?)null;

        var vramTotal = state.Gpus.Any(static g => g.VramTotalBytes.HasValue)
            ? state.Gpus.Where(static g => g.VramTotalBytes.HasValue).Sum(static g => g.VramTotalBytes!.Value)
            : (long?)null;

        var maxGpuUtil = state.Gpus.Any(static g => g.UtilizationPercent.HasValue)
            ? state.Gpus.Where(static g => g.UtilizationPercent.HasValue).Max(static g => g.UtilizationPercent!.Value)
            : (double?)null;

        return new MachineTelemetryStateResponse(
            state.MachineId,
            state.DisplayName,
            state.SourceType,
            state.Endpoint.ToString(),
            state.LastAttemptedAtUtc,
            state.LastSuccessfulAtUtc,
            state.LastLatencyMs,
            state.IsStale(_staleAfter, timeProvider),
            state.LastError,
            hottestSensor?.SensorKey,
            hottestSensor?.TemperatureC,
            maxGpuUtil,
            vramFree,
            vramTotal,
            state.Cpu?.TotalUtilizationPercent,
            state.Memory?.UsedBytes,
            state.Memory?.TotalBytes,
            state.CurrentSensors.Select(static sensor => new ThermalSensorResponse(
                sensor.SensorKey,
                sensor.SensorName,
                sensor.SensorPath,
                sensor.TemperatureC,
                sensor.MinTemperatureC,
                sensor.MaxTemperatureC)).ToArray());
    }

    public async Task<MachineCapacityResponse?> GetMachineCapacityAsync(string machineId, CancellationToken cancellationToken)
    {
        if (!machineRegistry.TryGet(machineId, out var machine))
        {
            return null;
        }

        await telemetryRefreshService.EnsureMachineFreshAsync(machineId, cancellationToken);

        if (!latestTelemetryCache.TryGet(machineId, out var state))
        {
            return null;
        }

        return ToCapacityResponse(await AttachLoadedModelsAsync(state, cancellationToken));
    }

    public async Task<BestFitResponse> GetBestFitAsync(long requiredVramBytes, CancellationToken cancellationToken)
    {
        await telemetryRefreshService.EnsureAllFreshAsync(cancellationToken);

        List<MachineFitCandidate> candidates = [];

        foreach (var machine in machineRegistry.All)
        {
            if (!latestTelemetryCache.TryGet(machine.MachineId, out var state))
            {
                candidates.Add(new MachineFitCandidate(machine.MachineId, machine.DisplayName, "unknown", 0, 0, null));
                continue;
            }

            var isStale = state.IsStale(_staleAfter, timeProvider);
            var vramFree = state.Gpus
                .Where(static g => g.VramTotalBytes.HasValue && g.VramUsedBytes.HasValue)
                .Sum(static g => g.VramTotalBytes!.Value - g.VramUsedBytes!.Value);
            var vramTotal = state.Gpus
                .Where(static g => g.VramTotalBytes.HasValue)
                .Sum(static g => g.VramTotalBytes!.Value);

            var verdict = ComputeVerdict(state, isStale, vramFree, requiredVramBytes);

            candidates.Add(new MachineFitCandidate(
                state.MachineId,
                state.DisplayName,
                verdict,
                vramFree,
                vramTotal,
                state.Gpus.Any(static g => g.UtilizationPercent.HasValue)
                    ? state.Gpus.Where(static g => g.UtilizationPercent.HasValue).Max(static g => g.UtilizationPercent!.Value)
                    : null));
        }

        candidates.Sort((a, b) => b.VramFreeBytes.CompareTo(a.VramFreeBytes));

        return new BestFitResponse(timeProvider.GetUtcNow(), requiredVramBytes, candidates);
    }

    private MachineCapacityResponse ToCapacityResponse(MachineTelemetryRuntimeState state)
    {
        var isStale = state.IsStale(_staleAfter, timeProvider);
        var vramUsed = state.Gpus
            .Where(static g => g.VramUsedBytes.HasValue)
            .Sum(static g => g.VramUsedBytes!.Value);
        var vramTotal = state.Gpus
            .Where(static g => g.VramTotalBytes.HasValue)
            .Sum(static g => g.VramTotalBytes!.Value);
        var vramFree = state.Gpus
            .Where(static g => g.VramTotalBytes.HasValue && g.VramUsedBytes.HasValue)
            .Sum(static g => g.VramTotalBytes!.Value - g.VramUsedBytes!.Value);

        var verdict = ComputeVerdict(state, isStale, vramFree, 0);

        GpuCapacitySummaryResponse? gpuSummary = state.Gpus.Count > 0
            ? new GpuCapacitySummaryResponse(
                state.Gpus.Count,
                state.Gpus.Any(static g => g.UtilizationPercent.HasValue)
                    ? state.Gpus.Where(static g => g.UtilizationPercent.HasValue).Max(static g => g.UtilizationPercent!.Value)
                    : null,
                vramUsed,
                vramTotal,
                vramFree,
                state.Gpus.Where(static g => g.TemperatureC.HasValue).Select(static g => g.TemperatureC!.Value).DefaultIfEmpty().Max(),
                state.Gpus.Where(static g => g.PowerDrawWatts.HasValue).Sum(static g => g.PowerDrawWatts!.Value))
            : null;

        return new MachineCapacityResponse(
            state.MachineId,
            state.DisplayName,
            state.SourceType,
            state.Endpoint.ToString(),
            state.LastSuccessfulAtUtc,
            state.LastLatencyMs,
            isStale,
            verdict,
            gpuSummary,
            state.Gpus.Select(static g => new GpuDetailResponse(
                g.GpuIndex, g.GpuName, g.UtilizationPercent,
                g.VramUsedBytes, g.VramTotalBytes,
                g.TemperatureC, g.PowerDrawWatts)).ToArray(),
            state.Cpu is not null
                ? new CpuCapacityResponse(state.Cpu.TotalUtilizationPercent, state.Cpu.TemperatureC, state.Cpu.PackagePowerWatts)
                : null,
            state.Memory is not null
                ? new MemoryCapacityResponse(
                    state.Memory.UsedBytes,
                    state.Memory.TotalBytes,
                    state.Memory is { UsedBytes: not null, TotalBytes: not null }
                        ? state.Memory.TotalBytes.Value - state.Memory.UsedBytes.Value
                        : null)
                : null,
            state.LoadedModels.Select(static m => new LoadedModelResponse(m.ModelName, m.SizeVramBytes, m.ContextLength)).ToArray(),
            state.LastError);
    }

    private static string ComputeVerdict(MachineTelemetryRuntimeState state, bool isStale, long vramFree, long requiredVramBytes)
    {
        if (isStale)
        {
            return "stale";
        }

        if (!state.Gpus.Any(static g => g.VramTotalBytes.HasValue))
        {
            return "unknown";
        }

        if (requiredVramBytes > 0 && vramFree < requiredVramBytes)
        {
            return "insufficient_vram";
        }

        var maxUtil = state.Gpus
            .Where(static g => g.UtilizationPercent.HasValue)
            .Select(static g => g.UtilizationPercent!.Value)
            .DefaultIfEmpty()
            .Max();

        return maxUtil switch
        {
            < 30 => "fits_idle",
            < 80 => "fits_available",
            _ => "fits_busy",
        };
    }

    private async Task<MachineTelemetryRuntimeState> AttachLoadedModelsAsync(
        MachineTelemetryRuntimeState state,
        CancellationToken cancellationToken)
    {
        var latestLoadedModels = await llmUsageRepository.GetLatestLoadedModelsAsync(state.MachineId, cancellationToken);
        if (latestLoadedModels.Count == 0)
        {
            return state;
        }

        return state with
        {
            LoadedModels = latestLoadedModels
                .Select(static snapshot => new LoadedModelInfo(snapshot.ModelName, snapshot.SizeVramBytes, snapshot.ContextLength))
                .ToArray(),
        };
    }
}
