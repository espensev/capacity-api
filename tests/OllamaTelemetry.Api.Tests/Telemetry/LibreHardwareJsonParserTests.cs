using System.Text.Json;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Source;

namespace OllamaTelemetry.Api.Tests.Telemetry;

public sealed class LibreHardwareJsonParserTests
{
    [Fact]
    public void ParseTemperatures_FindsExpectedThermalSensors()
    {
        const string json =
            """
            {
              "Children": [
                {
                  "Text": "Machine A",
                  "Children": [
                    {
                      "Text": "CPU",
                      "Children": [
                        {
                          "Text": "Temperatures",
                          "Children": [
                            {
                              "Text": "CPU Package",
                              "Value": "73.5 °C",
                              "Min": "42.0 °C",
                              "Max": "81.0 °C"
                            },
                            {
                              "Text": "CCD 1",
                              "Value": "68.0 °C"
                            }
                          ]
                        },
                        {
                          "Text": "Clocks",
                          "Children": [
                            {
                              "Text": "Core #1",
                              "Value": "4800 MHz"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);
        var parser = new LibreHardwareJsonParser();

        var sensors = parser.ParseTemperatures(document, new SensorFilter([], []));

        Assert.Collection(
            sensors.OrderBy(static sensor => sensor.SensorKey),
            sensor =>
            {
                Assert.Equal("machine-a-cpu-ccd-1", sensor.SensorKey);
                Assert.Equal("CCD 1", sensor.SensorName);
                Assert.Equal(68.0, sensor.TemperatureC);
                Assert.Null(sensor.MinTemperatureC);
                Assert.Null(sensor.MaxTemperatureC);
            },
            sensor =>
            {
                Assert.Equal("machine-a-cpu-cpu-package", sensor.SensorKey);
                Assert.Equal("CPU Package", sensor.SensorName);
                Assert.Equal(73.5, sensor.TemperatureC);
                Assert.Equal(42.0, sensor.MinTemperatureC);
                Assert.Equal(81.0, sensor.MaxTemperatureC);
            });
    }

    [Fact]
    public void ParseTemperatures_RespectsFilterKeywords()
    {
        const string json =
            """
            {
              "Children": [
                {
                  "Text": "Machine B",
                  "Children": [
                    {
                      "Text": "GPU",
                      "Children": [
                        {
                          "Text": "Temperatures",
                          "Children": [
                            {
                              "Text": "GPU Core",
                              "Value": "55.0 °C"
                            },
                            {
                              "Text": "Memory Junction",
                              "Value": "74.0 °C"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);
        var parser = new LibreHardwareJsonParser();

        var sensors = parser.ParseTemperatures(document, new SensorFilter(["Core"], []));

        var sensor = Assert.Single(sensors);
        Assert.Equal("GPU Core", sensor.SensorName);
        Assert.Equal(55.0, sensor.TemperatureC);
    }
}
