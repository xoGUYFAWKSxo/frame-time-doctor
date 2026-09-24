using System.Globalization;
using System.Text.Json;

namespace FrameTimeDoctor.Core.Capture;

/// <summary>
/// Reads the results Cyberpunk 2077's built-in benchmark writes to
/// Documents\CD Projekt Red\Cyberpunk 2077\benchmarkResults\benchmark_*: a per-frame frames.csv and a
/// summary.json holding every graphics setting. Every player who has run the benchmark already has these.
/// </summary>
public static class CyberpunkBenchmarkParser
{
    public const string FramesFile = "frames.csv";
    public const string SummaryFile = "summary.json";
    public const string ProcessName = "Cyberpunk2077.exe";

    // Result fields, not settings: they differ run to run and must not trip the comparability check.
    static readonly HashSet<string> ResultFields = new(StringComparer.OrdinalIgnoreCase)
        { "averageFps", "minFps", "maxFps", "time", "frameNumber" };

    public static bool IsRunFolder(string dir) =>
        File.Exists(Path.Combine(dir, FramesFile)) && File.Exists(Path.Combine(dir, SummaryFile));

    /// <summary>Run folders directly inside a benchmarkResults folder, oldest first.</summary>
    public static IReadOnlyList<string> FindRunFolders(string dir) =>
        Directory.GetDirectories(dir).Where(IsRunFolder).OrderBy(d => d, StringComparer.Ordinal).ToList();

    public static FrameCapture ParseFrames(string framesCsv)
    {
        using var reader = new StreamReader(framesCsv);
        return ParseFrames(reader);
    }

    /// <summary>Columns: Frame index, Frame time (ms), then per-upscaler frame-generation columns.</summary>
    public static FrameCapture ParseFrames(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("Cyberpunk frames.csv is empty.");
        var cols = header.Split(',').Select(c => c.Trim()).ToArray();
        int ftCol = Array.FindIndex(cols, c => c.StartsWith("Frame time", StringComparison.OrdinalIgnoreCase));
        if (ftCol < 0) throw new InvalidDataException("Not a Cyberpunk frames.csv: no \"Frame time (ms)\" column.");

        var frames = new List<FrameRecord>();
        double t = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split(',');
            if (f.Length <= ftCol || !double.TryParse(f[ftCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ft) || ft <= 0)
                continue;
            // The game logs engine frame time only: no GPU busy, sync or presentation data.
            frames.Add(new FrameRecord(t, ft, null, null, null, 0, false, "Unknown", "Application"));
            t += ft;
        }
        if (frames.Count == 0) throw new InvalidDataException("Cyberpunk frames.csv contained no frames.");
        return new FrameCapture { Application = ProcessName, ProcessId = 0, Frames = frames };
    }

    /// <summary>Every setting in summary.json as text, for display and run-to-run comparability checks.</summary>
    public static Dictionary<string, string> ReadSettings(string summaryJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(summaryJson));
        var data = doc.RootElement.TryGetProperty("Data", out var d) ? d : doc.RootElement;
        return data.EnumerateObject()
            .Where(p => !ResultFields.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
    }

    /// <summary>Run start time from a "benchmark_2026-09-24_11-24-23" folder name.</summary>
    public static DateTime? RunStartTime(string runFolder) =>
        DateTime.TryParseExact(Path.GetFileName(runFolder).Replace("benchmark_", ""), "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>The few settings a player recognises, in display order.</summary>
    public static IEnumerable<(string Label, string Value)> KeySettings(IReadOnlyDictionary<string, string> s)
    {
        string? Get(string k) => s.TryGetValue(k, out var v) ? v : null;
        if (Get("presetName") is { } preset) yield return ("Preset", preset);
        if (Get("renderWidth") is { } w && Get("renderHeight") is { } h) yield return ("Resolution", $"{w}x{h}");
        var upscaler = Get("DLSSEnabled") == "true" ? "DLSS" : Get("FSR4Enabled") == "true" ? "FSR 4" : Get("FSR3Enabled") == "true" ? "FSR 3"
                     : Get("XeSSEnabled") == "true" ? "XeSS" : Get("DLAAEnabled") == "true" ? "DLAA" : "Off";
        yield return ("Upscaler", upscaler);
        var frameGen = Get("DLSSMultiFrameGenEnabled") == "true" ? "DLSS Multi Frame Gen"
                     : Get("DLSSFrameGenEnabled") == "true" ? "DLSS Frame Gen"
                     : Get("FSR3FrameGenEnabled") == "true" || Get("XeSSFrameGenEnabled") == "true" ? "On" : "Off";
        yield return ("Frame generation", frameGen);
        if (Get("rayTracedPathTracingEnabled") == "true") yield return ("Ray tracing", "Path tracing");
        else if (Get("rayTracingEnabled") is { } rt) yield return ("Ray tracing", rt == "true" ? "On" : "Off");
        if (Get("verticalSync") is { } vs) yield return ("V-Sync", vs == "true" ? "On" : "Off");
        if (Get("gameVersion") is { } ver) yield return ("Game version", ver);
    }
}
