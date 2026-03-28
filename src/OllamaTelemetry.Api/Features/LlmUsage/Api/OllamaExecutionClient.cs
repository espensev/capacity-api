using System.Net.Http.Json;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;

namespace OllamaTelemetry.Api.Features.LlmUsage.Api;

public sealed class OllamaExecutionClient(HttpClient httpClient)
{
    public async Task<OllamaGenerateExecution> GenerateAsync(
        string machineId,
        string displayName,
        string endpoint,
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            model,
            prompt,
            stream = false,
        };

        using var response = await httpClient.PostAsJsonAsync(
            $"{endpoint.TrimEnd('/')}/api/generate",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponsePayload>(cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException("Empty response from Ollama.");
        }

        var tokensPerSecond = result.Eval_duration > 0
            ? result.Eval_count / (result.Eval_duration / 1_000_000_000.0)
            : 0;

        return new OllamaGenerateExecution(
            machineId,
            displayName,
            endpoint,
            result.Model,
            result.Response,
            result.Prompt_eval_count,
            result.Eval_count,
            result.Prompt_eval_duration,
            result.Eval_duration,
            result.Total_duration,
            tokensPerSecond);
    }
}

public sealed record OllamaGenerateExecution(
    string MachineId,
    string DisplayName,
    string Endpoint,
    string Model,
    string Response,
    int PromptTokens,
    int CompletionTokens,
    long PromptEvalDurationNs,
    long EvalDurationNs,
    long TotalDurationNs,
    double TokensPerSecond);

internal sealed class OllamaGenerateResponsePayload
{
    public string Model { get; set; } = "";
    public string Response { get; set; } = "";
    public int Prompt_eval_count { get; set; }
    public long Prompt_eval_duration { get; set; }
    public int Eval_count { get; set; }
    public long Eval_duration { get; set; }
    public long Total_duration { get; set; }
}
