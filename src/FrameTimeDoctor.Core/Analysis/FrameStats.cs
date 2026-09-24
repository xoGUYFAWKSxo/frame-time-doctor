using FrameTimeDoctor.Core.Capture;

namespace FrameTimeDoctor.Core.Analysis;

/// <summary>
/// Summary metrics for a capture. "1% low" and "0.1% low" are defined as the FPS equivalent of the
/// 99th / 99.9th percentile frame time, which is stable across capture lengths and matches CapFrameX "P1".
/// </summary>
public sealed record FrameStats(
    int FrameCount,
    double DurationSeconds,
    double AvgFps,
    double MedianFrameTimeMs,
    double P95FrameTimeMs,
    double P99FrameTimeMs,
    double P999FrameTimeMs,
    double MaxFrameTimeMs,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double StdDevMs,
    double MeanAbsDeltaMs,
    int StutterCount,
    double StuttersPerMinute,
    double? GpuBusyRatio)
{
    /// <summary>Coefficient of variation of frame time. Below ~0.06 the frame rate is almost certainly capped.</summary>
    public double FrameTimeCv => MeanFrameTimeMs > 0 ? StdDevMs / MeanFrameTimeMs : 0;

    public double MeanFrameTimeMs => AvgFps > 0 ? 1000.0 / AvgFps : 0;

    /// <summary>A stutter is a frame taking at least twice the local median and at least 8 ms longer than it.</summary>
    public const double StutterRatio = 2.0;
    public const double StutterMinExtraMs = 8.0;
    const int StutterWindow = 30;

    public static FrameStats Compute(FrameCapture capture)
    {
        var ft = capture.Frames.Select(f => f.FrameTimeMs).ToArray();
        if (ft.Length < 2) throw new InvalidOperationException("Need at least two frames to compute statistics.");

        var sorted = ft.OrderBy(x => x).ToArray();
        var totalMs = ft.Sum();
        var mean = totalMs / ft.Length;
        var p99 = Percentile(sorted, 0.99);
        var p999 = Percentile(sorted, 0.999);

        double variance = ft.Sum(x => (x - mean) * (x - mean)) / ft.Length;
        double meanAbsDelta = 0;
        for (int i = 1; i < ft.Length; i++) meanAbsDelta += Math.Abs(ft[i] - ft[i - 1]);
        meanAbsDelta /= ft.Length - 1;

        var stutters = StutterIndices(ft).Count;
        var durationMin = totalMs / 60_000.0;

        var gpuRatios = capture.Frames
            .Where(f => f.GpuBusyMs is > 0)
            .Select(f => Math.Min(1.0, f.GpuBusyMs!.Value / f.FrameTimeMs))
            .OrderBy(x => x).ToArray();

        return new FrameStats(
            ft.Length,
            totalMs / 1000.0,
            ft.Length / (totalMs / 1000.0),
            Percentile(sorted, 0.5),
            Percentile(sorted, 0.95),
            p99,
            p999,
            sorted[^1],
            1000.0 / p99,
            1000.0 / p999,
            Math.Sqrt(variance),
            meanAbsDelta,
            stutters,
            durationMin > 0 ? stutters / durationMin : 0,
            gpuRatios.Length > ft.Length / 2 ? Percentile(gpuRatios, 0.5) : null);
    }

    public static List<int> StutterIndices(IReadOnlyList<double> ft)
    {
        var result = new List<int>();
        var window = new List<double>(StutterWindow);
        for (int i = 0; i < ft.Count; i++)
        {
            if (window.Count >= 10)
            {
                var median = Percentile(window.OrderBy(x => x).ToArray(), 0.5);
                if (ft[i] >= median * StutterRatio && ft[i] - median >= StutterMinExtraMs) result.Add(i);
            }
            window.Add(ft[i]);
            if (window.Count > StutterWindow) window.RemoveAt(0);
        }
        return result;
    }

    /// <summary>Linear-interpolated percentile over an ascending array. p in [0,1].</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
