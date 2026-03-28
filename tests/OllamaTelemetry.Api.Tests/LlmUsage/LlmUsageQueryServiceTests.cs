using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaTelemetry.Api.Features.LlmUsage.Api;
using OllamaTelemetry.Api.Features.LlmUsage.Collector;
using OllamaTelemetry.Api.Features.LlmUsage.Domain;
using OllamaTelemetry.Api.Features.LlmUsage.Storage;
using OllamaTelemetry.Api.Features.Telemetry.Storage;
using OllamaTelemetry.Api.Infrastructure.Configuration;

namespace OllamaTelemetry.Api.Tests.LlmUsage;

public sealed class LlmUsageQueryServiceTests
{
    [Fact]
    public async Task GetOverviewAsync_ReturnsMachineAwareOllamaFleetAndProviderSummaries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var connectionFactory = CreateConnectionFactory(tempRoot);
            await new LlmUsageDatabaseInitializer(connectionFactory).InitializeAsync(CancellationToken.None);

            var repository = new LlmUsageRepository(connectionFactory);
            var recordedAt = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

            await repository.InsertUsageRecordAsync(new LlmUsageRecord(
                "claude",
                "session_end",
                "sess-1",
                "claude-sonnet-4-6",
                recordedAt,
                "maindesk",
                "claude-code",
                1000,
                500,
                0,
                0,
                0.003,
                0.007,
                0.010,
                3,
                120_000,
                null,
                0,
                0,
                false,
                "completed",
                null,
                null,
                null), CancellationToken.None);

            var statusCache = new OllamaStatusCache();
            statusCache.Update(
                "maindesk",
                "MAINDESK",
                "http://127.0.0.1:11434",
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
                        recordedAt,
                        true),
                ],
                recordedAt);
            statusCache.MarkUnreachable(
                "snd-host",
                "SND-HOST",
                "http://192.168.2.5:11434",
                recordedAt,
                "connection refused");

            var service = new LlmUsageQueryService(repository, statusCache, new FixedTimeProvider(recordedAt.AddMinutes(30)));

            var overview = await service.GetOverviewAsync(24, CancellationToken.None);

            Assert.Equal(2, overview.Ollama.MachineCount);
            Assert.Equal(1, overview.Ollama.ReachableMachineCount);

            var machineStates = overview.Ollama.Machines.OrderBy(static m => m.MachineId, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal("maindesk", machineStates[0].MachineId);
            Assert.True(machineStates[0].IsReachable);
            Assert.Equal(1, machineStates[0].LoadedModelCount);
            Assert.Equal("snd-host", machineStates[1].MachineId);
            Assert.False(machineStates[1].IsReachable);

            var model = Assert.Single(overview.Ollama.Models);
            Assert.Equal("maindesk", model.MachineId);
            Assert.Equal("llama3.1:8b", model.ModelName);

            var provider = Assert.Single(overview.Providers);
            Assert.Equal("claude", provider.Provider);
            Assert.Equal("claude-code", provider.AssistantKind);
            Assert.Equal("maindesk", provider.MachineId);
            Assert.Equal("claude-sonnet-4-6", provider.Model);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task GetRecentOllamaInferenceAsync_FiltersByMachineAndModel()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var connectionFactory = CreateConnectionFactory(tempRoot);
            await new LlmUsageDatabaseInitializer(connectionFactory).InitializeAsync(CancellationToken.None);

            var repository = new LlmUsageRepository(connectionFactory);
            var now = new DateTimeOffset(2026, 3, 28, 15, 0, 0, TimeSpan.Zero);

            await repository.InsertOllamaInferenceAsync(new OllamaInferenceRecord(
                "maindesk",
                "MAINDESK",
                "http://127.0.0.1:11434",
                "llama3.1:8b",
                now.AddMinutes(-10),
                120,
                64,
                2_000_000,
                8_000_000,
                32.5,
                12_000_000), CancellationToken.None);

            await repository.InsertOllamaInferenceAsync(new OllamaInferenceRecord(
                "maindesk",
                "MAINDESK",
                "http://127.0.0.1:11434",
                "mistral:7b",
                now.AddMinutes(-9),
                100,
                40,
                1_000_000,
                7_000_000,
                24.0,
                9_000_000), CancellationToken.None);

            await repository.InsertOllamaInferenceAsync(new OllamaInferenceRecord(
                "snd-host",
                "SND-HOST",
                "http://192.168.2.5:11434",
                "llama3.1:8b",
                now.AddMinutes(-8),
                90,
                30,
                1_000_000,
                6_000_000,
                18.0,
                8_000_000), CancellationToken.None);

            var service = new LlmUsageQueryService(repository, new OllamaStatusCache(), new FixedTimeProvider(now));

            var records = await service.GetRecentOllamaInferenceAsync(
                24,
                10,
                "maindesk",
                "llama3.1:8b",
                CancellationToken.None);

            var record = Assert.Single(records);
            Assert.Equal("maindesk", record.MachineId);
            Assert.Equal("llama3.1:8b", record.ModelName);
            Assert.Equal(12, record.TotalDurationMs);
            Assert.Equal(2, record.PromptEvalDurationMs);
            Assert.Equal(8, record.EvalDurationMs);
            Assert.Equal(32.5, record.TokensPerSecond);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task InsertUsageRecordAsync_UpsertsBySourceEventId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ollama-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var connectionFactory = CreateConnectionFactory(tempRoot);
            await new LlmUsageDatabaseInitializer(connectionFactory).InitializeAsync(CancellationToken.None);

            var repository = new LlmUsageRepository(connectionFactory);
            var startedAt = new DateTimeOffset(2026, 3, 28, 16, 0, 0, TimeSpan.Zero);

            await repository.InsertUsageRecordAsync(new LlmUsageRecord(
                "codex",
                "session_stop",
                "codex-session-1",
                "gpt-5.4",
                startedAt,
                "maindesk",
                "codex-cli",
                1200,
                300,
                200,
                0,
                0,
                0,
                0,
                2,
                60_000,
                null,
                0,
                0,
                false,
                null,
                @"D:\Development\Ai-managment\ollama-telemetry",
                "approval=on-request;sandbox=workspace-write",
                "codex-session-stop:codex-session-1"), CancellationToken.None);

            await repository.InsertUsageRecordAsync(new LlmUsageRecord(
                "codex",
                "session_stop",
                "codex-session-1",
                "gpt-5.4",
                startedAt.AddMinutes(5),
                "maindesk",
                "codex-cli",
                1800,
                450,
                320,
                0,
                0,
                0,
                0,
                3,
                120_000,
                null,
                0,
                0,
                false,
                null,
                @"D:\Development\Ai-managment\ollama-telemetry",
                "approval=on-request;sandbox=workspace-write",
                "codex-session-stop:codex-session-1"), CancellationToken.None);

            var records = await repository.GetSessionRecordsAsync("codex-session-1", CancellationToken.None);
            var record = Assert.Single(records);

            Assert.Equal(1800, record.InputTokens);
            Assert.Equal(450, record.OutputTokens);
            Assert.Equal(320, record.CacheReadTokens);
            Assert.Equal(3, record.NumTurns);
            Assert.Equal(120_000, record.DurationMs);
            Assert.Equal("codex-session-stop:codex-session-1", record.SourceEventId);
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
