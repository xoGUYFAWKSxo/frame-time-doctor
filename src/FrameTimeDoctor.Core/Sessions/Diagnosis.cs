using FrameTimeDoctor.Core.Analysis;
using FrameTimeDoctor.Core.Knowledge;
using FrameTimeDoctor.Core.Rules;

namespace FrameTimeDoctor.Core.Sessions;

/// <summary>A session run through the analysis and the rules engine.</summary>
public sealed class Diagnosis
{
    public required Session Session { get; init; }
    public required FrameStats Stats { get; init; }
    public required DiagnosticContext Context { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }

    public static Diagnosis Run(Session session, KnowledgeBase kb)
    {
        var stats = FrameStats.Compute(session.Capture);
        var ctx = new DiagnosticContext
        {
            System = session.System,
            Kb = kb,
            Capture = session.Capture,
            Stats = stats,
            Telemetry = session.Telemetry,
        };
        var findings = RuleEngine.CreateDefault().Run(ctx);
        return new Diagnosis { Session = session, Stats = stats, Context = ctx, Findings = findings };
    }
}

/// <summary>Before/after comparison of two diagnoses of the same benchmark.</summary>
public sealed class Comparison
{
    public required Diagnosis Before { get; init; }
    public required Diagnosis After { get; init; }

    /// <summary>
    /// Run-to-run variation on a repeatable benchmark. Deltas smaller than this are reported as noise
    /// rather than improvements. Provisional until measured across real repeat runs.
    /// </summary>
    public const double NoiseAvgPct = 3.0;
    public const double NoiseLowsPct = 5.0;

    public IEnumerable<Finding> Resolved =>
        Before.Findings.Where(b => After.Findings.All(a => a.Rule.Id != b.Rule.Id));

    public IEnumerable<Finding> New =>
        After.Findings.Where(a => Before.Findings.All(b => b.Rule.Id != a.Rule.Id));

    /// <summary>Reasons the two runs may not be comparable. Empty means comparable.</summary>
    public IEnumerable<string> ComparabilityWarnings()
    {
        var b = Before.Session; var a = After.Session;
        if (!string.Equals(Path.GetFileNameWithoutExtension(b.Capture.Application), Path.GetFileNameWithoutExtension(a.Capture.Application), StringComparison.OrdinalIgnoreCase))
            yield return $"Different games were captured ({b.Capture.Application} vs {a.Capture.Application}).";
        var db = Before.Stats.DurationSeconds; var da = After.Stats.DurationSeconds;
        if (Math.Abs(da - db) / Math.Max(db, da) > 0.2)
            yield return $"Capture lengths differ a lot ({db:0}s vs {da:0}s). Use the same benchmark sequence and duration.";
        if (b.System.Resolution != a.System.Resolution)
            yield return $"Desktop resolution changed ({b.System.Resolution} → {a.System.Resolution}).";
        if (b.System.RefreshRateHz != a.System.RefreshRateHz)
            yield return $"Refresh rate changed ({b.System.RefreshRateHz} Hz → {a.System.RefreshRateHz} Hz).";
    }

    public static double DeltaPct(double before, double after) => before == 0 ? 0 : (after - before) / before * 100;
}
