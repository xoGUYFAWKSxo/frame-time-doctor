using System.Text.Json;
using System.Text.RegularExpressions;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Knowledge;
using FrameTimeDoctor.Core.Telemetry;

namespace FrameTimeDoctor.Core.Sessions;

public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? GameId { get; set; }
    public string? GameName { get; set; }
    public string? ProcessName { get; set; }
    public DateTime StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>"capture" (recorded by ftd) or "import" (an existing PresentMon CSV).</summary>
    public string Source { get; set; } = "capture";
    public string ToolVersion { get; set; } = "";
}

/// <summary>
/// One benchmark pass on disk: session.json, system.json, telemetry.json (captures only) and presentmon.csv.
/// Sessions are plain files so they can be zipped and shared for support or beta feedback.
/// </summary>
public sealed class Session
{
    public const string CsvFile = "presentmon.csv";
    public const string ReportFile = "report.html";

    public required string Directory { get; init; }
    public required SessionInfo Info { get; init; }
    public required SystemSnapshot System { get; init; }
    public TelemetryLog? Telemetry { get; init; }

    public string CsvPath => Path.Combine(Directory, CsvFile);
    public string ReportPath => Path.Combine(Directory, ReportFile);

    FrameCapture? _capture;
    public FrameCapture Capture => _capture ??= PresentMonCsvParser.Parse(CsvPath, Info.ProcessName);
}

public static class SessionStore
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FTD_SESSIONS");
            return !string.IsNullOrEmpty(env) ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FrameTimeDoctor", "sessions");
        }
    }

    public static string NewDirectory(string label)
    {
        var slug = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var dir = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{(slug.Length > 0 ? slug : "run")}");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Save(string dir, SessionInfo info, SystemSnapshot system, TelemetryLog? telemetry)
    {
        info.Id = Path.GetFileName(dir);
        Write(Path.Combine(dir, "session.json"), info);
        Write(Path.Combine(dir, "system.json"), system);
        if (telemetry != null) Write(Path.Combine(dir, "telemetry.json"), telemetry);
    }

    /// <summary>Accepts a directory path, a session id (folder name), or "latest".</summary>
    public static Session Load(string reference)
    {
        var dir = Resolve(reference);
        var telemetryPath = Path.Combine(dir, "telemetry.json");
        return new Session
        {
            Directory = dir,
            Info = Read<SessionInfo>(Path.Combine(dir, "session.json")),
            System = Read<SystemSnapshot>(Path.Combine(dir, "system.json")),
            Telemetry = File.Exists(telemetryPath) ? Read<TelemetryLog>(telemetryPath) : null,
        };
    }

    public static IReadOnlyList<SessionInfo> List()
    {
        if (!System.IO.Directory.Exists(Root)) return Array.Empty<SessionInfo>();
        return System.IO.Directory.GetDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, "session.json")))
            .Select(d => Read<SessionInfo>(Path.Combine(d, "session.json")))
            .OrderByDescending(i => i.StartedAt)
            .ToList();
    }

    static string Resolve(string reference)
    {
        if (reference.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Root, List().FirstOrDefault()?.Id ?? throw new FileNotFoundException("No sessions recorded yet."));
        if (System.IO.Directory.Exists(reference)) return Path.GetFullPath(reference);
        var byId = Path.Combine(Root, reference);
        if (System.IO.Directory.Exists(byId)) return byId;
        var partial = List().FirstOrDefault(i => i.Id.Contains(reference, StringComparison.OrdinalIgnoreCase));
        return partial != null ? Path.Combine(Root, partial.Id) : throw new DirectoryNotFoundException($"Session '{reference}' not found in {Root}.");
    }

    static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, KnowledgeBase.JsonOptions));

    static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), KnowledgeBase.JsonOptions) ?? throw new InvalidDataException($"Could not read {path}.");
}
