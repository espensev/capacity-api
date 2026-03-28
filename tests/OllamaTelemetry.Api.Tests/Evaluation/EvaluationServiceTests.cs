using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.Evaluation.Api;
using OllamaTelemetry.Api.Features.Evaluation.Contracts;
using OllamaTelemetry.Api.Features.Evaluation.Storage;
using OllamaTelemetry.Api.Features.LlmUsage.Api;
using OllamaTelemetry.Api.Features.LlmUsage.Collector;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.Evaluation;

public sealed class EvaluationServiceTests
{
    [Fact]
    public async Task CreateRunAndExecuteCase_PersistsStructuredComparisonResults()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var connectionFactory = CreateConnectionFactory(tempRoot);
            await new LlmUsageDatabaseInitializer(connectionFactory).InitializeAsync(CancellationToken.None);
            await new EvaluationDatabaseInitializer(connectionFactory).InitializeAsync(CancellationToken.None);

            var llmRepository = new LlmUsageRepository(connectionFactory);
            var evaluationRepository = new EvaluationRepository(connectionFactory);
            var now = new DateTimeOffset(2026, 3, 28, 19, 0, 0, TimeSpan.Zero);

            await llmRepository.InsertOllamaSnapshotBatchAsync(
            [
                new OllamaModelSnapshot(
                    "maindesk",
                    "MAINDESK",
                    "http://127.0.0.1:11434",
                    "llama3.1:8b",
                    "llama",
                    "8B",
                    "Q4_K_M",
                    4L * 1024 * 1024 * 1024,
                    6L * 1024 * 1024 * 1024,
                    8192,
                    now,
                    true),
                new OllamaModelSnapshot(
                    "snd-host",
                    "SND-HOST",
                    "http://192.168.2.5:11434",
                    "mistral:7b",
                    "mistral",
                    "7B",
                    "Q4_K_M",
                    3L * 1024 * 1024 * 1024,
                    5L * 1024 * 1024 * 1024,
                    8192,
                    now,
                    true),
            ], CancellationToken.None);

            var llmQueryService = new LlmUsageQueryService(llmRepository, new OllamaStatusCache(), new FixedTimeProvider(now));
            var executionClient = new OllamaExecutionClient(new HttpClient(new StubHttpMessageHandler()));
            var service = new EvaluationService(
                evaluationRepository,
                llmRepository,
                llmQueryService,
                executionClient,
                new FixedTimeProvider(now));

            var run = await service.CreateRunAsync(new CreateEvaluationRunRequest(
                "Two-model smoke test",
                "codex",
                "Initial assistant evaluation",
                [
                    new CreateEvaluationCandidateRequest("maindesk", "llama3.1:8b"),
                    new CreateEvaluationCandidateRequest("snd-host", "mistral:7b"),
                ]), CancellationToken.None);

            var testCase = await service.AddCaseAsync(
                run.RunId,
                new CreateEvaluationCaseRequest("hello-world", "Hello", "Say hello in one sentence.", null),
                CancellationToken.None);

            Assert.NotNull(testCase);

            var executed = await service.ExecuteCaseAsync(
                run.RunId,
                testCase!.CaseId,
                new ExecuteEvaluationCaseRequest("codex", true),
                CancellationToken.None);

            Assert.NotNull(executed);
            Assert.Equal(2, executed!.Results.Count);
            Assert.Contains(executed.Results, result => result.MachineId == "maindesk" && result.ResponseText == "Desk answer");
            Assert.Contains(executed.Results, result => result.MachineId == "snd-host" && result.ResponseText == "Host answer");

            var judged = await service.RecordJudgmentAsync(
                run.RunId,
                testCase.CaseId,
                run.Candidates[0].CandidateId,
                new RecordEvaluationJudgmentRequest("claude", 0.9, "preferred", "Cleaner answer"),
                CancellationToken.None);

            Assert.NotNull(judged);
            Assert.Equal("preferred", judged!.Verdict);
            Assert.Equal("claude", judged.JudgedBy);

            var detail = await service.GetRunDetailAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(2, detail!.Candidates.Count);
            Assert.Single(detail.Cases);
            Assert.Equal(2, detail.Cases[0].Results.Count);
            Assert.All(detail.Candidates, candidate => Assert.StartsWith("ollama/", candidate.CanonicalModelId, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static SqliteConnectionFactory CreateConnectionFactory(string tempRoot)
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

        return new SqliteConnectionFactory(options, environment);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.Host switch
            {
                "127.0.0.1" => """
                {"model":"llama3.1:8b","response":"Desk answer","prompt_eval_count":12,"prompt_eval_duration":2000000,"eval_count":24,"eval_duration":8000000,"total_duration":11000000}
                """,
                _ => """
                {"model":"mistral:7b","response":"Host answer","prompt_eval_count":10,"prompt_eval_duration":1500000,"eval_count":20,"eval_duration":7000000,"total_duration":9500000}
                """,
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
