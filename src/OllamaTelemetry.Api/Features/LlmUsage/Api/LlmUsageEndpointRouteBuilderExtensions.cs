using OllamaTelemetry.Api.Features.LlmUsage.Contracts;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace OllamaTelemetry.Api.Features.LlmUsage.Api;

public static class LlmUsageEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLlmUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/llm");

        group.MapGet("/overview", async (
            int? hours,
            LlmUsageQueryService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedHours = hours ?? 24;
            if (resolvedHours is < 1 or > 720)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["hours"] = ["hours must be between 1 and 720."],
                });
            }

            return Results.Ok(await service.GetOverviewAsync(resolvedHours, cancellationToken));
        });

        group.MapGet("/recent", async (
            int? limit,
            string? provider,
            string? type,
            string? machineId,
            LlmUsageQueryService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedLimit = limit ?? 50;
            if (resolvedLimit is < 1 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be between 1 and 500."],
                });
            }

            return Results.Ok(await service.GetRecentAsync(resolvedLimit, provider, type, machineId, cancellationToken));
        });

        group.MapGet("/sessions/{sessionId}", async Task<IResult> (
            string sessionId,
            LlmUsageQueryService service,
            CancellationToken cancellationToken) =>
        {
            var detail = await service.GetSessionDetailAsync(sessionId, cancellationToken);
            return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
        });

        group.MapPost("/ingest", async (
            LlmUsageIngestRequest request,
            LlmUsageRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Provider))
            {
                return Results.BadRequest(new { error = "type and provider are required" });
            }

            DateTimeOffset timestamp;
            if (!string.IsNullOrWhiteSpace(request.Timestamp) &&
                DateTimeOffset.TryParse(request.Timestamp, out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                timestamp = timeProvider.GetUtcNow();
            }

            var record = new LlmUsageRecord(
                request.Provider,
                request.Type,
                request.Session_id ?? "unknown",
                request.Model,
                timestamp,
                request.Machine_id,
                request.Assistant_kind,
                request.Input_tokens ?? 0,
                request.Output_tokens ?? 0,
                request.Cache_read_tokens ?? 0,
                request.Cache_creation_tokens ?? 0,
                request.Input_cost_usd ?? 0,
                request.Output_cost_usd ?? 0,
                request.Total_cost_usd ?? 0,
                request.Num_turns ?? 0,
                request.Duration_ms ?? 0,
                request.Tool_name,
                request.Tool_input_size ?? 0,
                request.Tool_output_size ?? 0,
                request.Was_error ?? false,
                request.Stop_reason,
                request.Cwd,
                request.Permission_mode,
                request.Source_event_id);

            await repository.InsertUsageRecordAsync(record, cancellationToken);
            return Results.Accepted();
        });

        group.MapGet("/ollama/inference", async (
            int? hours,
            int? limit,
            string? machineId,
            string? model,
            LlmUsageQueryService service,
            CancellationToken cancellationToken) =>
        {
            var resolvedHours = hours ?? 24;
            var resolvedLimit = limit ?? 100;

            if (resolvedHours is < 1 or > 720)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["hours"] = ["hours must be between 1 and 720."],
                });
            }

            if (resolvedLimit is < 1 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be between 1 and 500."],
                });
            }

            return Results.Ok(await service.GetRecentOllamaInferenceAsync(
                resolvedHours,
                resolvedLimit,
                machineId,
                model,
                cancellationToken));
        });

        group.MapGet("/ollama/models", async (
            string? machineId,
            bool? loadedOnly,
            LlmUsageQueryService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetOllamaModelsAsync(machineId, loadedOnly ?? false, cancellationToken)));

        // Ollama-specific: proxy a generate call and track the inference metrics
        group.MapPost("/ollama/generate", async (
            OllamaGenerateProxyRequest request,
            OllamaExecutionClient executionClient,
            LlmUsageRepository repository,
            TimeProvider timeProvider,
            IOptions<LlmUsageOptions> llmOptions,
            CancellationToken cancellationToken) =>
        {
            var ollamaMachine = ResolveOllamaMachine(llmOptions.Value.Ollama, request.MachineId);
            if (ollamaMachine is null)
            {
                return Results.NotFound(new { error = "Configured Ollama machine was not found." });
            }

            var ollamaRequest = new
            {
                model = request.Model,
                prompt = request.Prompt,
                stream = false,
            };

            try
            {
                var displayName = string.IsNullOrWhiteSpace(ollamaMachine.DisplayName)
                    ? ollamaMachine.MachineId
                    : ollamaMachine.DisplayName.Trim();
                var execution = await executionClient.GenerateAsync(
                    ollamaMachine.MachineId,
                    displayName,
                    ollamaMachine.Endpoint,
                    request.Model,
                    request.Prompt,
                    cancellationToken);

                var inference = new OllamaInferenceRecord(
                    execution.MachineId,
                    execution.DisplayName,
                    execution.Endpoint,
                    execution.Model,
                    timeProvider.GetUtcNow(),
                    execution.PromptTokens,
                    execution.CompletionTokens,
                    execution.PromptEvalDurationNs,
                    execution.EvalDurationNs,
                    execution.TokensPerSecond,
                    execution.TotalDurationNs);

                await repository.InsertOllamaInferenceAsync(inference, cancellationToken);

                return Results.Ok(new
                {
                    machine_id = execution.MachineId,
                    response = execution.Response,
                    model = execution.Model,
                    metrics = new
                    {
                        prompt_tokens = execution.PromptTokens,
                        completion_tokens = execution.CompletionTokens,
                        tokens_per_second = Math.Round(execution.TokensPerSecond, 1),
                        total_duration_ms = execution.TotalDurationNs / 1_000_000,
                        prompt_eval_ms = execution.PromptEvalDurationNs / 1_000_000,
                        eval_ms = execution.EvalDurationNs / 1_000_000,
                    },
                });
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(502);
            }
        });

        return endpoints;
    }

    private static OllamaMachineOptions? ResolveOllamaMachine(OllamaOptions options, string? machineId)
    {
        var targets = options.ResolveTargets();
        if (targets.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(machineId))
        {
            return targets[0];
        }

        return targets.FirstOrDefault(target =>
            string.Equals(target.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record OllamaGenerateProxyRequest(string Model, string Prompt, string? MachineId = null);
