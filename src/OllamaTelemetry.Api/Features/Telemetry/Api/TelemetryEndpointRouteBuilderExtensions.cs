using OllamaTelemetry.Api.Features.Telemetry.Contracts;

namespace OllamaTelemetry.Api.Features.Telemetry.Api;

public static class TelemetryEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/telemetry");

        group.MapGet("/overview", async (TelemetryQueryService service, CancellationToken cancellationToken)
            => TypedResults.Ok(await service.GetOverviewAsync(cancellationToken)));

        group.MapGet("/machines/{machineId}/latest", async Task<IResult> (
            string machineId,
            TelemetryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetMachineLatestAsync(machineId, cancellationToken);
            return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
        });

        group.MapGet("/machines/{machineId}/history/{sensorKey}", async Task<IResult> (
            string machineId,
            string sensorKey,
            int? hours,
            int? limit,
            TelemetryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedHours = hours ?? 6;
            var resolvedLimit = limit ?? 500;

            if (resolvedHours is < 1 or > 168)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["hours"] = ["hours must be between 1 and 168."],
                });
            }

            if (resolvedLimit is < 1 or > 5000)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be between 1 and 5000."],
                });
            }

            var response = await service.GetHistoryAsync(machineId, sensorKey, resolvedHours, resolvedLimit, cancellationToken);
            return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
        });

        group.MapGet("/machines/{machineId}/discovery", async Task<IResult> (
            string machineId,
            TelemetryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.DiscoverAsync(machineId, cancellationToken);
            return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
        });

        return endpoints;
    }
}
