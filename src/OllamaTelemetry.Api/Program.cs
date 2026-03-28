using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Evaluation.Api;
using OllamaTelemetry.Api.Features.Evaluation.Storage;
using OllamaTelemetry.Api.Features.LlmUsage.Api;
using OllamaTelemetry.Api.Features.LlmUsage.Collector;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Features.Telemetry.Api;
using OllamaTelemetry.Api.Features.Telemetry.Collector;
using OllamaTelemetry.Api.Features.Telemetry.Source;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;
using OllamaTelemetry.Api.Infrastructure.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddCheck<TelemetryCollectorHealthCheck>("telemetry_refresh");

builder.Services.AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MachineTelemetryRegistry>();
builder.Services.AddSingleton<LibreHardwareJsonParser>();
builder.Services.AddSingleton<IMachineMetricsSource, LibreHardwareMonitorTelemetrySource>();
builder.Services.AddSingleton<IMachineMetricsSource, NvmlMetricsSource>();
builder.Services.AddSingleton<IMachineMetricsSource, HostAgentMetricsSource>();
builder.Services.AddSingleton<TelemetrySourceResolver>();
builder.Services.AddSingleton<LatestTelemetryCache>();
builder.Services.AddSingleton<ReadingPersistencePolicy>();
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<TelemetryDatabaseInitializer>();
builder.Services.AddSingleton<TelemetryRepository>();
builder.Services.AddSingleton<TelemetryRefreshService>();
builder.Services.AddSingleton<TelemetryQueryService>();

builder.Services.AddHttpClient<LibreHardwareMonitorTelemetrySource>(static client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaTelemetry/1.0");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<HostAgentMetricsSource>(static client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaTelemetry/1.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ── LLM Usage tracking ────────────────────��────────────────────────
builder.Services.AddOptions<LlmUsageOptions>()
    .Bind(builder.Configuration.GetSection(LlmUsageOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LlmUsageOptions>, LlmUsageOptionsValidator>();

builder.Services.AddSingleton<LlmUsageDatabaseInitializer>();
builder.Services.AddSingleton<LlmUsageRepository>();
builder.Services.AddSingleton<LlmUsageQueryService>();
builder.Services.AddSingleton<OllamaStatusCache>();
builder.Services.AddSingleton<EvaluationDatabaseInitializer>();
builder.Services.AddSingleton<EvaluationRepository>();
builder.Services.AddSingleton<EvaluationService>();
builder.Services.AddHttpClient<OllamaExecutionClient>((serviceProvider, client) =>
{
    var llmOptions = serviceProvider.GetRequiredService<IOptions<LlmUsageOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(llmOptions.Ollama.GenerateTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaTelemetry/1.0");
});

builder.Services.AddHttpClient<OllamaCollectorService>(static client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaTelemetry/1.0");
});
builder.Services.AddHostedService<OllamaCollectorService>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health");

app.MapGet("/", () => TypedResults.Ok(new
{
    service = "ollama-telemetry",
    endpoints = new[]
    {
        "/api/telemetry/overview",
        "/api/telemetry/machines/{machineId}/latest",
        "/api/telemetry/machines/{machineId}/history/{sensorKey}",
        "/api/telemetry/machines/{machineId}/discovery",
        "/api/machines/{machineId}/capacity",
        "/api/machines/best-fit",
        "/api/llm/overview",
        "/api/llm/recent",
        "/api/llm/sessions/{sessionId}",
        "/api/llm/ollama/models",
        "/api/llm/ollama/inference",
        "/api/llm/ingest",
        "/api/llm/ollama/generate",
        "/api/evals/catalog/models",
        "/api/evals/runs",
        "/api/evals/runs/{runId}",
        "/api/evals/runs/{runId}/cases",
        "/api/evals/runs/{runId}/cases/{caseId}/execute",
        "/api/evals/runs/{runId}/cases/{caseId}/results/{candidateId}/judgment",
        "/health",
    },
}));

app.MapTelemetryEndpoints();
app.MapCapacityEndpoints();
app.MapLlmUsageEndpoints();
app.MapEvaluationEndpoints();

await app.Services.GetRequiredService<TelemetryDatabaseInitializer>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<LlmUsageDatabaseInitializer>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<EvaluationDatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.Run();

public partial class Program;
