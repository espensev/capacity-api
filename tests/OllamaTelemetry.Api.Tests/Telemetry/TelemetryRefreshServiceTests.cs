using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Source;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.Telemetry;

public sealed class TelemetryRefreshServiceTests
{
    [Fact]
    public async Task EnsureMachineFreshAsync_UsesCacheUntilRefreshWindowExpires()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero));
            var options = Options.Create(new TelemetryOptions
            {
                RefreshAfterSeconds = 60,
                StaleAfterSeconds = 300,
                Storage = new StorageOptions
                {
                    ConnectionString = $"Data Source={Path.Combine(tempRoot, "telemetry.db")};Mode=ReadWriteCreate;Cache=Shared;Pooling=False",
                    RetentionDays = 14,
                    CleanupIntervalMinutes = 60,
                },
                Persistence = new PersistenceOptions
                {
                    MinimumDeltaCelsius = 0.5,
                    ForceWriteIntervalSeconds = 60,
                },
                Machines =
                [
                    new MachineTelemetryTargetOptions
                    {
                        MachineId = "remote",
                        DisplayName = "Remote Machine",
                        SourceType = "LibreHardwareMonitor",
                        Endpoint = "http://192.168.2.5:8082/",
                        Sensors = new SensorFilterOptions
                        {
                            IncludeKeywords = ["CPU Package"],
                        },
                    },
                ],
            });

            var environment = new TestHostEnvironment
            {
                ApplicationName = "OllamaTelemetry.Api.Tests",
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            };

            var registry = new MachineTelemetryRegistry(options);
            var cache = new LatestTelemetryCache();
            var policy = new ReadingPersistencePolicy(options);
            var connectionFactory = new SqliteConnectionFactory(options, environment);
            var initializer = new TelemetryDatabaseInitializer(connectionFactory);
            await initializer.InitializeAsync(CancellationToken.None);

            var repository = new TelemetryRepository(connectionFactory);
            var source = new RecordingTelemetrySource(
                CreateSnapshot("remote", "http://192.168.2.5:8082/data.json", timeProvider.GetUtcNow(), 64.0),
                CreateSnapshot("remote", "http://192.168.2.5:8082/data.json", timeProvider.GetUtcNow().AddSeconds(70), 67.0));
            var resolver = new TelemetrySourceResolver([source]);

            var service = new TelemetryRefreshService(
                registry,
                resolver,
                cache,
                policy,
                repository,
                options,
                timeProvider,
                NullLogger<TelemetryRefreshService>.Instance);

            Assert.True(await service.EnsureMachineFreshAsync("remote", CancellationToken.None));
            Assert.Equal(1, source.CollectCallCount);

            Assert.True(await service.EnsureMachineFreshAsync("remote", CancellationToken.None));
            Assert.Equal(1, source.CollectCallCount);

            timeProvider.Advance(TimeSpan.FromSeconds(70));

            Assert.True(await service.EnsureMachineFreshAsync("remote", CancellationToken.None));
            Assert.Equal(2, source.CollectCallCount);

            var target = registry.All.Single();
            var latest = await repository.GetLatestSuccessfulSnapshotAsync(target, CancellationToken.None);
            var history = await repository.GetSensorHistoryAsync("remote", "remote-cpu-cpu-package", timeProvider.GetUtcNow().AddHours(-1), 10, CancellationToken.None);

            Assert.NotNull(latest);
            Assert.Equal("http://192.168.2.5:8082/data.json", latest!.Endpoint.ToString());
            Assert.Equal(67.0, Assert.Single(latest.ThermalSensors).TemperatureC);

            Assert.Collection(
                history,
                point => Assert.Equal(64.0, point.TemperatureC),
                point => Assert.Equal(67.0, point.TemperatureC));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static MachineCapacitySnapshot CreateSnapshot(
        string machineId,
        string endpoint,
        DateTimeOffset capturedAtUtc,
        double temperatureC)
        => new(
            machineId,
            "Remote Machine",
            "LibreHardwareMonitor",
            new Uri(endpoint),
            capturedAtUtc,
            25,
            [],
            null,
            null,
            [new ThermalSensorSample("remote-cpu-cpu-package", "CPU Package", "Remote / CPU / CPU Package", temperatureC, null, null)],
            []);

    private sealed class RecordingTelemetrySource(params MachineCapacitySnapshot[] snapshots) : IMachineMetricsSource
    {
        private readonly Queue<MachineCapacitySnapshot> _snapshots = new(snapshots);

        public string SourceType => "LibreHardwareMonitor";

        public int CollectCallCount { get; private set; }

        public Task<MachineCapacitySnapshot> CollectAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
        {
            CollectCallCount++;
            return Task.FromResult(_snapshots.Dequeue());
        }

        public Task<IReadOnlyList<DiscoveredTemperatureSensor>> DiscoverAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DiscoveredTemperatureSensor>>([]);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
