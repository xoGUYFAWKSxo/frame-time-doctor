using FrameTimeDoctor.Core.Analysis;

namespace FrameTimeDoctor.Core.Rules;

internal static class Stat
{
    public static double Median(IEnumerable<double> values) => FrameStats.Percentile(values.OrderBy(v => v).ToArray(), 0.5);

    /// <summary>Median of the first and last fifth of a series: detects drift over the run.</summary>
    public static (double Early, double Late) Edges<T>(IReadOnlyList<T> series, Func<T, double> value)
    {
        int n = Math.Max(4, series.Count / 5);
        return (Median(series.Take(n).Select(value)), Median(series.Skip(series.Count - n).Select(value)));
    }
}

public sealed class MemoryProfileRule : IRule
{
    public string Id => "memory-profile-off";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        var modules = ctx.System.Memory.Where(m => m.ConfiguredMts > 0).ToList();
        if (modules.Count == 0) yield break;

        int configured = modules.Min(m => m.ConfiguredMts);
        var rated = modules.Select(m => RatedSpeedParser.Parse(m.PartNumber)).ToList();
        // Mixed kits can only run as fast as the slowest; unknown part numbers mean we can't be sure.
        int? ratedSpeed = rated.All(r => r.HasValue) ? rated.Min() : null;
        var parts = modules.Select(m => m.PartNumber).Distinct().ToList();
        var details = parts.Count > 1
            ? new[] { $"Mixed memory kits installed ({string.Join(", ", parts)}). Mixed kits often won't run their rated profile; a matched kit is more reliable." }
            : null;

        if (ratedSpeed is int r && configured < r - 100)
        {
            yield return new RuleHit(Id, Confidence.High,
                $"Your memory ({parts[0]}) is rated for {r} MT/s but is running at {configured} MT/s. " +
                $"That's {(1 - configured / (double)r):P0} slower than it's sold to run.",
                ctx.CpuSideImpactScale,
                new Dictionary<string, string> { ["rated"] = r.ToString(), ["configured"] = configured.ToString() },
                details);
        }
        else if (ratedSpeed == null && modules.Any(m => m.IsDdr5) && configured <= 4800)
        {
            yield return new RuleHit(Id, Confidence.Medium,
                $"Your DDR5 memory ({parts[0]}) is running at {configured} MT/s, the default speed used when no memory profile is enabled. " +
                "Most DDR5 gaming kits are rated 5600–6400 MT/s — check the label on the sticks to confirm yours.",
                ctx.CpuSideImpactScale * 0.8,
                new Dictionary<string, string> { ["rated"] = "its rated speed", ["configured"] = configured.ToString() },
                details);
        }
    }
}

public sealed class CoreCountRule : IRule
{
    public string Id => "cores-disabled";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.Cpu is not { Cores: > 0 } cpu) yield break;
        var s = ctx.System;
        int expectedThreads = cpu.Cores * 2;
        if (s.LogicalProcessors <= 0 || s.LogicalProcessors >= expectedThreads) yield break;

        bool coresOff = s.CpuCores > 0 && s.CpuCores < cpu.Cores;
        bool smtOff = s.CpuCores > 0 && s.LogicalProcessors <= s.CpuCores;
        var what = (coresOff, smtOff) switch
        {
            (true, true) => $"only {s.CpuCores} of {cpu.Cores} cores are enabled and SMT (hyper-threading) is off",
            (true, false) => $"only {s.CpuCores} of {cpu.Cores} cores are enabled",
            (false, true) => "SMT (hyper-threading) is off",
            _ => "some processors are hidden from Windows",
        };
        // Disabling the second CCD on a dual-CCD chip is a known deliberate gaming tweak, so be less sure then.
        bool looksDeliberate = cpu.Ccds == 2 && s.CpuCores == cpu.Cores / 2;
        yield return new RuleHit(Id, looksDeliberate ? Confidence.Low : Confidence.Medium,
            $"Windows sees {s.LogicalProcessors} logical processors, but a Ryzen {cpu.Model} has {cpu.Cores} cores / {expectedThreads} threads: {what}." +
            (looksDeliberate ? " Exactly one CCD's worth of cores is active, which is sometimes done on purpose for gaming." : ""),
            ctx.CpuSideImpactScale,
            new Dictionary<string, string> { ["cores"] = cpu.Cores.ToString(), ["threads"] = expectedThreads.ToString(), ["seen"] = s.LogicalProcessors.ToString() });
    }
}

public sealed class PowerPlanRule : IRule
{
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public string Id => "power-plan";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        var guid = ctx.System.PowerPlanGuid;
        var name = ctx.System.PowerPlanName ?? "unknown";

        if (guid == PowerSaver)
        {
            yield return new RuleHit(Id, Confidence.High,
                $"Windows is using the \"{name}\" plan, which deliberately limits CPU speed to save energy.",
                Math.Max(0.5, ctx.CpuSideImpactScale));
            yield break;
        }

        // Balanced is AMD's own recommendation on Ryzen, so only flag it when the capture shows it hurting.
        if (guid != Balanced || ctx.Telemetry == null || ctx.Cpu == null || ctx.HasHit("thermal-throttling")) yield break;
        if (ctx.Bottleneck is not (Bottleneck.Cpu or Bottleneck.Mixed)) yield break;

        var loaded = ctx.Telemetry.Cpu.Where(c => c.UtilPct >= 10).ToList();
        if (loaded.Count < 10) yield break;
        var floor = ctx.Cpu.MaxBoostMhz * 0.8;
        var dips = loaded.Count(c => c.MaxCoreMhz < floor) / (double)loaded.Count;
        if (dips < 0.15) yield break;

        yield return new RuleHit(Id, Confidence.Medium,
            $"Windows is on the \"{name}\" plan, the game was CPU-limited, and in {dips:P0} of samples even the fastest core " +
            $"was below {floor:0} MHz — cores are ramping down between frames.",
            ctx.CpuSideImpactScale);
    }
}

public sealed class CpuBoostRule : IRule
{
    public string Id => "pbo-stock";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        // Heat or a power-saving plan explain low clocks better than PBO does; fix those first.
        if (ctx.Cpu is not { } cpu || ctx.Telemetry == null || !ctx.System.IsAmdCpu) yield break;
        if (ctx.HasHit("thermal-throttling") || ctx.HasHit("power-plan")) yield break;
        // PBO can't help frames the GPU (or a frame cap) is limiting.
        if (ctx.Bottleneck is Bottleneck.Gpu or Bottleneck.Capped) yield break;

        var loaded = ctx.Telemetry.Cpu.Where(c => c.UtilPct >= 10).Select(c => c.MaxCoreMhz).OrderBy(x => x).ToArray();
        if (loaded.Length < 10) yield break;
        var p90 = FrameStats.Percentile(loaded, 0.9);
        if (p90 >= cpu.MaxBoostMhz * 0.93) yield break;

        yield return new RuleHit(Id,
            ctx.Bottleneck == Bottleneck.Cpu ? Confidence.Medium : Confidence.Low,
            $"During the run your fastest core typically peaked around {p90:0} MHz, {1 - p90 / cpu.MaxBoostMhz:P0} below the " +
            $"{cpu.MaxBoostMhz} MHz your Ryzen {cpu.Model} is rated to boost to, with no sign of thermal throttling. " +
            "The game was at least partly CPU-limited, so higher sustained clocks should show up in frame times.",
            ctx.CpuSideImpactScale);
    }
}

public sealed class ThermalThrottlingRule : IRule
{
    public string Id => "thermal-throttling";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        var t = ctx.Telemetry;
        if (t == null) yield break;

        var details = new List<string>();
        Confidence? confidence = null;
        double scale = 0;

        // GPU: the NVIDIA driver's own throttle-reason flags are authoritative.
        var flagged = t.Gpu.Where(g => g.ThermalSlowdown != null).ToList();
        if (flagged.Count >= 10)
        {
            var share = flagged.Count(g => g.ThermalSlowdown == true) / (double)flagged.Count;
            if (share >= 0.05)
            {
                details.Add($"GPU: the NVIDIA driver reported thermal slowdown in {share:P0} of samples (peak {t.Gpu.Max(g => g.TempC ?? 0):0} °C).");
                confidence = Confidence.High;
                scale = 1.0;
            }
        }
        if (confidence == null)
        {
            var busy = t.Gpu.Where(g => g.ClockMhz > 0 && g.TempC != null && g.UtilPct >= 50).ToList();
            if (busy.Count >= 20)
            {
                var (early, late) = Stat.Edges(busy, g => g.ClockMhz!.Value);
                var peak = busy.Max(g => g.TempC!.Value);
                if (peak >= 85 && late < early * 0.94)
                {
                    details.Add($"GPU: reached {peak:0} °C and its clock fell from {early:0} to {late:0} MHz over the run.");
                    confidence = Confidence.Medium;
                    scale = 0.8;
                }
            }
        }

        // CPU: Windows can't read Ryzen temperatures without a kernel driver, so look for heat soak instead:
        // clocks falling over the run while the load stays the same.
        var cpu = t.Cpu.Where(c => c.UtilPct >= 10).ToList();
        if (cpu.Count >= 20)
        {
            var (early, late) = Stat.Edges(cpu, c => c.MaxCoreMhz);
            var (earlyUtil, lateUtil) = Stat.Edges(cpu, c => c.UtilPct);
            if (late < early * 0.94 && Math.Abs(lateUtil - earlyUtil) < 15)
            {
                details.Add($"CPU: the fastest core dropped from {early:0} MHz at the start of the run to {late:0} MHz at the end " +
                            $"({1 - late / early:P0}) while load stayed about the same, which is a typical heat-soak pattern. " +
                            "Confirm with HWiNFO: a CPU (Tctl/Tdie) reading near 95 °C means the CPU is at its limit.");
                confidence ??= Confidence.Medium;
                scale = Math.Max(scale, ctx.CpuSideImpactScale);
            }
        }

        if (confidence == null) yield break;
        yield return new RuleHit(Id, confidence.Value, string.Join(" ", details), scale);
    }
}
