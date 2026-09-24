using FrameTimeDoctor.Core.Capture;

namespace FrameTimeDoctor.Core.Rules;

internal static class Display
{
    /// <summary>NVIDIA Reflex's auto-cap formula: a few fps under refresh keeps G-Sync engaged.</summary>
    public static int SuggestedCap(int refreshHz) => (int)Math.Floor(refreshHz - refreshHz * refreshHz / 3600.0);

    public static Dictionary<string, string> Vars(int refreshHz, double? capFps = null)
    {
        var period = 1000.0 / refreshHz;
        var vars = new Dictionary<string, string>
        {
            ["refresh"] = refreshHz.ToString(),
            ["suggestedCap"] = SuggestedCap(refreshHz).ToString(),
            ["period"] = period.ToString("0.0"),
            ["doublePeriod"] = (period * 2).ToString("0.0"),
        };
        if (capFps != null) vars["capFps"] = Math.Round(capFps.Value).ToString("0");
        return vars;
    }

    public static (string Mode, double Share) DominantPresentMode(FrameCapture capture)
    {
        var top = capture.Frames.GroupBy(f => f.PresentMode).MaxBy(g => g.Count())!;
        return (top.Key, top.Count() / (double)capture.Frames.Count);
    }
}

public sealed class FrameCapBelowRefreshRule : IRule
{
    public string Id => "frame-cap-below-refresh";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.Stats is not { } stats || ctx.System.RefreshRateHz is not int refresh || !ctx.LooksCapped) yield break;
        if (stats.AvgFps >= refresh * 0.9) yield break; // capped at or near refresh is the right setup

        // Only a problem if the hardware had room for more frames.
        var gpuUtil = ctx.AvgGpuUtil;
        var gpuRatio = stats.GpuBusyRatio;
        bool headroom = gpuUtil is < 85 || gpuRatio is < 0.8;
        if (!headroom) yield break;

        var load = gpuUtil != null ? $"the GPU was only {gpuUtil:0}% busy" : $"the GPU was busy for only {gpuRatio:P0} of each frame";
        yield return new RuleHit(Id, Confidence.Medium,
            $"Frame rate held almost perfectly flat at {stats.AvgFps:0} fps while your display runs at {refresh} Hz and {load}.",
            Math.Min(1.0, (refresh - stats.AvgFps) / stats.AvgFps),
            Display.Vars(refresh, stats.AvgFps));
    }
}

public sealed class VsyncJudderRule : IRule
{
    public string Id => "vsync-judder";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.Capture is not { } capture || ctx.System.RefreshRateHz is not int refresh) yield break;
        var frames = capture.Frames;
        var vsyncShare = frames.Count(f => f.SyncInterval >= 1 && !f.AllowsTearing) / (double)frames.Count;
        if (vsyncShare < 0.8) yield break;

        var period = 1000.0 / refresh;
        double Near(double target) => frames.Count(f => Math.Abs(f.FrameTimeMs - target) <= target * 0.12) / (double)frames.Count;
        var single = Near(period);
        var doubled = Near(period * 2);
        if (single < 0.3 || doubled < 0.03) yield break;

        yield return new RuleHit(Id, Confidence.High,
            $"V-Sync was on for {vsyncShare:P0} of frames, and {doubled:P1} of frames were held for two refreshes " +
            $"({period * 2:0.0} ms) instead of one ({period:0.0} ms). Each of those is a visible hitch.",
            Math.Min(1.5, doubled / 0.05),
            Display.Vars(refresh));
    }
}

public sealed class ComposedPresentationRule : IRule
{
    public string Id => "composed-presentation";

    public IEnumerable<RuleHit> Evaluate(DiagnosticContext ctx)
    {
        if (ctx.Capture is not { Frames.Count: > 0 } capture) yield break;
        var (mode, share) = Display.DominantPresentMode(capture);
        if (!mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase) || share < 0.8) yield break;

        // GDI copy paths are the legacy, slowest route; composed flip is milder.
        var gdiCopy = mode.Contains("Copy", StringComparison.OrdinalIgnoreCase);
        yield return new RuleHit(Id, gdiCopy ? Confidence.High : Confidence.Medium,
            $"{share:P0} of frames were presented as \"{mode}\", meaning Windows composited them with the desktop instead of flipping them straight to the display.",
            gdiCopy ? 1.5 : 1.0,
            new Dictionary<string, string> { ["presentMode"] = mode });
    }
}
