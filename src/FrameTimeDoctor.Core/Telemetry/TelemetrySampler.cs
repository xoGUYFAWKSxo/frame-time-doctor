using System.Diagnostics;
using System.Globalization;

namespace FrameTimeDoctor.Core.Telemetry;

/// <summary>
/// Samples GPU (nvidia-smi, an NVML front end) and CPU (Windows performance counters) state while
/// PresentMon captures frames, plus per-process CPU time over the whole window.
/// </summary>
public sealed class TelemetrySampler : IDisposable
{
    readonly double _baseClockMhz;
    readonly int _intervalMs;
    readonly Stopwatch _clock = new();
    readonly TelemetryLog _log = new();
    readonly object _gate = new();

    Process? _nvidiaSmi;
    Thread? _cpuThread;
    volatile bool _running;
    Dictionary<int, (string Name, TimeSpan Cpu)> _procStart = new();

    public TelemetrySampler(double baseClockMhz, int intervalMs = 500)
    {
        _baseClockMhz = baseClockMhz;
        _intervalMs = intervalMs;
    }

    public void Start()
    {
        _running = true;
        _procStart = SnapshotProcessCpu();
        _clock.Start();
        StartGpu();
        _cpuThread = new Thread(SampleCpuLoop) { IsBackground = true, Name = "ftd-cpu-sampler" };
        _cpuThread.Start();
    }

    public TelemetryLog Stop()
    {
        _running = false;
        var elapsed = _clock.Elapsed;
        var procEnd = SnapshotProcessCpu();
        _cpuThread?.Join(2000);
        try { _nvidiaSmi?.Kill(); } catch { /* exited */ }

        _log.DurationSeconds = elapsed.TotalSeconds;
        _log.Processes = DiffProcessCpu(_procStart, procEnd, elapsed.TotalSeconds);
        lock (_gate) return _log;
    }

    public void Dispose()
    {
        _running = false;
        try { _nvidiaSmi?.Kill(); } catch { /* exited */ }
        _nvidiaSmi?.Dispose();
    }

    // ---- GPU ------------------------------------------------------------------------------

    const string GpuFields = "utilization.gpu,clocks.gr,clocks.mem,temperature.gpu,power.draw,power.limit";

    void StartGpu()
    {
        // Driver 530+ renamed clocks_throttle_reasons to clocks_event_reasons; probe which one works.
        string? reasonPrefix = null;
        foreach (var prefix in new[] { "clocks_event_reasons", "clocks_throttle_reasons" })
        {
            var probe = SystemProbe.RunTool("nvidia-smi", $"--query-gpu={prefix}.hw_thermal_slowdown --format=csv,noheader");
            if (probe != null && !probe.Contains("not a valid", StringComparison.OrdinalIgnoreCase)) { reasonPrefix = prefix; break; }
        }
        var fields = GpuFields + (reasonPrefix == null ? "" :
            $",{reasonPrefix}.hw_thermal_slowdown,{reasonPrefix}.sw_thermal_slowdown,{reasonPrefix}.sw_power_cap");

        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"-i 0 --query-gpu={fields} --format=csv,noheader,nounits -lms {_intervalMs}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            _nvidiaSmi = Process.Start(psi);
            if (_nvidiaSmi == null) throw new InvalidOperationException("nvidia-smi did not start");
            _nvidiaSmi.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null || !_running) return;
                var sample = ParseGpuLine(e.Data, _clock.Elapsed.TotalSeconds);
                if (sample != null) lock (_gate) _log.Gpu.Add(sample);
            };
            _nvidiaSmi.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            lock (_gate) _log.Warnings.Add($"GPU telemetry unavailable (nvidia-smi): {ex.Message}");
        }
    }

    internal static GpuSample? ParseGpuLine(string line, double t)
    {
        var f = line.Split(',').Select(x => x.Trim()).ToArray();
        if (f.Length < 6) return null;
        double? N(int i) => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        bool? B(int i) => i < f.Length ? f[i].Equals("Active", StringComparison.OrdinalIgnoreCase) : null;
        if (N(0) == null) return null; // header or error text
        bool? thermal = f.Length >= 8 ? (B(6) == true || B(7) == true) : null;
        return new GpuSample(t, N(0), N(1), N(2), N(3), N(4), N(5), thermal, f.Length >= 9 ? B(8) : null);
    }

    // ---- CPU ------------------------------------------------------------------------------

    void SampleCpuLoop()
    {
        List<PerformanceCounter> cores;
        PerformanceCounter utility;
        try
        {
            // "% Processor Performance" is effective clock as a percentage of base (can exceed 100 when boosting).
            var category = new PerformanceCounterCategory("Processor Information");
            cores = category.GetInstanceNames()
                .Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .Select(n => new PerformanceCounter("Processor Information", "% Processor Performance", n, readOnly: true))
                .ToList();
            utility = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true);
            foreach (var c in cores) c.NextValue(); // first read primes the counter
            utility.NextValue();
        }
        catch (Exception ex)
        {
            lock (_gate) _log.Warnings.Add($"CPU telemetry unavailable (performance counters): {ex.Message}");
            return;
        }

        while (_running)
        {
            Thread.Sleep(_intervalMs);
            if (!_running) break;
            try
            {
                var mhz = cores.Select(c => c.NextValue() / 100.0 * _baseClockMhz).ToList();
                var util = Math.Min(100, utility.NextValue());
                lock (_gate) _log.Cpu.Add(new CpuSample(_clock.Elapsed.TotalSeconds, util, mhz.Max(), mhz.Average()));
            }
            catch (Exception ex)
            {
                lock (_gate) _log.Warnings.Add($"CPU sample failed: {ex.Message}");
            }
        }
        foreach (var c in cores) c.Dispose();
        utility.Dispose();
    }

    // ---- Per-process CPU -------------------------------------------------------------------

    static Dictionary<int, (string, TimeSpan)> SnapshotProcessCpu()
    {
        var result = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try { result[p.Id] = (p.ProcessName, p.TotalProcessorTime); }
            catch { /* protected or exited; needs elevation to read some system processes */ }
            finally { p.Dispose(); }
        }
        return result;
    }

    internal static List<ProcessCpuUsage> DiffProcessCpu(
        Dictionary<int, (string Name, TimeSpan Cpu)> start, Dictionary<int, (string Name, TimeSpan Cpu)> end, double seconds)
    {
        if (seconds <= 0) return new();
        return end
            .Where(e => start.TryGetValue(e.Key, out var s) && s.Name == e.Value.Name)
            .Select(e => (e.Value.Name, Delta: (e.Value.Cpu - start[e.Key].Cpu).TotalSeconds))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cores = g.Sum(x => x.Delta) / seconds;
                return new ProcessCpuUsage(g.Key, g.Count(), Math.Round(cores, 3),
                    Math.Round(cores / Environment.ProcessorCount * 100, 2));
            })
            .Where(p => p.CoresUsed > 0.01)
            .OrderByDescending(p => p.CoresUsed)
            .ToList();
    }
}
