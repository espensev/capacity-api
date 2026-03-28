using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Telemetry.Domain;
using OllamaTelemetry.Api.Features.Telemetry.Source;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.Telemetry;

public sealed class LibreHardwareMonitorTelemetrySourceTests
{
    [Fact]
    public async Task CollectAsync_ResolvesDataJsonEndpointAndCachesIt()
    {
        const string payload =
            """
            {
              "Children": [
                {
                  "Text": "Remote",
                  "Children": [
                    {
                      "Text": "CPU",
                      "Children": [
                        {
                          "Text": "Temperatures",
                          "Children": [
                            {
                              "Text": "CPU Package",
                              "Value": "71.0 °C"
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

        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
                },
                "/data.json" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        using var httpClient = new HttpClient(handler);
        var source = new LibreHardwareMonitorTelemetrySource(
            httpClient,
            new LibreHardwareJsonParser(),
            Options.Create(new TelemetryOptions
            {
                Source = new SourceOptions
                {
                    RequestTimeoutSeconds = 4,
                },
            }),
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 28, 11, 0, 0, TimeSpan.Zero)));

        var target = new MachineTelemetryTarget(
            "remote",
            "Remote Machine",
            "LibreHardwareMonitor",
            new Uri("http://192.168.2.5:8082/"),
            new SensorFilter(["CPU Package"], []));

        var firstSnapshot = await source.CollectAsync(target, CancellationToken.None);

        Assert.Equal("http://192.168.2.5:8082/data.json", firstSnapshot.Endpoint.ToString());
        Assert.Equal(new[] { "/", "/data.json" }, handler.RequestedPaths.ToArray());

        handler.RequestedPaths.Clear();

        var secondSnapshot = await source.CollectAsync(target, CancellationToken.None);

        Assert.Equal("http://192.168.2.5:8082/data.json", secondSnapshot.Endpoint.ToString());
        Assert.Equal(new[] { "/data.json" }, handler.RequestedPaths.ToArray());
        Assert.Equal(71.0, Assert.Single(secondSnapshot.ThermalSensors).TemperatureC);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
