using System.Globalization;
using System.Net;
using System.Text;
using FrameTimeDoctor.Core.Analysis;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Rules;
using FrameTimeDoctor.Core.Sessions;

namespace FrameTimeDoctor.Core.Report;

/// <summary>
/// Renders a single self-contained HTML file (no scripts, no external assets) so the report can be
/// opened offline, attached to a support ticket or screenshotted for a forum post.
/// </summary>
public static class HtmlReportGenerator
{
    public static string Generate(Diagnosis current, Diagnosis? baseline = null, string? retestCommand = null)
    {
        var cmp = baseline != null ? new Comparison { Before = baseline, After = current } : null;
        var s = current.Session;
        var h = new StringBuilder();

        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        h.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        h.Append($"<title>Frame Time Doctor – {E(GameName(s))}</title><style>{Css}</style></head><body><main>");

        // Header
        h.Append("<header><div class=\"brand\">Frame Time Doctor</div>");
        h.Append($"<h1>{E(GameName(s))} <span class=\"label\">{E(s.Info.Label)}</span></h1>");
        h.Append($"<p class=\"sub\">{s.Info.StartedAt:dddd d MMM yyyy, HH:mm} · {current.Stats.DurationSeconds:0}s capture · {current.Stats.FrameCount:N0} frames");
        if (cmp != null) h.Append($" · compared with <b>{E(baseline!.Session.Info.Label)}</b> ({baseline.Session.Info.StartedAt:d MMM HH:mm})");
        h.Append("</p></header>");

        if (cmp != null) AppendComparison(h, cmp);

        // Metrics
        h.Append($"<section><h2>{(cmp != null ? "This run" : "Baseline")}</h2><div class=\"tiles\">");
        Tile(h, "1% low", $"{current.Stats.OnePercentLowFps:0}", "fps", "The frame rate you drop to in the worst 1% of moments. This is the number that feels like stutter.", true);
        Tile(h, "0.1% low", $"{current.Stats.PointOnePercentLowFps:0}", "fps", "The worst 0.1% of frames: rare, big hitches.");
        Tile(h, "Average", $"{current.Stats.AvgFps:0}", "fps", "Average frames per second over the run.");
        Tile(h, "Frame time variation", $"{current.Stats.StdDevMs:0.0}", "ms", "Standard deviation of frame times. Lower is smoother.");
        Tile(h, "Stutters", $"{current.Stats.StuttersPerMinute:0.0}", "/min", $"Frames that took at least {FrameStats.StutterRatio:0}× as long as the frames around them.");
        Tile(h, "Limited by", BottleneckLabel(current.Context.Bottleneck), "", BottleneckHelp(current.Context.Bottleneck));
        h.Append("</div>");
        h.Append("<figure>");
        h.Append(FrameTimeChart(current.Session.Capture.Frames, baseline?.Session.Capture.Frames));
        h.Append("<figcaption><span class=\"key now\"></span>This run");
        if (baseline != null) h.Append("<span class=\"key before\"></span>Before");
        h.Append(" · Frame time per frame over the run; spikes are hitches. Lower and flatter is better.</figcaption></figure></section>");

        // Findings
        AppendFindings(h, current.Findings);

        // Re-test
        h.Append("<section><h2>Re-test</h2><ol class=\"steps\">");
        h.Append("<li>Apply <b>one</b> fix, starting from the top of the list. Changing several things at once hides which one helped.</li>");
        h.Append("<li>Run the exact same benchmark sequence again, at the same settings and resolution.</li>");
        h.Append($"<li>Run: <code>{E(retestCommand ?? $"ftd capture --process {s.Capture.Application} --baseline {s.Info.Id}")}</code></li>");
        h.Append("<li>The new report will show the before/after change and which findings are resolved.</li></ol></section>");

        AppendRunDetails(h, current);
        AppendSystem(h, current);
        AppendMethod(h);

        h.Append($"<footer>Frame Time Doctor · knowledge base {E(current.Context.Kb.Version)} · session {E(s.Info.Id)}</footer>");
        h.Append("</main></body></html>");
        return h.ToString();
    }

    // ---- Sections ---------------------------------------------------------------------------

    static void AppendComparison(StringBuilder h, Comparison c)
    {
        var b = c.Before.Stats; var a = c.After.Stats;
        h.Append("<section class=\"compare\"><h2>Before → after</h2>");
        foreach (var warning in c.ComparabilityWarnings())
            h.Append($"<p class=\"warn\">⚠ {E(warning)}</p>");
        h.Append("<div class=\"tiles\">");
        DeltaTile(h, "1% low", b.OnePercentLowFps, a.OnePercentLowFps, "fps", higherIsBetter: true, Comparison.NoiseLowsPct);
        DeltaTile(h, "0.1% low", b.PointOnePercentLowFps, a.PointOnePercentLowFps, "fps", higherIsBetter: true, Comparison.NoiseLowsPct);
        DeltaTile(h, "Average", b.AvgFps, a.AvgFps, "fps", higherIsBetter: true, Comparison.NoiseAvgPct);
        DeltaTile(h, "Stutters", b.StuttersPerMinute, a.StuttersPerMinute, "/min", higherIsBetter: false, 15);
        h.Append("</div>");

        var resolved = c.Resolved.ToList();
        var added = c.New.ToList();
        if (resolved.Count > 0)
            h.Append("<p class=\"resolved\">✓ Resolved: " + string.Join(", ", resolved.Select(f => E(f.Title))) + "</p>");
        if (added.Count > 0)
            h.Append("<p class=\"warn\">New since last run: " + string.Join(", ", added.Select(f => E(f.Title))) + "</p>");
        h.Append($"<p class=\"note\">Changes smaller than about {Comparison.NoiseAvgPct:0}% (average) or {Comparison.NoiseLowsPct:0}% (lows) are within normal run-to-run variation and are marked as noise.</p>");
        h.Append("</section>");
    }

    static void AppendFindings(StringBuilder h, IReadOnlyList<Finding> findings)
    {
        h.Append($"<section><h2>Findings <span class=\"count\">{findings.Count}</span></h2>");
        if (findings.Count == 0)
        {
            h.Append("<p class=\"ok\">No known misconfigurations were detected. Your system is running the way it should for this game — remaining performance differences come down to hardware and in-game settings.</p></section>");
            return;
        }
        h.Append("<p class=\"note\">Ranked by expected gain on this system, weighted by how certain the diagnosis is. Start at the top.</p>");
        int rank = 1;
        foreach (var f in findings)
        {
            h.Append($"<article class=\"finding\"><div class=\"rank\">{rank++}</div><div class=\"body\">");
            h.Append($"<h3>{E(f.Title)}</h3><div class=\"meta\">");
            h.Append($"<span class=\"badge conf-{f.Hit.Confidence.ToString().ToLowerInvariant()}\">{f.Hit.Confidence} confidence</span>");
            h.Append($"<span class=\"badge cat\">{E(f.Rule.Category)}</span>");
            var impact = f.ImpactMaxPct < 1 ? "little measurable change expected here"
                : $"expected +{f.ImpactMinPct:0.#}–{f.ImpactMaxPct:0.#}% {E(f.Rule.Impact.Metric)}";
            h.Append($"<span class=\"impact\">{impact}{(f.Rule.Validated ? "" : " <i title=\"Estimated range; not yet validated with before/after testing\">(estimate)</i>")}</span>");
            h.Append("</div>");
            h.Append($"<p class=\"evidence\"><b>What we measured:</b> {E(f.Hit.Evidence)}</p>");
            if (f.Hit.Details is { Count: > 0 })
                h.Append("<ul class=\"details\">" + string.Concat(f.Hit.Details.Select(d => $"<li>{E(d)}</li>")) + "</ul>");
            h.Append($"<p><b>Why it matters:</b> {E(f.Why)}</p>");
            h.Append("<details open><summary>How to fix it</summary><ol>");
            foreach (var step in f.FixSteps) h.Append($"<li>{E(step)}</li>");
            h.Append("</ol></details></div></article>");
        }
        h.Append("</section>");
    }

    static void AppendRunDetails(StringBuilder h, Diagnosis d)
    {
        var st = d.Stats; var cap = d.Session.Capture; var t = d.Session.Telemetry;
        var mode = cap.Frames.GroupBy(f => f.PresentMode).MaxBy(g => g.Count())?.Key ?? "Unknown";

        h.Append("<section><h2>Run details</h2><div class=\"cols\"><table>");
        Row(h, "Median frame time", $"{st.MedianFrameTimeMs:0.00} ms");
        Row(h, "95th / 99th / 99.9th percentile", $"{st.P95FrameTimeMs:0.0} / {st.P99FrameTimeMs:0.0} / {st.P999FrameTimeMs:0.0} ms");
        Row(h, "Worst frame", $"{st.MaxFrameTimeMs:0.0} ms");
        Row(h, "Frame-to-frame change (avg)", $"{st.MeanAbsDeltaMs:0.00} ms");
        Row(h, "Stutter frames", $"{st.StutterCount}");
        if (st.GpuBusyRatio is double r) Row(h, "GPU busy share of frame", $"{r:P0}");
        Row(h, "Presentation mode", mode);
        Row(h, "Process", $"{cap.Application} (PID {cap.ProcessId})");
        h.Append("</table><table>");
        if (t != null)
        {
            var gpuUtil = t.Gpu.Where(g => g.UtilPct != null).Select(g => g.UtilPct!.Value).ToList();
            var gpuTemp = t.Gpu.Where(g => g.TempC != null).Select(g => g.TempC!.Value).ToList();
            var gpuClock = t.Gpu.Where(g => g.ClockMhz != null).Select(g => g.ClockMhz!.Value).ToList();
            var gpuPower = t.Gpu.Where(g => g.PowerW != null).Select(g => g.PowerW!.Value).ToList();
            if (gpuUtil.Count > 0) Row(h, "GPU utilization (avg)", $"{gpuUtil.Average():0}%");
            if (gpuClock.Count > 0) Row(h, "GPU clock (avg)", $"{gpuClock.Average():0} MHz");
            if (gpuTemp.Count > 0) Row(h, "GPU temperature (max)", $"{gpuTemp.Max():0} °C");
            if (gpuPower.Count > 0) Row(h, "GPU power (avg)", $"{gpuPower.Average():0} W");
            if (t.Cpu.Count > 0)
            {
                Row(h, "CPU utilization (avg)", $"{t.Cpu.Average(c => c.UtilPct):0}%");
                var maxCore = t.Cpu.Select(c => c.MaxCoreMhz).OrderBy(x => x).ToArray();
                Row(h, "CPU fastest core (median / p90)", $"{FrameStats.Percentile(maxCore, 0.5):0} / {FrameStats.Percentile(maxCore, 0.9):0} MHz");
            }
            var top = t.Processes.Where(p => !d.Context.Kb.IgnoredProcesses.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Take(5);
            Row(h, "Top CPU users", string.Join(", ", top.Select(p => $"{p.Name} {p.CoresUsed:0.0}c")));
            foreach (var w in t.Warnings.Distinct().Take(5)) Row(h, "Telemetry note", w);
        }
        else
        {
            Row(h, "Telemetry", "Not available: this session was imported from a PresentMon CSV, so clock/temperature/background checks were skipped.");
        }
        if (cap.OtherSources.Count > 0)
            Row(h, "Other apps presenting frames", string.Join(", ", cap.OtherSources.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key)));
        h.Append("</table></div></section>");
    }

    static void AppendSystem(StringBuilder h, Diagnosis d)
    {
        var s = d.Session.System;
        h.Append("<section><h2>System</h2><table>");
        Row(h, "CPU", $"{s.CpuName} ({s.LogicalProcessors} threads)");
        Row(h, "Motherboard / BIOS", $"{s.Motherboard} · {s.BiosVersion}{(s.BiosDate is DateTime bd ? $" ({bd:yyyy-MM-dd})" : "")}");
        if (s.Memory.Count > 0)
        {
            var m = s.Memory[0];
            Row(h, "Memory", $"{s.Memory.Sum(x => x.CapacityGb):0} GB ({s.Memory.Count}×{m.CapacityGb:0} GB) {(m.IsDdr5 ? "DDR5" : "")} · {m.PartNumber} · running at {m.ConfiguredMts} MT/s");
        }
        if (s.Gpu != null) Row(h, "GPU", $"{s.Gpu.Name} · driver {s.Gpu.DriverVersion}{(s.Gpu.DriverDate is DateTime dd ? $" ({dd:yyyy-MM-dd})" : "")}");
        Row(h, "Display", $"{s.Resolution} @ {s.RefreshRateHz} Hz");
        Row(h, "Power plan", s.PowerPlanName ?? "unknown");
        if (s.IsAmdCpu) Row(h, "AMD chipset driver", s.ChipsetDriverVersion ?? "not installed");
        Row(h, "Windows", s.OsVersion);
        foreach (var w in s.ProbeWarnings) Row(h, "Probe note", w);
        h.Append("</table></section>");
    }

    static void AppendMethod(StringBuilder h)
    {
        h.Append("<section class=\"method\"><h2>How this report works</h2><p>");
        h.Append("Frame times come from PresentMon, which records every frame the game presents (not a sampled average). ");
        h.Append("1% and 0.1% lows are the frame rates matching the 99th and 99.9th percentile frame times. ");
        h.Append("Findings come from a fixed, hand-written rules table, not machine learning: each one lists the measurement that triggered it. ");
        h.Append("Expected gains marked “estimate” are starting ranges that are still being validated with real before/after tests — your re-test is the real answer. ");
        h.Append("Frame Time Doctor never changes settings on your PC; you stay in control of every fix.</p></section>");
    }

    // ---- Chart ------------------------------------------------------------------------------

    const int W = 1000, H = 280, PadL = 46, PadR = 10, PadT = 10, PadB = 26;

    internal static string FrameTimeChart(IReadOnlyList<FrameRecord> frames, IReadOnlyList<FrameRecord>? before)
    {
        var all = before != null ? frames.Concat(before) : frames;
        var sorted = all.Select(f => f.FrameTimeMs).OrderBy(x => x).ToArray();
        var median = FrameStats.Percentile(sorted, 0.5);
        var yMax = NiceCeiling(Math.Max(FrameStats.Percentile(sorted, 0.999) * 1.25, median * 2.2));
        var duration = Math.Max(frames.Sum(f => f.FrameTimeMs), before?.Sum(f => f.FrameTimeMs) ?? 0) / 1000.0;

        double X(double sec) => PadL + sec / duration * (W - PadL - PadR);
        double Y(double ms) => PadT + (1 - Math.Min(ms, yMax) / yMax) * (H - PadT - PadB);

        var svg = new StringBuilder();
        svg.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Frame time over the run\" class=\"chart\">");
        var step = NiceStep(yMax / 5);
        for (double v = 0; v <= yMax + 1e-9; v += step)
        {
            svg.Append($"<line x1=\"{PadL}\" x2=\"{W - PadR}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\" class=\"grid\"/>");
            svg.Append($"<text x=\"{PadL - 6}\" y=\"{F(Y(v) + 4)}\" class=\"axis\" text-anchor=\"end\">{v:0.#}</text>");
        }
        var tStep = NiceStep(duration / 8);
        for (double t = 0; t <= duration + 1e-9; t += tStep)
            svg.Append($"<text x=\"{F(X(t))}\" y=\"{H - 6}\" class=\"axis\" text-anchor=\"middle\">{t:0}s</text>");
        svg.Append($"<text x=\"{PadL + 4}\" y=\"{PadT + 12}\" class=\"axis\">ms</text>");

        if (before != null) svg.Append(Series(before, "before", X, Y));
        svg.Append(Series(frames, "now", X, Y));
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Min/max per pixel column keeps every spike visible without drawing 20k points.</summary>
    static string Series(IReadOnlyList<FrameRecord> frames, string cls, Func<double, double> x, Func<double, double> y)
    {
        const int columns = W - PadL - PadR;
        var total = frames.Sum(f => f.FrameTimeMs);
        var lo = new double[columns]; var hi = new double[columns];
        Array.Fill(lo, double.MaxValue); Array.Fill(hi, double.MinValue);
        double t = 0;
        foreach (var f in frames)
        {
            t += f.FrameTimeMs;
            int c = Math.Clamp((int)(t / total * columns), 0, columns - 1);
            lo[c] = Math.Min(lo[c], f.FrameTimeMs);
            hi[c] = Math.Max(hi[c], f.FrameTimeMs);
        }
        var path = new StringBuilder();
        var secPerCol = total / 1000.0 / columns;
        for (int c = 0; c < columns; c++)
        {
            if (hi[c] == double.MinValue) continue;
            var px = F(x((c + 0.5) * secPerCol));
            path.Append(path.Length == 0 ? 'M' : 'L').Append(px).Append(',').Append(F(y(lo[c])));
            path.Append('L').Append(px).Append(',').Append(F(y(hi[c])));
        }
        return $"<path d=\"{path}\" class=\"series {cls}\"/>";
    }

    static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * mag;
    }

    static double NiceCeiling(double v)
    {
        var step = NiceStep(v / 5);
        return Math.Ceiling(v / step) * step;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    static void Tile(StringBuilder h, string label, string value, string unit, string help, bool primary = false) =>
        h.Append($"<div class=\"tile{(primary ? " primary" : "")}\" title=\"{E(help)}\"><div class=\"tl\">{E(label)}</div><div class=\"tv\">{E(value)}<span class=\"u\">{E(unit)}</span></div><div class=\"th\">{E(help)}</div></div>");

    static void DeltaTile(StringBuilder h, string label, double before, double after, string unit, bool higherIsBetter, double noisePct)
    {
        var pct = Comparison.DeltaPct(before, after);
        var better = higherIsBetter ? pct > 0 : pct < 0;
        var cls = Math.Abs(pct) < noisePct ? "noise" : better ? "better" : "worse";
        var tag = cls == "noise" ? "within noise" : $"{(pct >= 0 ? "+" : "")}{pct:0.0}%";
        h.Append($"<div class=\"tile\"><div class=\"tl\">{E(label)}</div><div class=\"tv\">{after:0.#}<span class=\"u\">{E(unit)}</span></div>");
        h.Append($"<div class=\"delta {cls}\">{before:0.#} → {after:0.#} · {tag}</div></div>");
    }

    static void Row(StringBuilder h, string k, string v) => h.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");

    static string GameName(Session s) => s.Info.GameName ?? Path.GetFileNameWithoutExtension(s.Info.ProcessName ?? s.Capture.Application);

    static string BottleneckLabel(Bottleneck b) => b switch
    {
        Bottleneck.Gpu => "GPU",
        Bottleneck.Cpu => "CPU",
        Bottleneck.Mixed => "CPU + GPU",
        Bottleneck.Capped => "Frame cap",
        _ => "Unknown",
    };

    static string BottleneckHelp(Bottleneck b) => b switch
    {
        Bottleneck.Gpu => "The graphics card was the limit. CPU and memory fixes will show smaller gains in this scene.",
        Bottleneck.Cpu => "The CPU side (CPU, memory, background load) was the limit, so CPU and memory fixes matter most.",
        Bottleneck.Mixed => "The limit moved between CPU and GPU during the run.",
        Bottleneck.Capped => "A frame rate cap or V-Sync was holding frame rate steady.",
        _ => "Not enough data to tell what limited frame rate.",
    };

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    const string Css = """
        :root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5d6570;--line:#e3e6ea;--accent:#0b72e7;--accent-soft:#e6f0fd;
        --good:#11845b;--good-soft:#e5f5ee;--bad:#c2410c;--bad-soft:#fdeee6;--warn:#8a6100;--warn-soft:#fdf5dc;--series-before:#9aa3ad}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1216;--card:#171b21;--text:#e8ebef;--muted:#9aa3ad;--line:#2a3038;--accent:#5aa2ff;
        --accent-soft:#16263b;--good:#3ccf91;--good-soft:#12291f;--bad:#ff8a5c;--bad-soft:#2f1b12;--warn:#f0c14b;--warn-soft:#2c2410;--series-before:#5d6570}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.55 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:1040px;margin:0 auto;padding:28px 16px 48px}header{margin-bottom:8px}
        .brand{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--accent);font-weight:700}
        h1{font-size:28px;margin:4px 0}h1 .label{font-size:16px;font-weight:500;color:var(--muted);margin-left:6px}
        h2{font-size:18px;margin:0 0 12px}.sub,.note{color:var(--muted);margin:4px 0}.note{font-size:13px}
        section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:20px;margin:16px 0}
        .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-bottom:14px}
        .tile{border:1px solid var(--line);border-radius:10px;padding:12px}.tile.primary{border-color:var(--accent);background:var(--accent-soft)}
        .tl{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}.tv{font-size:26px;font-weight:700;font-variant-numeric:tabular-nums}
        .u{font-size:13px;font-weight:500;color:var(--muted);margin-left:3px}.th{font-size:12px;color:var(--muted);margin-top:2px}
        .delta{font-size:13px;margin-top:4px;padding:2px 8px;border-radius:99px;display:inline-block;font-variant-numeric:tabular-nums}
        .delta.better{background:var(--good-soft);color:var(--good)}.delta.worse{background:var(--bad-soft);color:var(--bad)}.delta.noise{background:var(--line);color:var(--muted)}
        figure{margin:0}.chart{width:100%;height:auto;display:block}.grid{stroke:var(--line);stroke-width:1}
        .axis{fill:var(--muted);font-size:11px}.series{fill:none;stroke-width:1.2;stroke-linejoin:round}.series.now{stroke:var(--accent)}
        .series.before{stroke:var(--series-before)}figcaption{font-size:12px;color:var(--muted);margin-top:6px}
        .key{display:inline-block;width:14px;height:3px;border-radius:2px;margin:0 5px 3px 8px;vertical-align:middle}.key.now{background:var(--accent)}.key.before{background:var(--series-before)}
        .count{font-size:13px;background:var(--accent-soft);color:var(--accent);border-radius:99px;padding:1px 9px;margin-left:6px;vertical-align:middle}
        .finding{display:flex;gap:14px;border-top:1px solid var(--line);padding:16px 0}.finding:first-of-type{border-top:0}
        .rank{flex:0 0 30px;height:30px;border-radius:50%;background:var(--accent);color:#fff;font-weight:700;display:grid;place-items:center}
        .body{flex:1;min-width:0}.finding h3{margin:2px 0 6px;font-size:17px}.meta{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin-bottom:8px}
        .badge{font-size:12px;border-radius:99px;padding:1px 9px;border:1px solid var(--line)}.conf-high{background:var(--good-soft);color:var(--good);border-color:transparent}
        .conf-medium{background:var(--warn-soft);color:var(--warn);border-color:transparent}.conf-low{color:var(--muted)}.cat{color:var(--muted)}
        .impact{font-size:13px;font-weight:600}.impact i{font-weight:400;color:var(--muted)}.evidence{background:var(--bg);border-radius:8px;padding:8px 12px}
        .details{margin:6px 0 10px;padding-left:20px;font-size:14px}details summary{cursor:pointer;font-weight:600;margin:6px 0}details ol{margin:6px 0;padding-left:22px}
        details li,.steps li{margin:4px 0}code{background:var(--bg);border:1px solid var(--line);border-radius:6px;padding:1px 6px;font-size:13px;word-break:break-all}
        table{width:100%;border-collapse:collapse;font-size:14px}th,td{text-align:left;padding:6px 8px;border-top:1px solid var(--line);vertical-align:top}
        th{color:var(--muted);font-weight:500;width:42%}.cols{display:grid;grid-template-columns:1fr 1fr;gap:16px}
        .warn{background:var(--warn-soft);color:var(--warn);border-radius:8px;padding:8px 12px}.resolved{background:var(--good-soft);color:var(--good);border-radius:8px;padding:8px 12px;font-weight:600}
        .ok{background:var(--good-soft);color:var(--good);border-radius:8px;padding:10px 14px}.method p{color:var(--muted);font-size:14px;margin:0}
        footer{color:var(--muted);font-size:12px;text-align:center;margin-top:20px}
        @media (max-width:700px){.cols{grid-template-columns:1fr}h1{font-size:23px}section{padding:16px}.tv{font-size:22px}}
        """;
}
