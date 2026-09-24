using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Knowledge;
using FrameTimeDoctor.Core.Rules;
using FrameTimeDoctor.Core.Telemetry;
using FrameTimeDoctor.Core.Analysis;

namespace FrameTimeDoctor.Tests;

/// <summary>Builders for synthetic captures with known properties.</summary>
static class Synthetic
{
    public static readonly KnowledgeBase Kb = KnowledgeBase.Load(Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.json"));

    public static FrameCapture Capture(IEnumerable<double> frameTimes, int syncInterval = 0, bool tearing = true,
        string mode = "Hardware: Independent Flip", Func<double, double?>? gpuBusy = null)
    {
        double t = 0;
        var frames = frameTimes.Select(ft =>
        {
            var r = new FrameRecord(t, ft, ft, ft * 0.5, gpuBusy?.Invoke(ft) ?? ft * 0.95, syncInterval, tearing, mode, "Application");
            t += ft;
            return r;
        }).ToList();
        return new FrameCapture { Application = "Game.exe", ProcessId = 1234, Frames = frames };
    }

    /// <summary>Normal-ish frame times around a mean with deterministic jitter.</summary>
    public static IEnumerable<double> Jittery(int count, double meanMs, double jitterMs, int seed = 1)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++) yield return Math.Max(1, meanMs + (rng.NextDouble() * 2 - 1) * jitterMs);
    }

    public static SystemSnapshot Rig(Action<SystemSnapshot>? tweak = null)
    {
        var s = new SystemSnapshot
        {
            CpuName = "AMD Ryzen 9 9950X 16-Core Processor",
            CpuBaseClockMhz = 4300,
            LogicalProcessors = 32,
            Memory =
            {
                new MemoryModule("Kingston", "KF560C32-48", 4800, 6000, 48, 34),
                new MemoryModule("Kingston", "KF560C32-48", 4800, 6000, 48, 34),
            },
            Gpu = new GpuInfo("NVIDIA GeForce RTX 5070", "NVIDIA", "617.14", DateTime.Today.AddDays(-7)),
            RefreshRateHz = 120,
            Resolution = "3440x1440",
            PowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            PowerPlanName = "High performance",
            ChipsetDriverVersion = "8.03.25.247",
            ChipsetDriverInstallDate = DateTime.Today.AddDays(-1),
        };
        tweak?.Invoke(s);
        return s;
    }

    public static TelemetryLog Telemetry(int samples = 120, double gpuUtil = 97, double cpuMaxMhz = 5600,
        Func<int, double>? cpuMhzAt = null, bool gpuThermal = false, params ProcessCpuUsage[] processes)
    {
        var log = new TelemetryLog { DurationSeconds = samples * 0.5 };
        for (int i = 0; i < samples; i++)
        {
            log.Gpu.Add(new GpuSample(i * 0.5, gpuUtil, 2700, 14000, 70, 220, 250, gpuThermal, true));
            var mhz = cpuMhzAt?.Invoke(i) ?? cpuMaxMhz;
            log.Cpu.Add(new CpuSample(i * 0.5, 30, mhz, mhz - 400));
        }
        log.Processes.AddRange(processes);
        return log;
    }

    public static DiagnosticContext Context(FrameCapture? capture = null, SystemSnapshot? system = null, TelemetryLog? telemetry = null) => new()
    {
        System = system ?? Rig(),
        Kb = Kb,
        Capture = capture,
        Stats = capture != null ? FrameStats.Compute(capture) : null,
        Telemetry = telemetry,
    };

    public static List<Finding> Run(DiagnosticContext ctx) => RuleEngine.CreateDefault().Run(ctx);
}
