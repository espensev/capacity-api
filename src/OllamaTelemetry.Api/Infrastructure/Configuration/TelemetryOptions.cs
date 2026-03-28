using System.ComponentModel.DataAnnotations;

namespace OllamaTelemetry.Api.Infrastructure.Configuration;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    [Range(0, 3600)]
    public int RefreshAfterSeconds { get; set; } = 5;

    [Range(5, 86400)]
    public int StaleAfterSeconds { get; set; } = 30;

    [Required]
    public StorageOptions Storage { get; set; } = new();

    [Required]
    public PersistenceOptions Persistence { get; set; } = new();

    [Required]
    public SourceOptions Source { get; set; } = new();

    [MinLength(1)]
    public List<MachineTelemetryTargetOptions> Machines { get; set; } = [];
}

public sealed class StorageOptions
{
    [Required]
    public string ConnectionString { get; set; } = "Data Source=data/telemetry.db;Mode=ReadWriteCreate;Cache=Shared;Pooling=True";

    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 14;

    [Range(1, 1440)]
    public int CleanupIntervalMinutes { get; set; } = 60;
}

public sealed class PersistenceOptions
{
    [Range(0, 50)]
    public double MinimumDeltaCelsius { get; set; } = 0.5;

    [Range(1, 86400)]
    public int ForceWriteIntervalSeconds { get; set; } = 60;
}

public sealed class SourceOptions
{
    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; set; } = 4;
}

public sealed class MachineTelemetryTargetOptions
{
    [Required]
    public string MachineId { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string SourceType { get; set; } = "LibreHardwareMonitor";

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public SensorFilterOptions Sensors { get; set; } = new();
}

public sealed class SensorFilterOptions
{
    public List<string> IncludeKeywords { get; set; } = [];

    public List<string> ExcludeKeywords { get; set; } = [];
}
