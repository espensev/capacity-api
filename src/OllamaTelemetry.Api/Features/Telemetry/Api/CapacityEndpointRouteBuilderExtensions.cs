namespace OllamaTelemetry.Api.Features.Telemetry.Api;

public static class CapacityEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapCapacityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/machines");

        group.MapGet("/{machineId}/capacity", async Task<IResult> (
            string machineId,
            TelemetryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetMachineCapacityAsync(machineId, cancellationToken);
            return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
        });

        group.MapGet("/best-fit", async Task<IResult> (
            long? requiredVramBytes,
            TelemetryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var vram = requiredVramBytes ?? 0;

            if (vram < 0)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requiredVramBytes"] = ["requiredVramBytes must be non-negative."],
                });
            }

            return TypedResults.Ok(await service.GetBestFitAsync(vram, cancellationToken));
        });

        return endpoints;
    }
}
