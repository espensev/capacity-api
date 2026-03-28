using OllamaTelemetry.Api.Features.Evaluation.Contracts;

namespace OllamaTelemetry.Api.Features.Evaluation.Api;

public static class EvaluationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/evals");

        group.MapGet("/catalog/models", async (
            string? machineId,
            bool? loadedOnly,
            EvaluationService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetModelCatalogAsync(machineId, loadedOnly ?? false, cancellationToken)));

        group.MapGet("/runs", async (
            int? limit,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedLimit = limit ?? 20;
            if (resolvedLimit is < 1 or > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be between 1 and 200."],
                });
            }

            return Results.Ok(await service.GetRunsAsync(resolvedLimit, cancellationToken));
        });

        group.MapPost("/runs", async (
            CreateEvaluationRunRequest request,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "title is required" });
            }

            try
            {
                var detail = await service.CreateRunAsync(request, cancellationToken);
                return Results.Created($"/api/evals/runs/{detail.RunId}", detail);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/runs/{runId}", async Task<IResult> (
            string runId,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            var detail = await service.GetRunDetailAsync(runId, cancellationToken);
            return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
        });

        group.MapPost("/runs/{runId}/cases", async Task<IResult> (
            string runId,
            CreateEvaluationCaseRequest request,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.PromptLabel) || string.IsNullOrWhiteSpace(request.PromptText))
            {
                return Results.BadRequest(new { error = "promptLabel and promptText are required" });
            }

            var created = await service.AddCaseAsync(runId, request, cancellationToken);
            return created is null ? TypedResults.NotFound() : TypedResults.Ok(created);
        });

        group.MapPost("/runs/{runId}/cases/{caseId}/execute", async Task<IResult> (
            string runId,
            string caseId,
            ExecuteEvaluationCaseRequest request,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ExecuteCaseAsync(runId, caseId, request, cancellationToken);
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        });

        group.MapPost("/runs/{runId}/cases/{caseId}/results/{candidateId}/judgment", async Task<IResult> (
            string runId,
            string caseId,
            string candidateId,
            RecordEvaluationJudgmentRequest request,
            EvaluationService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RecordJudgmentAsync(runId, caseId, candidateId, request, cancellationToken);
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        });

        return endpoints;
    }
}
