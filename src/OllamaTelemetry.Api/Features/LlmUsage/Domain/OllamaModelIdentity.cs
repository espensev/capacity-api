using System.Text;

namespace OllamaTelemetry.Api.Features.LlmUsage.Domain;

public sealed record OllamaModelIdentity(
    string Provider,
    string MachineModelId,
    string CanonicalModelId,
    string DisplayLabel,
    string ShortLabel,
    string Family,
    string FamilySlug,
    string? Tag,
    string ParameterSize,
    string QuantizationLevel);

public static class OllamaModelIdentityParser
{
    public static OllamaModelIdentity Parse(OllamaModelSnapshot snapshot)
    {
        var family = NormalizeValue(snapshot.Family, InferFamily(snapshot.ModelName));
        var familySlug = ToSlug(family);
        var tag = ExtractTag(snapshot.ModelName);
        var parameterSize = NormalizeValue(snapshot.ParameterSize, "unknown");
        var quantization = NormalizeValue(snapshot.QuantizationLevel, "unknown");

        var canonicalModelId = string.Join("/",
        [
            "ollama",
            familySlug,
            ToSlug(parameterSize),
            ToSlug(quantization),
            ToSlug(tag ?? "default"),
        ]);

        var machineModelId = string.Join("/",
        [
            "ollama",
            ToSlug(snapshot.MachineId),
            ToSlug(snapshot.ModelName),
        ]);

        var shortLabel = $"{family} {parameterSize} {quantization}".Trim();
        var displayLabel = $"{snapshot.DisplayName} | {snapshot.ModelName} | {shortLabel}";

        return new OllamaModelIdentity(
            "ollama",
            machineModelId,
            canonicalModelId,
            displayLabel,
            shortLabel,
            family,
            familySlug,
            tag,
            parameterSize,
            quantization);
    }

    private static string NormalizeValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value.Trim();

    private static string InferFamily(string modelName)
    {
        var name = modelName.Split(':', 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static string? ExtractTag(string modelName)
    {
        var parts = modelName.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
    }

    private static string ToSlug(string value)
    {
        StringBuilder builder = new(value.Length);
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
                continue;
            }

            pendingSeparator = true;
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}
