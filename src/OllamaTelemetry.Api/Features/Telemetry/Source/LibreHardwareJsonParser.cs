using System.Globalization;
using System.Text;
using System.Text.Json;
using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Features.Telemetry.Source;

public sealed class LibreHardwareJsonParser
{
    public IReadOnlyList<ThermalSensorSample> ParseTemperatures(JsonDocument document, SensorFilter filter)
        => Parse(document, filter)
            .Select(static sensor => new ThermalSensorSample(
                sensor.SensorKey,
                sensor.SensorName,
                sensor.SensorPath,
                sensor.TemperatureC,
                sensor.MinTemperatureC,
                sensor.MaxTemperatureC))
            .ToArray();

    public IReadOnlyList<DiscoveredTemperatureSensor> DiscoverTemperatures(JsonDocument document, SensorFilter filter)
        => Parse(document, filter)
            .Select(static sensor => new DiscoveredTemperatureSensor(sensor.SensorKey, sensor.SensorName, sensor.SensorPath))
            .ToArray();

    public ParsedMachineMetrics ParseMetrics(JsonDocument document)
    {
        List<MetricReading> readings = [];
        VisitForMetrics(document.RootElement, null, null, readings);
        return AggregateReadings(readings);
    }

    private static IReadOnlyList<ParsedSensor> Parse(JsonDocument document, SensorFilter filter)
    {
        List<ParsedSensor> sensors = [];
        List<string> path = [];
        Visit(document.RootElement, path, filter, sensors);

        return EnsureUniqueKeys(sensors)
            .OrderBy(static sensor => sensor.SensorPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Visit(
        JsonElement element,
        List<string> path,
        SensorFilter filter,
        List<ParsedSensor> sensors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    Visit(child, path, filter, sensors);
                }

                return;

            case JsonValueKind.Object:
                break;

            default:
                return;
        }

        var nodeText = GetStringProperty(element, "Text") ?? GetStringProperty(element, "Name");
        var pushed = false;

        if (!string.IsNullOrWhiteSpace(nodeText))
        {
            path.Add(nodeText.Trim());
            pushed = true;
        }

        if (TryCreateSensor(element, path, filter, out var sensor))
        {
            sensors.Add(sensor);
        }

        if (element.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Visit(child, path, filter, sensors);
            }
        }

        if (pushed)
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    private static bool TryCreateSensor(
        JsonElement element,
        IReadOnlyList<string> path,
        SensorFilter filter,
        out ParsedSensor sensor)
    {
        sensor = default!;

        var valueText = GetStringProperty(element, "Value");

        if (!TryParseTemperature(valueText, out var temperatureC) || !IsTemperatureNode(element, path))
        {
            return false;
        }

        var sensorPath = string.Join(" / ", path.Where(static segment => !string.Equals(segment, "Temperatures", StringComparison.OrdinalIgnoreCase)));

        if (string.IsNullOrWhiteSpace(sensorPath) || !filter.Matches(sensorPath))
        {
            return false;
        }

        var sensorName = path.Count > 0 ? path[^1] : "Temperature";
        double? minTemperatureC = TryParseTemperature(GetStringProperty(element, "Min"), out var min) ? min : null;
        double? maxTemperatureC = TryParseTemperature(GetStringProperty(element, "Max"), out var max) ? max : null;

        sensor = new ParsedSensor(
            CreateSensorKey(sensorPath),
            sensorName,
            sensorPath,
            temperatureC,
            minTemperatureC,
            maxTemperatureC);

        return true;
    }

    private static bool IsTemperatureNode(JsonElement element, IReadOnlyList<string> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (string.Equals(path[i], "Temperatures", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var sensorType = GetStringProperty(element, "SensorType") ?? GetStringProperty(element, "Type");
        if (!string.IsNullOrWhiteSpace(sensorType)
            && sensorType.Contains("temperature", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var imageUrl = GetStringProperty(element, "ImageURL");
        return !string.IsNullOrWhiteSpace(imageUrl)
            && imageUrl.Contains("temperature", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ParsedSensor> EnsureUniqueKeys(IReadOnlyList<ParsedSensor> sensors)
    {
        Dictionary<string, int> keyCounts = new(StringComparer.OrdinalIgnoreCase);
        List<ParsedSensor> normalized = new(sensors.Count);

        foreach (var sensor in sensors)
        {
            if (!keyCounts.TryGetValue(sensor.SensorKey, out var count))
            {
                keyCounts[sensor.SensorKey] = 1;
                normalized.Add(sensor);
                continue;
            }

            count++;
            keyCounts[sensor.SensorKey] = count;
            normalized.Add(sensor with { SensorKey = $"{sensor.SensorKey}-{count}" });
        }

        return normalized;
    }

    private static bool TryParseTemperature(string? rawValue, out double temperatureC)
    {
        temperatureC = 0;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        Span<char> buffer = stackalloc char[rawValue.Length];
        var length = 0;
        var seenDigit = false;

        foreach (var character in rawValue)
        {
            if (char.IsDigit(character))
            {
                buffer[length++] = character;
                seenDigit = true;
                continue;
            }

            if (character is '+' or '-' or '.' or ',')
            {
                buffer[length++] = character == ',' ? '.' : character;
                continue;
            }

            if (seenDigit)
            {
                break;
            }
        }

        return seenDigit && double.TryParse(buffer[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out temperatureC);
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static string CreateSensorKey(string value)
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

        return builder.Length == 0 ? "temperature" : builder.ToString();
    }

    // ── Metrics extraction ──────────────────────────────────────────────

    private static void VisitForMetrics(
        JsonElement element,
        HardwareContext? currentHardware,
        SensorCategory? currentCategory,
        List<MetricReading> readings)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                VisitForMetrics(child, currentHardware, currentCategory, readings);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var nodeText = (GetStringProperty(element, "Text") ?? GetStringProperty(element, "Name") ?? "").Trim();
        var imageUrl = GetStringProperty(element, "ImageURL") ?? "";

        var detectedHardware = DetectHardwareType(nodeText, imageUrl);
        var hardwareCtx = detectedHardware.HasValue
            ? new HardwareContext(detectedHardware.Value, nodeText)
            : currentHardware;

        var detectedCategory = InferSensorCategory(nodeText);
        var categoryCtx = detectedCategory ?? currentCategory;

        if (hardwareCtx is not null && categoryCtx.HasValue)
        {
            var valueText = GetStringProperty(element, "Value");
            if (TryParseNumericValue(valueText, out var value))
            {
                readings.Add(new MetricReading(hardwareCtx.Type, hardwareCtx.Name, categoryCtx.Value, nodeText, value));
            }
        }

        if (element.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                VisitForMetrics(child, hardwareCtx, detectedCategory.HasValue ? categoryCtx : null, readings);
            }
        }
    }

    private static HardwareType? DetectHardwareType(string nodeText, string imageUrl)
    {
        if (imageUrl.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || imageUrl.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("RTX", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("GTX", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareType.Gpu;
        }

        if (imageUrl.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("Xeon", StringComparison.OrdinalIgnoreCase)
            || nodeText.Contains("Core i", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareType.Cpu;
        }

        if (imageUrl.Contains("ram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeText, "Generic Memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeText, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareType.Memory;
        }

        return null;
    }

    private static SensorCategory? InferSensorCategory(string nodeText)
        => nodeText switch
        {
            _ when string.Equals(nodeText, "Temperatures", StringComparison.OrdinalIgnoreCase) => SensorCategory.Temperature,
            _ when string.Equals(nodeText, "Load", StringComparison.OrdinalIgnoreCase) => SensorCategory.Load,
            _ when string.Equals(nodeText, "Data", StringComparison.OrdinalIgnoreCase) => SensorCategory.Data,
            _ when string.Equals(nodeText, "Powers", StringComparison.OrdinalIgnoreCase) => SensorCategory.Power,
            _ => null,
        };

    private static bool TryParseNumericValue(string? rawValue, out double value)
        => TryParseTemperature(rawValue, out value);

    private static ParsedMachineMetrics AggregateReadings(List<MetricReading> readings)
    {
        var gpuGroups = readings
            .Where(static r => r.HardwareType == HardwareType.Gpu)
            .GroupBy(static r => r.HardwareName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cpuReadings = readings.Where(static r => r.HardwareType == HardwareType.Cpu).ToList();
        var memoryReadings = readings.Where(static r => r.HardwareType == HardwareType.Memory).ToList();

        List<GpuMetrics> gpus = [];
        var gpuIndex = 0;

        foreach (var gpuGroup in gpuGroups)
        {
            var load = gpuGroup.Where(static r => r.Category == SensorCategory.Load);
            var data = gpuGroup.Where(static r => r.Category == SensorCategory.Data);
            var temp = gpuGroup.Where(static r => r.Category == SensorCategory.Temperature);
            var power = gpuGroup.Where(static r => r.Category == SensorCategory.Power);

            gpus.Add(new GpuMetrics(
                gpuIndex++,
                gpuGroup.Key,
                FindReading(load, "GPU Core", "GPU"),
                ConvertGBToBytes(FindReading(data, "GPU Memory Used", "D3D Dedicated Memory Used")),
                ConvertGBToBytes(FindReading(data, "GPU Memory Total", "D3D Dedicated Memory Total")),
                FindReading(temp, "GPU Core", "GPU"),
                FindReading(power, "GPU Power", "Power")));
        }

        CpuMetrics? cpu = cpuReadings.Count > 0
            ? new CpuMetrics(
                FindReading(cpuReadings.Where(static r => r.Category == SensorCategory.Load), "CPU Total", "Total"),
                FindReading(cpuReadings.Where(static r => r.Category == SensorCategory.Temperature), "CPU Package", "Package"),
                FindReading(cpuReadings.Where(static r => r.Category == SensorCategory.Power), "CPU Package", "Package"))
            : null;

        MemoryMetrics? memory = null;
        if (memoryReadings.Count > 0)
        {
            var memData = memoryReadings.Where(static r => r.Category == SensorCategory.Data);
            var usedGb = FindReading(memData, "Memory Used", "Used");
            var availableGb = FindReading(memData, "Memory Available", "Available");
            var usedBytes = ConvertGBToBytes(usedGb);
            var totalBytes = usedGb.HasValue && availableGb.HasValue
                ? ConvertGBToBytes(usedGb.Value + availableGb.Value)
                : null;

            memory = new MemoryMetrics(usedBytes, totalBytes);
        }

        return new ParsedMachineMetrics(gpus, cpu, memory);
    }

    private static double? FindReading(IEnumerable<MetricReading> readings, params string[] namePatterns)
    {
        foreach (var pattern in namePatterns)
        {
            var reading = readings.FirstOrDefault(r =>
                r.SensorName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (reading is not null)
            {
                return reading.Value;
            }
        }

        return null;
    }

    private static long? ConvertGBToBytes(double? gb)
        => gb.HasValue ? (long)(gb.Value * 1024 * 1024 * 1024) : null;

    // ── Internal types ──────────────────────────────────────────────────

    private sealed record ParsedSensor(
        string SensorKey,
        string SensorName,
        string SensorPath,
        double TemperatureC,
        double? MinTemperatureC,
        double? MaxTemperatureC);

    private enum HardwareType { Gpu, Cpu, Memory }

    private enum SensorCategory { Temperature, Load, Data, Power }

    private sealed record HardwareContext(HardwareType Type, string Name);

    private sealed record MetricReading(
        HardwareType HardwareType,
        string HardwareName,
        SensorCategory Category,
        string SensorName,
        double Value);

    public sealed record ParsedMachineMetrics(
        IReadOnlyList<GpuMetrics> Gpus,
        CpuMetrics? Cpu,
        MemoryMetrics? Memory);
}
