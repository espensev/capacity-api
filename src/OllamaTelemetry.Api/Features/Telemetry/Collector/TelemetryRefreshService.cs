using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Source;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Features.Telemetry.Collector;

public sealed class TelemetryRefreshService(
    MachineTelemetryRegistry machineRegistry,
    TelemetrySourceResolver sourceResolver,
    LatestTelemetryCache latestTelemetryCache,
    ReadingPersistencePolicy readingPersistencePolicy,
    TelemetryRepository telemetryRepository,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryRefreshService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _machineLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _refreshAfter = TimeSpan.FromSeconds(options.Value.RefreshAfterSeconds);
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;

    public Task EnsureAllFreshAsync(CancellationToken cancellationToken)
        => Task.WhenAll(machineRegistry.All.Select(machine => EnsureMachineFreshAsync(machine.MachineId, cancellationToken)));

    public async Task<bool> EnsureMachineFreshAsync(string machineId, CancellationToken cancellationToken)
    {
        if (!machineRegistry.TryGet(machineId, out var machine))
        {
            return false;
        }

        if (!NeedsRefresh(machine))
        {
            return true;
        }

        var gate = _machineLocks.GetOrAdd(machine.MachineId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (!NeedsRefresh(machine))
            {
                return true;
            }

            await RefreshMachineAsync(machine, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool NeedsRefresh(MachineTelemetryTarget machine)
    {
        if (!latestTelemetryCache.TryGet(machine.MachineId, out var state))
        {
            return true;
        }

        if (state.LastAttemptedAtUtc is null)
        {
            return true;
        }

        return timeProvider.GetUtcNow() - state.LastAttemptedAtUtc.Value >= _refreshAfter;
    }

    private async Task RefreshMachineAsync(MachineTelemetryTarget machine, CancellationToken cancellationToken)
    {
        var source = sourceResolver.Resolve(machine.SourceType);
        var startedAtUtc = timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var snapshot = await source.CollectAsync(machine, cancellationToken);
            latestTelemetryCache.UpdateSuccess(machine, snapshot);

            var sensorsToPersist = readingPersistencePolicy.SelectSensorsToPersist(snapshot);
            await telemetryRepository.RecordSuccessfulPollAsync(snapshot, sensorsToPersist, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await SeedCacheFromLatestSnapshotAsync(machine, cancellationToken);

            var latencyMs = (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue);
            logger.LogWarning(ex, "Telemetry refresh failed for machine {MachineId}.", machine.MachineId);
            latestTelemetryCache.UpdateFailure(machine, startedAtUtc, latencyMs, ex.Message);
            await telemetryRepository.RecordFailedPollAsync(machine, startedAtUtc, latencyMs, ex.Message, cancellationToken);
        }

        await PurgeExpiredDataIfNeededAsync(cancellationToken);
    }

    private async Task SeedCacheFromLatestSnapshotAsync(MachineTelemetryTarget machine, CancellationToken cancellationToken)
    {
        if (latestTelemetryCache.TryGet(machine.MachineId, out _))
        {
            return;
        }

        var latestSnapshot = await telemetryRepository.GetLatestSuccessfulSnapshotAsync(machine, cancellationToken);
        if (latestSnapshot is not null)
        {
            latestTelemetryCache.UpdateSuccess(machine, latestSnapshot);
        }
    }

    private async Task PurgeExpiredDataIfNeededAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanupUtc)
        {
            return;
        }

        await _cleanupLock.WaitAsync(cancellationToken);

        try
        {
            now = timeProvider.GetUtcNow();
            if (now < _nextCleanupUtc)
            {
                return;
            }

            await telemetryRepository.PurgeOlderThanAsync(now.AddDays(-options.Value.Storage.RetentionDays), cancellationToken);
            _nextCleanupUtc = now.AddMinutes(options.Value.Storage.CleanupIntervalMinutes);
        }
        finally
        {
            _cleanupLock.Release();
        }
    }
}
