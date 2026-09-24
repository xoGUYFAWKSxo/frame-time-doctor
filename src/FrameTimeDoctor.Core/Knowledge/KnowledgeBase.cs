using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FrameTimeDoctor.Core.Knowledge;

/// <summary>
/// The expert-authored half of the rules table: what each finding means, how to fix it and how much it
/// is expected to help, plus reference data (CPU specs, driver floors, known background offenders).
/// Detection logic lives in code (Rules/); everything a hardware expert would want to tune lives here.
/// </summary>
public sealed class KnowledgeBase
{
    public string Version { get; set; } = "";
    public List<KbRule> Rules { get; set; } = new();
    public List<CpuSpec> Cpus { get; set; } = new();
    public DriverPolicy Drivers { get; set; } = new();
    public List<KnownProcess> BackgroundOffenders { get; set; } = new();
    public List<string> IgnoredProcesses { get; set; } = new();

    public KbRule Rule(string id) =>
        Rules.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException($"Knowledge base has no rule '{id}'.");

    /// <summary>Longest model name that appears as a whole word in the CPU name ("9950X3D" beats "9950X").</summary>
    public CpuSpec? MatchCpu(string cpuName) =>
        Cpus.OrderByDescending(c => c.Model.Length)
            .FirstOrDefault(c => Regex.IsMatch(cpuName, $@"\b{Regex.Escape(c.Model)}\b", RegexOptions.IgnoreCase));

    public KnownProcess? KnownOffender(string processName) =>
        BackgroundOffenders.FirstOrDefault(o => string.Equals(o.Process, processName, StringComparison.OrdinalIgnoreCase));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static KnowledgeBase Load(string? path = null)
    {
        path ??= DefaultPath();
        return JsonSerializer.Deserialize<KnowledgeBase>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException($"Could not parse knowledge base at {path}.");
    }

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("FTD_KNOWLEDGE");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(AppContext.BaseDirectory, "knowledge", "rules.json");
    }
}

public sealed class KbRule
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>settings | drivers | software | cooling | display</summary>
    public string Category { get; set; } = "";
    public string Why { get; set; } = "";
    public List<string> Fix { get; set; } = new();
    public ImpactRange Impact { get; set; } = new();
    /// <summary>False until the impact range has been confirmed with real before/after captures.</summary>
    public bool Validated { get; set; }
}

public sealed class ImpactRange
{
    public double MinPct { get; set; }
    public double MaxPct { get; set; }
    /// <summary>What the percentage applies to, e.g. "1% low FPS".</summary>
    public string Metric { get; set; } = "1% low FPS";
}

public sealed class CpuSpec
{
    public string Model { get; set; } = "";
    public int Cores { get; set; }
    public int MaxBoostMhz { get; set; }
    public bool X3d { get; set; }
    public int Ccds { get; set; } = 1;
}

public sealed class DriverPolicy
{
    /// <summary>Oldest NVIDIA driver considered current. Null = only use driver age.</summary>
    public string? NvidiaMinimumVersion { get; set; }
    public int NvidiaMaxAgeDays { get; set; } = 150;
    public string? AmdChipsetMinimumVersion { get; set; }
    public int AmdChipsetMaxAgeDays { get; set; } = 365;
}

public sealed class KnownProcess
{
    public string Process { get; set; } = "";
    public string Label { get; set; } = "";
    public string Advice { get; set; } = "";
}
