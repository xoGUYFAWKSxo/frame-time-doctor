using System.Globalization;

namespace FrameTimeDoctor.Core.Capture;

/// <summary>
/// Parses PresentMon CSV output. Columns are looked up by name so the 1.x, 2.x and current
/// (2.4+) layouts all work, as do CSVs exported by tools that embed PresentMon.
/// </summary>
public static class PresentMonCsvParser
{
    // Present-to-present interval is the frame time players feel; MsBetweenAppStart is the fallback.
    static readonly string[] FrameTimeColumns = { "MsBetweenPresents", "FrameTime", "MsBetweenAppStart" };
    static readonly string[] CpuStartMsColumns = { "CPUStartTime", "CPUStartQPCTime" };
    static readonly string[] DisplayedColumns = { "DisplayedTime", "MsBetweenDisplayChange" };
    static readonly string[] CpuBusyColumns = { "MsCPUBusy", "CPUBusy" };
    static readonly string[] GpuBusyColumns = { "MsGPUBusy", "GPUBusy" };

    public static FrameCapture Parse(string path, string? processName = null)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, processName);
    }

    public static FrameCapture Parse(TextReader reader, string? processName = null)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("PresentMon CSV is empty.");
        var cols = header.Split(',').Select(c => c.Trim()).ToArray();

        int Col(params string[] names)
        {
            foreach (var name in names)
            {
                var idx = Array.FindIndex(cols, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) return idx;
            }
            return -1;
        }

        int app = Col("Application"), pid = Col("ProcessID"), swap = Col("SwapChainAddress");
        int frameTime = Col(FrameTimeColumns);
        if (frameTime < 0)
            throw new InvalidDataException($"No frame time column found. Expected one of: {string.Join(", ", FrameTimeColumns)}.");
        int startMs = Col(CpuStartMsColumns), startSec = Col("TimeInSeconds");
        int displayed = Col(DisplayedColumns), dropped = Col("Dropped");
        int cpuBusy = Col(CpuBusyColumns), gpuBusy = Col(GpuBusyColumns);
        int sync = Col("SyncInterval"), tearing = Col("AllowsTearing"), mode = Col("PresentMode"), type = Col("FrameType");

        var groups = new Dictionary<(string App, int Pid, string Swap), List<FrameRecord>>();
        var runningStart = new Dictionary<(string, int, string), double>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');

            var appName = Str(f, app) ?? "<unknown>";
            if (processName != null && !MatchesProcess(appName, processName)) continue;

            var ft = Num(f, frameTime);
            if (ft is not > 0) continue; // first frame of a swap chain has no interval

            var key = (appName, (int)(Num(f, pid) ?? 0), Str(f, swap) ?? "");
            double start;
            if (Num(f, startMs) is double ms) start = ms;
            else if (Num(f, startSec) is double s) start = s * 1000.0;
            else
            {
                runningStart.TryGetValue(key, out start);
                runningStart[key] = start + ft.Value;
            }

            double? shown = Num(f, displayed);
            if (dropped >= 0 && Str(f, dropped) == "1") shown = null;

            var record = new FrameRecord(
                start, ft.Value, shown, Num(f, cpuBusy), Num(f, gpuBusy),
                (int)(Num(f, sync) ?? 0),
                Str(f, tearing) == "1",
                Str(f, mode) ?? "Unknown",
                Str(f, type) ?? "Application");

            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<FrameRecord>();
            list.Add(record);
        }

        if (groups.Count == 0)
            throw new InvalidDataException(processName == null
                ? "PresentMon CSV contained no frames."
                : $"PresentMon CSV contained no frames for '{processName}'.");

        // The game is the swap chain that presented the most frames; everything else is noise we report.
        var main = groups.MaxBy(g => g.Value.Count);
        return new FrameCapture
        {
            Application = main.Key.App,
            ProcessId = main.Key.Pid,
            Frames = main.Value.OrderBy(r => r.CpuStartMs).ToList(),
            OtherSources = groups.Where(g => !g.Key.Equals(main.Key))
                .GroupBy(g => g.Key.App)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Value.Count)),
        };
    }

    static bool MatchesProcess(string app, string wanted) =>
        string.Equals(app, wanted, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileNameWithoutExtension(app), Path.GetFileNameWithoutExtension(wanted), StringComparison.OrdinalIgnoreCase);

    static string? Str(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim() : null;

    static double? Num(string[] f, int i)
    {
        var s = Str(f, i);
        if (string.IsNullOrEmpty(s) || s == "NA") return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
