using System.ComponentModel.DataAnnotations;

namespace OllamaTelemetry.Api.Infrastructure.Configuration;

public sealed class LlmUsageOptions
{
    public const string SectionName = "LlmUsage";

    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    [Required]
    public OllamaOptions Ollama { get; set; } = new();

    [Required]
    public ClaudeCostOptions Claude { get; set; } = new();
}

public sealed class OllamaOptions
{
    public bool Enabled { get; set; } = true;

    [Required]
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string MachineId { get; set; } = "ollama";

    public string? DisplayName { get; set; }

    public List<OllamaMachineOptions> Machines { get; set; } = [];

    [Range(5, 3600)]
    public int PollIntervalSeconds { get; set; } = 30;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; set; } = 10;

    [Range(30, 600)]
    public int GenerateTimeoutSeconds { get; set; } = 300;

    public IReadOnlyList<OllamaMachineOptions> ResolveTargets()
    {
        if (!Enabled)
        {
            return [];
        }

        if (Machines.Count == 0)
        {
            var machineId = NormalizeMachineId(MachineId);
            return
            [
                new OllamaMachineOptions
                {
                    Enabled = true,
                    MachineId = machineId,
                    DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? machineId : DisplayName.Trim(),
                    Endpoint = Endpoint.Trim(),
                },
            ];
        }

        return Machines
            .Where(static machine => machine.Enabled)
            .Select(static machine =>
            {
                var machineId = NormalizeMachineId(machine.MachineId);
                return new OllamaMachineOptions
                {
                    Enabled = true,
                    MachineId = machineId,
                    DisplayName = string.IsNullOrWhiteSpace(machine.DisplayName) ? machineId : machine.DisplayName.Trim(),
                    Endpoint = machine.Endpoint.Trim(),
                };
            })
            .ToArray();
    }

    private static string NormalizeMachineId(string? machineId)
        => string.IsNullOrWhiteSpace(machineId) ? "ollama" : machineId.Trim();
}

public sealed class OllamaMachineOptions
{
    public bool Enabled { get; set; } = true;

    [Required]
    public string MachineId { get; set; } = "ollama";

    public string? DisplayName { get; set; }

    [Required]
    public string Endpoint { get; set; } = "http://localhost:11434";
}

public sealed class ClaudeCostOptions
{
    public double HaikuInputPer1M { get; set; } = 0.80;
    public double HaikuOutputPer1M { get; set; } = 4.00;
    public double SonnetInputPer1M { get; set; } = 3.00;
    public double SonnetOutputPer1M { get; set; } = 15.00;
    public double OpusInputPer1M { get; set; } = 15.00;
    public double OpusOutputPer1M { get; set; } = 75.00;
}
