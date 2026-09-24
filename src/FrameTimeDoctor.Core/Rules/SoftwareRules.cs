namespace FrameTimeDoctor.Core.Rules;

public sealed class BackgroundProcessRule : IRule
{
    public string Id => "background-processes";

    const double HeavyCores = 0.5;      // anything using half a core or more
    const double KnownOffenderCores = 0.15;

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.Telemetry is not { Processes.Count: > 0 } t) yield break;

        var game = ctx.Capture != null ? Path.GetFileNameWithoutExtension(ctx.Capture.Application) : null;
        var ignored = new HashSet<string>(ctx.Kb.IgnoredProcesses, StringComparer.OrdinalIgnoreCase);
        if (game != null) ignored.Add(game);

        var offenders = t.Processes
            .Where(p => !ignored.Contains(p.Name))
            .Where(p => p.CoresUsed >= HeavyCores || (ctx.Kb.KnownOffender(p.Name) != null && p.CoresUsed >= KnownOffenderCores))
            .OrderByDescending(p => p.CoresUsed)
            .Take(6)
            .ToList();
        if (offenders.Count == 0) yield break;

        var total = offenders.Sum(p => p.CoresUsed);
        var peakUtil = ctx.Telemetry.Cpu.Count > 0 ? ctx.Telemetry.Cpu.Max(c => c.UtilPct) : 0;
        var confidence = total >= 2 || peakUtil >= 85 ? Confidence.High
                       : total >= 1 ? Confidence.Medium
                       : Confidence.Low;

        var details = offenders.Select(p =>
        {
            var known = ctx.Kb.KnownOffender(p.Name);
            var label = known != null ? $"{known.Label} ({p.Name}.exe)" : $"{p.Name}.exe";
            var line = $"{label}: {p.CoresUsed:0.00} CPU cores' worth of time";
            return known != null ? $"{line}. {known.Advice}" : line;
        }).ToList();

        yield return new RuleHit(Id, confidence,
            $"{offenders.Count} background program{(offenders.Count == 1 ? "" : "s")} used {total:0.0} CPU cores' worth of time while the game was running.",
            ctx.CpuSideImpactScale * Math.Min(1.0, 0.4 + total / 3),
            Details: details);
    }
}

public sealed class GpuDriverRule : IRule
{
    public string Id => "gpu-driver-outdated";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.System.Gpu is not { IsNvidia: true } gpu) yield break;
        var policy = ctx.Kb.Drivers;

        if (policy.NvidiaMinimumVersion != null &&
            Version.TryParse(gpu.DriverVersion, out var installed) &&
            Version.TryParse(policy.NvidiaMinimumVersion, out var minimum) &&
            installed < minimum)
        {
            yield return new RuleHit(Id, Confidence.High,
                $"Driver {gpu.DriverVersion} is older than {policy.NvidiaMinimumVersion}, the oldest version this knowledge base treats as current.");
            yield break;
        }

        if (gpu.DriverDate is DateTime date)
        {
            var age = (int)(ctx.System.TakenAt - date).TotalDays;
            if (age > policy.NvidiaMaxAgeDays)
                yield return new RuleHit(Id, Confidence.Medium,
                    $"Your NVIDIA driver ({gpu.DriverVersion}) is dated {date:yyyy-MM-dd}, {age} days old. NVIDIA typically releases a Game Ready driver every few weeks.");
        }
    }
}

public sealed class ChipsetDriverRule : IRule
{
    public string Id => "chipset-driver";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        var s = ctx.System;
        if (!s.IsAmdCpu) yield break;
        var vars = new Dictionary<string, string> { ["motherboard"] = s.Motherboard ?? "your motherboard" };
        var dualCcdX3d = ctx.Cpu is { X3d: true, Ccds: 2 };

        if (s.ChipsetDriverVersion == null)
        {
            var evidence = "The AMD Chipset Software package isn't installed.";
            if (dualCcdX3d)
                evidence += $" On a dual-CCD {ctx.Cpu!.Model}, this matters a lot: without the 3D V-Cache Performance Optimizer, " +
                            "Windows can run games on the CCD without the extra cache.";
            // On dual-CCD X3D this finding can be worth several times the usual range.
            yield return new RuleHit(Id, Confidence.High, evidence, dualCcdX3d ? 3.0 : 1.0, vars);
            yield break;
        }

        var policy = ctx.Kb.Drivers;
        if (policy.AmdChipsetMinimumVersion != null &&
            Version.TryParse(s.ChipsetDriverVersion, out var installed) &&
            Version.TryParse(policy.AmdChipsetMinimumVersion, out var minimum) &&
            installed < minimum)
        {
            yield return new RuleHit(Id, Confidence.Medium,
                $"AMD chipset driver {s.ChipsetDriverVersion} is older than {policy.AmdChipsetMinimumVersion}.", 1.0, vars);
            yield break;
        }

        if (s.ChipsetDriverInstallDate is DateTime installedOn &&
            (s.TakenAt - installedOn).TotalDays > policy.AmdChipsetMaxAgeDays)
        {
            yield return new RuleHit(Id, Confidence.Low,
                $"AMD chipset driver {s.ChipsetDriverVersion} was installed {installedOn:yyyy-MM-dd}, over a year ago.", 1.0, vars);
        }
    }
}
