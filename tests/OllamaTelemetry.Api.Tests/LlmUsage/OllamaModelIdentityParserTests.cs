using OllamaTelemetry.Api.Features.LlmUsage.Domain;

namespace OllamaTelemetry.Api.Tests.LlmUsage;

public sealed class OllamaModelIdentityParserTests
{
    [Fact]
    public void Parse_BuildsStableHumanAndAgentFacingIds()
    {
        var snapshot = new OllamaModelSnapshot(
            "snd-host",
            "SND-HOST",
            "http://192.168.2.5:11434",
            "llama3.1:8b",
            "llama",
            "8B",
            "Q4_K_M",
            4L * 1024 * 1024 * 1024,
            6L * 1024 * 1024 * 1024,
            8192,
            new DateTimeOffset(2026, 3, 28, 18, 0, 0, TimeSpan.Zero),
            true);

        var identity = OllamaModelIdentityParser.Parse(snapshot);

        Assert.Equal("ollama", identity.Provider);
        Assert.Equal("ollama/snd-host/llama3-1-8b", identity.MachineModelId);
        Assert.Equal("ollama/llama/8b/q4-k-m/8b", identity.CanonicalModelId);
        Assert.Equal("llama", identity.FamilySlug);
        Assert.Equal("8b", identity.Tag);
        Assert.Equal("llama 8B Q4_K_M", identity.ShortLabel);
        Assert.Equal("SND-HOST | llama3.1:8b | llama 8B Q4_K_M", identity.DisplayLabel);
    }
}
