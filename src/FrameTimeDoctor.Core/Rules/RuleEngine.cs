using FrameTimeDoctor.Core.Analysis;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Knowledge;
using FrameTimeDoctor.Core.Telemetry;

namespace FrameTimeDoctor.Core.Rules;

public enum Confidence { Low, Medium, High }

/// <summary>What limited the frame rate during the run. Scales how much CPU-side fixes can matter.</summary>
public enum Bottleneck { Unknown, Gpu, Cpu, Mixed, Capped }

/// <summary>
/// Everything a rule may look at. Capture/Stats/Telemetry are null for a probe-only run (no benchmark),
/// so rules that need measurements simply don't fire.
/// </summary>
public sealed class DiagnosticContext
{
    public required SystemSnapshot System { get; init; }
    public required KnowledgeBase Kb { get; init; }
    public FrameCapture? Capture { get; init; }
    public FrameStats? Stats { get; init; }
    public TelemetryLog? Telemetry { get; init; }

    public CpuSpec? Cpu => Kb.MatchCpu(System.CpuName);
    public Bottleneck Bottleneck => _bottleneck ??= ClassifyBottleneck();
    Bottleneck? _bottleneck;

    /// <summary>Hits from rules that already ran, so later rules can defer to a more specific cause.</summary>
    public List<RuleHit> Hits { get; } = new();
    public bool HasHit(string ruleId) => Hits.Any(h => h.RuleId == ruleId);

    public double? AvgGpuUtil
    {
        get
        {
            var util = Telemetry?.Gpu.Where(g => g.UtilPct != null).Select(g => g.UtilPct!.Value).ToList();
            return util is { Count: > 0 } ? util.Average() : null;
        }
    }

    /// <summary>A capped game has near-constant frame times.</summary>
    public bool LooksCapped => Stats != null && Stats.FrameTimeCv < 0.06 && Stats.P99FrameTimeMs < Stats.MedianFrameTimeMs * 1.35;

    Bottleneck ClassifyBottleneck()
    {
        if (Stats == null) return Bottleneck.Unknown;
        if (LooksCapped) return Bottleneck.Capped;
        // GPU busy time as a share of frame time comes straight from PresentMon; it's the best signal.
        if (Stats.GpuBusyRatio is double r)
            return r >= 0.90 ? Bottleneck.Gpu : r <= 0.70 ? Bottleneck.Cpu : Bottleneck.Mixed;
        if (AvgGpuUtil is double u)
            return u >= 95 ? Bottleneck.Gpu : u <= 80 ? Bottleneck.Cpu : Bottleneck.Mixed;
        return Bottleneck.Unknown;
    }

    /// <summary>How much of a CPU/memory-side gain can show up given what limited this run.</summary>
    public double CpuSideImpactScale => Bottleneck switch
    {
        Bottleneck.Cpu => 1.0,
        Bottleneck.Mixed => 0.6,
        Bottleneck.Unknown => 0.6,
        Bottleneck.Gpu => 0.25,
        Bottleneck.Capped => 0.2,
        _ => 1.0,
    };
}

/// <param name="Evidence">Plain-language statement of what was measured, with the numbers.</param>
/// <param name="ImpactScale">Multiplier on the knowledge-base impact range for this system's situation.</param>
/// <param name="Vars">Values substituted into {placeholders} in the knowledge-base text.</param>
/// <param name="Details">Optional bullet list (e.g. offending processes) shown under the evidence.</param>
public sealed record RuleHit(
    string RuleId,
    Confidence Confidence,
    string Evidence,
    double ImpactScale = 1.0,
    IReadOnlyDictionary<string, string>? Vars = null,
    IReadOnlyList<string>? Details = null);

public interface IRule
{
    string Id { get; }
    IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx);
}

/// <summary>A rule hit joined with its knowledge-base entry, ready for ranking and reporting.</summary>
public sealed class Finding
{
    public required KbRule Rule { get; init; }
    public required RuleHit Hit { get; init; }

    public double ImpactScale => Math.Max(Hit.ImpactScale, Rule.MinImpactScale);
    public double ImpactMinPct => Math.Round(Rule.Impact.MinPct * ImpactScale, 1);
    public double ImpactMaxPct => Math.Round(Rule.Impact.MaxPct * ImpactScale, 1);

    /// <summary>Expected gain discounted by how sure we are. Findings are ranked by this.</summary>
    public double Score => (ImpactMinPct + ImpactMaxPct) / 2 * Hit.Confidence switch
    {
        Confidence.High => 1.0,
        Confidence.Medium => 0.6,
        _ => 0.3,
    };

    public string Title => Fill(Rule.Title);
    public string Why => Fill(Rule.Why);
    public IEnumerable<string> FixSteps => Rule.Fix.Select(Fill);

    string Fill(string text)
    {
        if (Hit.Vars == null) return text;
        foreach (var (k, v) in Hit.Vars) text = text.Replace("{" + k + "}", v);
        return text;
    }
}

public sealed class RuleEngine
{
    readonly IReadOnlyList<IRule> _rules;

    public RuleEngine(IEnumerable<IRule> rules) => _rules = rules.ToList();

    /// <summary>
    /// Default rule set. Order matters: root causes (heat, power plan) run before rules that would
    /// otherwise misread their symptoms (low boost clocks).
    /// </summary>
    public static RuleEngine CreateDefault() => new(new IRule[]
    {
        new ThermalThrottlingRule(),
        new PowerPlanRule(),
        new MemoryProfileRule(),
        new CoreCountRule(),
        new CpuBoostRule(),
        new BackgroundProcessRule(),
        new FrameCapBelowRefreshRule(),
        new VsyncJudderRule(),
        new ComposedPresentationRule(),
        new GpuDriverRule(),
        new ChipsetDriverRule(),
    });

    public List<Finding> Run(DiagnosticContext ctx)
    {
        foreach (var rule in _rules)
        {
            foreach (var hit in rule.Evaluate(ctx))
                ctx.Hits.Add(hit);
        }
        return ctx.Hits
            .Select(h => new Finding { Rule = ctx.Kb.Rule(h.RuleId), Hit = h })
            .OrderByDescending(f => f.Score)
            .ToList();
    }
}
