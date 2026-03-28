using System.Diagnostics;
using System.Runtime.InteropServices;
using OllamaTelemetry.Api.Features.Telemetry.Domain;

namespace OllamaTelemetry.Api.Features.Telemetry.Source;

/// <summary>
/// GPU metrics source that loads nvml.dll at runtime — the same DLL that ships
/// with the NVIDIA driver and is used by LibreHardwareMonitor, GPU-Z, and HWiNFO.
///
/// Mirrors the runtime-loading pattern from Gpu-sev/nvapi_controller/src/nvml_power.h
/// but adds utilization rates, memory info, and temperature for capacity monitoring.
///
/// If nvml.dll is not present (non-NVIDIA system, driver not installed), the source
/// returns empty GPU lists — failure is non-fatal.
///
/// For high-precision thermals (0.004 °C @ 200 Hz via undocumented NVAPI), see
/// Gpu-sev/nvapi_controller — that level of precision is for fan control loops,
/// not capacity monitoring.
/// </summary>
public sealed class NvmlMetricsSource : IMachineMetricsSource, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NvmlMetricsSource> _logger;
    private readonly object _initLock = new();
    private NvmlState? _state;
    private bool _initAttempted;

    public NvmlMetricsSource(TimeProvider timeProvider, ILogger<NvmlMetricsSource> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string SourceType => "Nvml";

    public Task<MachineCapacitySnapshot> CollectAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        EnsureInitialized();

        var gpus = _state is not null
            ? ReadAllGpus(_state)
            : [];

        var thermals = BuildThermalSensors(gpus);

        var snapshot = new MachineCapacitySnapshot(
            target.MachineId,
            target.DisplayName,
            SourceType,
            target.Endpoint,
            startedAtUtc,
            (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue),
            gpus,
            null,
            null,
            thermals,
            []);

        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<DiscoveredTemperatureSensor>> DiscoverAsync(MachineTelemetryTarget target, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DiscoveredTemperatureSensor>>([]);

    public void Dispose()
    {
        if (_state is not null)
        {
            try { _state.Shutdown?.Invoke(); }
            catch { /* best effort */ }

            NativeLibrary.Free(_state.DllHandle);
            _state = null;
        }
    }

    // ── Initialization (mirrors NvmlPower::init from nvml_power.h) ───────

    private void EnsureInitialized()
    {
        if (_initAttempted) return;
        lock (_initLock)
        {
            if (_initAttempted) return;
            _initAttempted = true;

            try
            {
                _state = TryLoadNvml();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NVML initialization failed — GPU metrics will be unavailable.");
            }
        }
    }

    private NvmlState? TryLoadNvml()
    {
        // nvml.dll ships with the NVIDIA driver. Try the standard locations.
        // NativeLibrary.TryLoad searches System32, PATH, and the NVSMI folder.
        if (!NativeLibrary.TryLoad("nvml", out var dll))
        {
            _logger.LogInformation("nvml.dll not found — NVML GPU metrics unavailable.");
            return null;
        }

        var init = GetDelegate<NvmlInitDelegate>(dll, "nvmlInit_v2");
        var shutdown = GetDelegate<NvmlShutdownDelegate>(dll, "nvmlShutdown");
        var getCount = GetDelegate<NvmlDeviceGetCountDelegate>(dll, "nvmlDeviceGetCount_v2");
        var getHandle = GetDelegate<NvmlDeviceGetHandleByIndexDelegate>(dll, "nvmlDeviceGetHandleByIndex_v2");

        if (init is null || shutdown is null || getCount is null || getHandle is null)
        {
            _logger.LogWarning("nvml.dll loaded but core function exports missing — NVML unavailable.");
            NativeLibrary.Free(dll);
            return null;
        }

        var rc = init();
        if (rc != NvmlSuccess)
        {
            _logger.LogWarning("nvmlInit_v2 returned {ErrorCode} — NVML unavailable.", rc);
            NativeLibrary.Free(dll);
            return null;
        }

        rc = getCount(out var count);
        if (rc != NvmlSuccess || count == 0)
        {
            _logger.LogInformation("NVML reports {DeviceCount} GPUs (rc={Rc}).", count, rc);
            shutdown();
            NativeLibrary.Free(dll);
            return null;
        }

        var devices = new nint[count];
        for (uint i = 0; i < count; i++)
        {
            rc = getHandle(i, out devices[i]);
            if (rc != NvmlSuccess)
            {
                _logger.LogWarning("nvmlDeviceGetHandleByIndex_v2({Index}) failed with {Rc}.", i, rc);
                devices[i] = nint.Zero;
            }
        }

        var state = new NvmlState
        {
            DllHandle = dll,
            Shutdown = shutdown,
            Devices = devices,
            GetName = GetDelegate<NvmlDeviceGetNameDelegate>(dll, "nvmlDeviceGetName"),
            GetPowerUsage = GetDelegate<NvmlDeviceGetPowerUsageDelegate>(dll, "nvmlDeviceGetPowerUsage"),
            GetTemperature = GetDelegate<NvmlDeviceGetTemperatureDelegate>(dll, "nvmlDeviceGetTemperature"),
            GetUtilizationRates = GetDelegate<NvmlDeviceGetUtilizationRatesDelegate>(dll, "nvmlDeviceGetUtilizationRates"),
            GetMemoryInfo = GetDelegate<NvmlDeviceGetMemoryInfoDelegate>(dll, "nvmlDeviceGetMemoryInfo"),
        };

        _logger.LogInformation("NVML initialized — {Count} GPU(s) discovered.", count);
        return state;
    }

    // ── Per-GPU read (mirrors NvmlPower::read_power_mw + additions) ──────

    private List<GpuMetrics> ReadAllGpus(NvmlState state)
    {
        var gpus = new List<GpuMetrics>(state.Devices.Length);

        for (var i = 0; i < state.Devices.Length; i++)
        {
            var device = state.Devices[i];
            if (device == nint.Zero) continue;

            gpus.Add(ReadSingleGpu(state, device, i));
        }

        return gpus;
    }

    private GpuMetrics ReadSingleGpu(NvmlState state, nint device, int index)
    {
        // GPU name
        string gpuName = $"NVIDIA GPU {index}";
        if (state.GetName is not null)
        {
            var nameBuffer = new byte[96]; // NVML_DEVICE_NAME_V2_BUFFER_SIZE = 96
            if (state.GetName(device, nameBuffer, (uint)nameBuffer.Length) == NvmlSuccess)
            {
                var nullIdx = Array.IndexOf(nameBuffer, (byte)0);
                if (nullIdx > 0)
                    gpuName = System.Text.Encoding.UTF8.GetString(nameBuffer, 0, nullIdx);
            }
        }

        // Utilization rates (GPU % and memory controller %)
        double? utilizationPercent = null;
        if (state.GetUtilizationRates is not null)
        {
            NvmlUtilization util = default;
            if (state.GetUtilizationRates(device, ref util) == NvmlSuccess)
            {
                utilizationPercent = util.Gpu;
            }
        }

        // Memory info (VRAM used / total in bytes)
        long? vramUsed = null, vramTotal = null;
        if (state.GetMemoryInfo is not null)
        {
            NvmlMemory mem = default;
            if (state.GetMemoryInfo(device, ref mem) == NvmlSuccess)
            {
                vramTotal = (long)mem.Total;
                vramUsed = (long)mem.Used;
            }
        }

        // Temperature (GPU core — sensor type 0)
        double? temperatureC = null;
        if (state.GetTemperature is not null)
        {
            if (state.GetTemperature(device, NvmlTemperatureGpu, out var tempC) == NvmlSuccess)
            {
                temperatureC = tempC;
            }
        }

        // Power draw (total board power in milliwatts → watts)
        double? powerDrawWatts = null;
        if (state.GetPowerUsage is not null)
        {
            if (state.GetPowerUsage(device, out var powerMw) == NvmlSuccess)
            {
                powerDrawWatts = powerMw / 1000.0;
            }
        }

        return new GpuMetrics(index, gpuName, utilizationPercent, vramUsed, vramTotal, temperatureC, powerDrawWatts);
    }

    private static IReadOnlyList<ThermalSensorSample> BuildThermalSensors(List<GpuMetrics> gpus)
    {
        List<ThermalSensorSample> sensors = [];

        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC.HasValue)
            {
                sensors.Add(new ThermalSensorSample(
                    $"gpu-{gpu.GpuIndex}-temperature",
                    $"{gpu.GpuName} Temperature",
                    $"GPU {gpu.GpuIndex} / {gpu.GpuName} / Temperature",
                    gpu.TemperatureC.Value,
                    null,
                    null));
            }
        }

        return sensors;
    }

    // ── NVML interop types ───────────────────────────────────────────────
    //
    // Mirrors the function pointer typedefs in nvml_power.h.
    // nvmlReturn_t is int (NVML_SUCCESS = 0).  nvmlDevice_t is void* (nint).

    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0; // NVML_TEMPERATURE_GPU

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;    // percent GPU utilization
        public uint Memory; // percent memory controller utilization
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total; // bytes
        public ulong Free;  // bytes
        public ulong Used;  // bytes
    }

    private delegate int NvmlInitDelegate();
    private delegate int NvmlShutdownDelegate();
    private delegate int NvmlDeviceGetCountDelegate(out uint count);
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out nint device);
    private delegate int NvmlDeviceGetNameDelegate(nint device, byte[] name, uint length);
    private delegate int NvmlDeviceGetPowerUsageDelegate(nint device, out uint powerMw);
    private delegate int NvmlDeviceGetTemperatureDelegate(nint device, uint sensorType, out uint tempC);
    private delegate int NvmlDeviceGetUtilizationRatesDelegate(nint device, ref NvmlUtilization utilization);
    private delegate int NvmlDeviceGetMemoryInfoDelegate(nint device, ref NvmlMemory memory);

    private static T? GetDelegate<T>(nint dll, string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(dll, name, out var ptr)
            ? Marshal.GetDelegateForFunctionPointer<T>(ptr)
            : null;
    }

    // ── Internal state ───────────────────────────────────────────────────

    private sealed class NvmlState
    {
        public nint DllHandle;
        public NvmlShutdownDelegate? Shutdown;
        public nint[] Devices = [];

        public NvmlDeviceGetNameDelegate? GetName;
        public NvmlDeviceGetPowerUsageDelegate? GetPowerUsage;
        public NvmlDeviceGetTemperatureDelegate? GetTemperature;
        public NvmlDeviceGetUtilizationRatesDelegate? GetUtilizationRates;
        public NvmlDeviceGetMemoryInfoDelegate? GetMemoryInfo;
    }
}
