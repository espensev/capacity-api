using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.Telemetry;

public sealed class TelemetryRepositoryTests
{
    [Fact]
    public async Task RecordSuccessfulPollAsync_PersistsLatestSnapshotAndHistory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var options = Options.Create(new TelemetryOptions
            {
                Storage = new StorageOptions
                {
                    ConnectionString = $"Data Source={Path.Combine(tempRoot, "telemetry.db")};Mode=ReadWriteCreate;Cache=Shared;Pooling=False",
                    RetentionDays = 14,
                    CleanupIntervalMinutes = 60,
                },
            });

            var environment = new TestHostEnvironment
            {
                ApplicationName = "OllamaTelemetry.Api.Tests",
                ContentRootPath = tempRoot,
                EnvironmentName = Environments.Development,
            };

            var connectionFactory = new SqliteConnectionFactory(options, environment);
            var initializer = new TelemetryDatabaseInitializer(connectionFactory);
            await initializer.InitializeAsync(CancellationToken.None);

            var repository = new TelemetryRepository(connectionFactory);
            var target = new MachineTelemetryTarget(
                "machine-a",
                "Machine A",
                "LibreHardwareMonitor",
                new Uri("http://machine-a:8085/data.json"),
                new SensorFilter([], []));

            var firstSnapshot = new MachineCapacitySnapshot(
                target.MachineId,
                target.DisplayName,
                target.SourceType,
                target.Endpoint,
                new DateTimeOffset(2026, 3, 27, 20, 0, 0, TimeSpan.Zero),
                25,
                [new GpuMetrics(0, "RTX 4090", 42.0, 8L * 1024 * 1024 * 1024, 24L * 1024 * 1024 * 1024, 66.0, 320.0)],
                new CpuMetrics(31.0, 61.0, 88.0),
                new MemoryMetrics(16L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024),
                [new ThermalSensorSample("cpu-package", "CPU Package", "CPU / CPU Package", 65.0, 40.0, 78.0)],
                [new LoadedModelInfo("llama3.1:8b", 6L * 1024 * 1024 * 1024, 8192)]);

            var secondSnapshot = firstSnapshot with
            {
                CapturedAtUtc = firstSnapshot.CapturedAtUtc.AddMinutes(1),
                ThermalSensors = [new ThermalSensorSample("cpu-package", "CPU Package", "CPU / CPU Package", 67.0, 40.0, 79.0)],
            };

            await repository.RecordSuccessfulPollAsync(firstSnapshot, firstSnapshot.ThermalSensors, CancellationToken.None);
            await repository.RecordSuccessfulPollAsync(secondSnapshot, secondSnapshot.ThermalSensors, CancellationToken.None);

            var latest = await repository.GetLatestSuccessfulSnapshotAsync(target, CancellationToken.None);
            var history = await repository.GetSensorHistoryAsync(target.MachineId, "cpu-package", firstSnapshot.CapturedAtUtc.AddMinutes(-1), 10, CancellationToken.None);

            Assert.NotNull(latest);
            Assert.Equal(secondSnapshot.CapturedAtUtc, latest!.CapturedAtUtc);
            Assert.Equal(67.0, Assert.Single(latest.ThermalSensors).TemperatureC);
            Assert.Equal("RTX 4090", Assert.Single(latest.Gpus).GpuName);
            Assert.Equal(31.0, latest.Cpu?.TotalUtilizationPercent);
            Assert.Equal(64L * 1024 * 1024 * 1024, latest.Memory?.TotalBytes);
            Assert.Equal("llama3.1:8b", Assert.Single(latest.LoadedModels).ModelName);

            Assert.Collection(
                history,
                point => Assert.Equal(65.0, point.TemperatureC),
                point => Assert.Equal(67.0, point.TemperatureC));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
