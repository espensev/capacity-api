using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.Telemetry;

public sealed class ReadingPersistencePolicyTests
{
    [Fact]
    public void SelectSensorsToPersist_ThinsUnchangedValuesUntilSilenceWindowExpires()
    {
        var options = Options.Create(new TelemetryOptions
        {
            Persistence = new PersistenceOptions
            {
                MinimumDeltaCelsius = 0.5,
                ForceWriteIntervalSeconds = 60,
            },
        });

        var policy = new ReadingPersistencePolicy(options);
        var endpoint = new Uri("http://machine-a:8085/data.json");

        var firstSnapshot = new MachineCapacitySnapshot(
            "machine-a",
            "Machine A",
            "LibreHardwareMonitor",
            endpoint,
            new DateTimeOffset(2026, 3, 27, 20, 0, 0, TimeSpan.Zero),
            20,
            [],
            null,
            null,
            [new ThermalSensorSample("cpu-package", "CPU Package", "Machine A / CPU / CPU Package", 65.0, null, null)],
            []);

        var secondSnapshot = firstSnapshot with
        {
            CapturedAtUtc = firstSnapshot.CapturedAtUtc.AddSeconds(10),
            ThermalSensors = [new ThermalSensorSample("cpu-package", "CPU Package", "Machine A / CPU / CPU Package", 65.2, null, null)],
        };

        var thirdSnapshot = firstSnapshot with
        {
            CapturedAtUtc = firstSnapshot.CapturedAtUtc.AddSeconds(70),
            ThermalSensors = [new ThermalSensorSample("cpu-package", "CPU Package", "Machine A / CPU / CPU Package", 65.2, null, null)],
        };

        Assert.Single(policy.SelectSensorsToPersist(firstSnapshot));
        Assert.Empty(policy.SelectSensorsToPersist(secondSnapshot));
        Assert.Single(policy.SelectSensorsToPersist(thirdSnapshot));
    }
}
