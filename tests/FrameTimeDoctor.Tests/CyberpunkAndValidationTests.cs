using System.Text.Json;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Sessions;
using static FrameTimeDoctor.Tests.Synthetic;

namespace FrameTimeDoctor.Tests;

public class CyberpunkBenchmarkParserTests
{
    // Header and first rows as written by Cyberpunk 2077 2.31.
    const string FramesCsv = """
        Frame index, Frame time (ms), DLSS Frame Generation (ms), FSR3 Frame Generation (ms), XeSS Frame Generation (ms), CPU memory (MB), GPU memory (MB)
        0, 13.95, 13.95, 13.95, 13.95
        1, 13.80, 13.80, 13.80, 13.80
        2, 13.84, 13.84, 13.84, 13.84
        """;

    [Fact]
    public void ParsesFrameTimes()
    {
        var cap = CyberpunkBenchmarkParser.ParseFrames(new StringReader(FramesCsv));
        Assert.Equal("Cyberpunk2077.exe", cap.Application);
        Assert.Equal(new[] { 13.95, 13.80, 13.84 }, cap.Frames.Select(f => f.FrameTimeMs));
        Assert.Equal(13.95 + 13.80, cap.Frames[2].CpuStartMs, 6);
        Assert.All(cap.Frames, f => Assert.Null(f.GpuBusyMs)); // the game doesn't log GPU busy
    }

    [Fact]
    public void RejectsOtherCsvs() =>
        Assert.Throws<InvalidDataException>(() => CyberpunkBenchmarkParser.ParseFrames(new StringReader("Application,MsBetweenPresents\nx,1")));

    [Fact]
    public void ReadsSettingsWithoutResultFields()
    {
        var dir = TempRun(presetName: "RayTracingUltra");
        var settings = CyberpunkBenchmarkParser.ReadSettings(Path.Combine(dir, "summary.json"));
        Assert.Equal("RayTracingUltra", settings["presetName"]);
        Assert.Equal("3440", settings["renderWidth"]);
        Assert.False(settings.ContainsKey("averageFps"));

        var key = CyberpunkBenchmarkParser.KeySettings(settings).ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal("3440x1440", key["Resolution"]);
        Assert.Equal("DLSS", key["Upscaler"]);
        Assert.Equal("Off", key["Frame generation"]);
    }

    [Fact]
    public void ParsesRunStartFromFolderName() =>
        Assert.Equal(new DateTime(2026, 9, 24, 11, 24, 23), CyberpunkBenchmarkParser.RunStartTime(@"C:\x\benchmark_2026-09-24_11-24-23"));

    [Fact]
    public void FindsRunFoldersInBenchmarkResults()
    {
        var root = Directory.CreateTempSubdirectory("ftd-results").FullName;
        TempRun(parent: root, name: "benchmark_2026-09-24_11-30-41");
        TempRun(parent: root, name: "benchmark_2026-09-24_11-24-23");
        Directory.CreateDirectory(Path.Combine(root, "not-a-run"));
        var runs = CyberpunkBenchmarkParser.FindRunFolders(root).Select(Path.GetFileName);
        Assert.Equal(new[] { "benchmark_2026-09-24_11-24-23", "benchmark_2026-09-24_11-30-41" }, runs);
    }

    [Fact]
    public void ComparisonWarnsWhenGameSettingsOrMeasurementDiffer()
    {
        var before = Diagnosis.Run(CyberpunkSession(TempRun(presetName: "RayTracingUltra")), Kb);
        var after = Diagnosis.Run(CyberpunkSession(TempRun(presetName: "Ultra")), Kb);
        var warnings = new Comparison { Before = before, After = after }.ComparabilityWarnings().ToList();
        Assert.Contains(warnings, w => w.Contains("In-game settings changed") && w.Contains("presetName"));
        Assert.Contains(warnings, w => w.Contains("imported"));

        var pm = CyberpunkSession(TempRun(presetName: "Ultra"));
        pm.Info.CaptureFormat = CaptureFormats.PresentMon;
        File.Copy(Path.Combine(pm.Directory, "frames.csv"), Path.Combine(pm.Directory, "presentmon.csv"));
        File.WriteAllText(Path.Combine(pm.Directory, "presentmon.csv"),
            "Application,ProcessID,SwapChainAddress,MsBetweenPresents\n" + string.Join('\n', Enumerable.Repeat("Cyberpunk2077.exe,1,0x1,14", 200)));
        var mixed = new Comparison { Before = before, After = Diagnosis.Run(pm, Kb) }.ComparabilityWarnings();
        Assert.Contains(mixed, w => w.Contains("measured differently"));
    }

    [Fact]
    public void SameSettingsProduceNoSettingsWarning()
    {
        var a = Diagnosis.Run(CyberpunkSession(TempRun(presetName: "Ultra")), Kb);
        var b = Diagnosis.Run(CyberpunkSession(TempRun(presetName: "Ultra")), Kb);
        Assert.DoesNotContain(new Comparison { Before = a, After = b }.ComparabilityWarnings(), w => w.Contains("In-game settings"));
    }

    static string TempRun(string presetName = "Ultra", string? parent = null, string? name = null)
    {
        var dir = parent != null ? Path.Combine(parent, name!) : Directory.CreateTempSubdirectory("ftd-run").FullName;
        Directory.CreateDirectory(dir);
        var rows = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"{i}, {13 + i % 5 * 0.4:0.00}, 0, 0, 0"));
        File.WriteAllText(Path.Combine(dir, "frames.csv"), "Frame index, Frame time (ms), DLSS Frame Generation (ms), FSR3 Frame Generation (ms), XeSS Frame Generation (ms)\n" + rows);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(new
        {
            RootType = "worldBenchmarkSummary",
            Data = new { gameVersion = "2.31", presetName, renderWidth = 3440, renderHeight = 1440, averageFps = 73.6 + Random.Shared.NextDouble(),
                         DLSSEnabled = true, DLSSFrameGenEnabled = false, rayTracingEnabled = true, verticalSync = false },
        }));
        return dir;
    }

    static Session CyberpunkSession(string runDir) => new()
    {
        Directory = runDir,
        Info = new SessionInfo
        {
            Id = Path.GetFileName(runDir), Label = "t", GameId = "cyberpunk", ProcessName = "Cyberpunk2077.exe", Source = "import",
            CaptureFormat = CaptureFormats.Cyberpunk, GameSettings = CyberpunkBenchmarkParser.ReadSettings(Path.Combine(runDir, "summary.json")),
        },
        System = Rig(),
    };
}

public class ValidationTests
{
    [Fact]
    public void EvidenceReferencesExistAndParse()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "knowledge");
        var cited = Kb.Rules.SelectMany(r => r.Evidence).ToList();
        Assert.NotEmpty(cited);
        foreach (var path in cited)
        {
            var file = Path.Combine(root, path);
            Assert.True(File.Exists(file), $"Missing evidence file {path}");
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("rule").ValueKind);
        }
    }

    [Fact]
    public void GpuBoundMemoryEstimateCoversMeasuredGain()
    {
        // Validation 2026-09-24: EXPO gave +4.8% 1% lows in a GPU-bound Cyberpunk run.
        var sys = Rig(s => s.Memory = new() { new("Kingston", "KF560C32-48", 4800, 4800, 48, 34) });
        var f = Run(Context(Capture(Jittery(3000, 10, 3)), sys, Telemetry())).Single();
        Assert.Equal(2.5, f.ImpactMinPct);
        Assert.Equal(7.5, f.ImpactMaxPct);
        Assert.InRange(4.8, f.ImpactMinPct, f.ImpactMaxPct);
    }
}
