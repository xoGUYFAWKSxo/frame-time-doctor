using System.Diagnostics;
using FrameTimeDoctor.Cli;
using FrameTimeDoctor.Core.Analysis;
using FrameTimeDoctor.Core.Benchmark;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Knowledge;
using FrameTimeDoctor.Core.Report;
using FrameTimeDoctor.Core.Rules;
using FrameTimeDoctor.Core.Sessions;
using FrameTimeDoctor.Core.Telemetry;

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
var opts = Args.Parse(args);

try
{
    return opts.Command switch
    {
        "probe" => Probe(),
        "games" => Games(),
        "capture" => await Capture(),
        "import" => Import(),
        "analyze" => Analyze(),
        "list" => List(),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or ArgumentException)
{
    Out.Error(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Out.Error("Cancelled.");
    return 130;
}

int Help()
{
    Console.WriteLine($"""
        Frame Time Doctor {version} — measurement-driven PC gaming diagnostics

        Usage:
          ftd probe                               Check system configuration (no benchmark needed)
          ftd games                               List supported benchmark titles
          ftd capture --game <id> [options]       Run a benchmark pass on a supported title
          ftd capture --process <exe> [options]   ...or on any game by process name
          ftd import <path> [options]             Diagnose existing logs: a PresentMon CSV, a Cyberpunk
                                                  benchmark run folder, or a whole benchmarkResults folder
          ftd analyze <session> [--baseline <session>]
          ftd list                                List recorded sessions

        Options:
          --duration <sec>       Capture length (default: game profile, else 60)
          --label <name>         Name for this pass, e.g. "baseline" or "expo-on"
          --baseline <session>   Compare against an earlier pass (id, folder or "latest")
          --start hotkey|now     Start on {Hotkey.Description} / Enter (default) or immediately
          --no-open              Don't open the report in the browser

        Sessions are stored in {SessionStore.Root}
        """);
    return opts.Command is null or "help" or "--help" or "-h" ? 0 : 1;
}

int Probe()
{
    var kb = KnowledgeBase.Load();
    Out.Step("Reading system configuration…");
    var system = SystemProbe.Take();
    PrintSystem(system);

    var ctx = new DiagnosticContext { System = system, Kb = kb };
    var findings = RuleEngine.CreateDefault().Run(ctx);
    Console.WriteLine();
    Out.Heading("Configuration checks");
    PrintFindings(findings, detailed: true);
    Console.WriteLine();
    Console.WriteLine("  Checks that need measurements (clock behaviour, thermals, background load, frame pacing)");
    Console.WriteLine("  run during a benchmark pass: ftd capture --game <id>   (see: ftd games)");
    return 0;
}

int Games()
{
    foreach (var g in GameProfile.LoadAll())
    {
        Out.Heading($"{g.Id,-10} {g.Name}");
        Console.WriteLine($"  Process: {g.Process} · {g.Mode} benchmark · {g.DurationSeconds}s capture · anti-cheat: {g.AntiCheat}");
        if (g.Notes != null) Console.WriteLine($"  {g.Notes}");
    }
    return 0;
}

async Task<int> Capture()
{
    var kb = KnowledgeBase.Load();
    GameProfile? game = null;
    if (opts.Get("game") is string gameId)
    {
        game = GameProfile.LoadAll().FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"Unknown game '{gameId}'. Run 'ftd games' to see supported titles.");
    }
    var process = opts.Get("process") ?? game?.Process ?? throw new ArgumentException("Specify --game <id> or --process <exe>.");
    if (!process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process += ".exe";
    var duration = opts.GetInt("duration") ?? game?.DurationSeconds ?? 60;
    var label = opts.Get("label") ?? (opts.Get("baseline") != null ? "retest" : "baseline");
    var baseline = opts.Get("baseline") is string b ? SessionStore.Load(b) : null;

    if (!PresentMonRunner.IsElevated())
    {
        Out.Error("Capturing frame times needs administrator rights (Windows restricts the event tracing PresentMon uses).");
        Console.WriteLine("  Open Terminal with \"Run as administrator\" and run the same command again.");
        return 2;
    }
    var presentMon = PresentMonRunner.Locate();

    Out.Step("Reading system configuration…");
    var system = SystemProbe.Take();

    Out.Heading($"Benchmark pass: {game?.Name ?? process} · {duration}s · \"{label}\"");
    if (game != null)
    {
        foreach (var step in game.Setup) Console.WriteLine($"  • {step}");
    }
    if (baseline != null)
        Console.WriteLine($"  • Comparing against \"{baseline.Info.Label}\" ({baseline.Info.Id}) — use the same settings and sequence as that run.");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var name = Path.GetFileNameWithoutExtension(process);
    if (Process.GetProcessesByName(name).Length == 0)
    {
        Out.Step($"Waiting for {process} to start… (Ctrl+C to cancel)");
        while (Process.GetProcessesByName(name).Length == 0)
            await Task.Delay(1000, cts.Token);
    }

    if (!string.Equals(opts.Get("start"), "now", StringComparison.OrdinalIgnoreCase))
    {
        Out.Step($"{process} is running. {game?.StartCue ?? "Start your benchmark sequence."}");
        Console.WriteLine($"  Press {Hotkey.Description} in-game (or Enter here) to start the {duration}s capture.");
        await Hotkey.WaitAsync(cts.Token);
    }

    Beep(1);
    var dir = SessionStore.NewDirectory(label);
    var csv = Path.Combine(dir, Session.CsvFile);
    Out.Step($"Capturing for {duration}s… keep the benchmark running.");

    var info = new SessionInfo
    {
        Label = label, GameId = game?.Id, GameName = game?.Name, ProcessName = process,
        StartedAt = DateTime.Now, Source = "capture", ToolVersion = version,
    };
    using var sampler = new TelemetrySampler(system.CpuBaseClockMhz);
    sampler.Start();
    var captureTask = presentMon.CaptureAsync(process, duration, csv, cts.Token);
    var progress = Task.Run(async () =>
    {
        var sw = Stopwatch.StartNew();
        while (!captureTask.IsCompleted)
        {
            Console.Write($"\r  {Math.Min(duration, (int)sw.Elapsed.TotalSeconds),3}/{duration}s ");
            await Task.Delay(500);
        }
        Console.WriteLine();
    });
    var result = await captureTask;
    var telemetry = sampler.Stop();
    await progress;
    Beep(2);

    if (!result.Succeeded)
    {
        Out.Error(result.AccessDenied
            ? "PresentMon was denied access to event tracing. Run the terminal as administrator."
            : $"PresentMon failed (exit code {result.ExitCode}): {result.Output.Trim()}");
        return 3;
    }

    info.DurationSeconds = telemetry.DurationSeconds;
    SessionStore.Save(dir, info, system, telemetry);
    return Report(SessionStore.Load(dir), baseline, kb);
}

int Import()
{
    var csv = opts.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("Usage: ftd import <presentmon.csv | cyberpunk run folder | benchmarkResults folder>");
    var kb = KnowledgeBase.Load();
    // Resolve before creating the new session, or "latest" would point at the new session itself.
    var baseline = opts.Get("baseline") is string b ? SessionStore.Load(b) : null;
    var label = opts.Get("label");

    // Cyberpunk: a benchmarkResults folder (every run in it), one run folder, or a file inside a run folder.
    var runDir = Directory.Exists(csv) ? csv : File.Exists(csv) ? Path.GetDirectoryName(Path.GetFullPath(csv))! : null;
    if (runDir == null) throw new FileNotFoundException($"Not found: {csv}");
    if (Directory.Exists(csv) && !CyberpunkBenchmarkParser.IsRunFolder(csv))
    {
        var runs = CyberpunkBenchmarkParser.FindRunFolders(csv);
        if (runs.Count == 0) throw new ArgumentException($"No Cyberpunk benchmark runs (frames.csv + summary.json) found in {csv}.");
        return ImportCyberpunkRuns(runs, label, kb);
    }
    if (CyberpunkBenchmarkParser.IsRunFolder(runDir) && (Directory.Exists(csv) || IsCyberpunkFile(csv)))
    {
        var imported = ImportCyberpunkRun(runDir, label, SystemProbe.Take());
        return Report(SessionStore.Load(imported), baseline, kb);
    }

    if (!File.Exists(csv)) throw new FileNotFoundException($"File not found: {csv}");
    var process = opts.Get("process");
    var capture = PresentMonCsvParser.Parse(csv, process); // validate before creating a session

    label ??= Path.GetFileNameWithoutExtension(csv);
    var dir = SessionStore.NewDirectory(label);
    File.Copy(csv, Path.Combine(dir, Session.CsvFile));
    Out.Step("Reading system configuration (imported CSVs are assumed to come from this PC)…");
    var system = SystemProbe.Take();
    SessionStore.Save(dir, new SessionInfo
    {
        Label = label, ProcessName = process ?? capture.Application, StartedAt = File.GetLastWriteTime(csv),
        DurationSeconds = capture.DurationSeconds, Source = "import", ToolVersion = version,
    }, system, telemetry: null);

    return Report(SessionStore.Load(dir), baseline, kb);
}

static bool IsCyberpunkFile(string path) =>
    Path.GetFileName(path) is var n &&
    (n.Equals(CyberpunkBenchmarkParser.FramesFile, StringComparison.OrdinalIgnoreCase) ||
     n.Equals(CyberpunkBenchmarkParser.SummaryFile, StringComparison.OrdinalIgnoreCase));

string ImportCyberpunkRun(string runDir, string? label, SystemSnapshot system)
{
    var capture = CyberpunkBenchmarkParser.ParseFrames(Path.Combine(runDir, CyberpunkBenchmarkParser.FramesFile));
    var summary = Path.Combine(runDir, CyberpunkBenchmarkParser.SummaryFile);
    label ??= Path.GetFileName(runDir);
    var dir = SessionStore.NewDirectory(label);
    File.Copy(Path.Combine(runDir, CyberpunkBenchmarkParser.FramesFile), Path.Combine(dir, CyberpunkBenchmarkParser.FramesFile));
    File.Copy(summary, Path.Combine(dir, CyberpunkBenchmarkParser.SummaryFile));
    SessionStore.Save(dir, new SessionInfo
    {
        Label = label, GameId = "cyberpunk", GameName = "Cyberpunk 2077", ProcessName = CyberpunkBenchmarkParser.ProcessName,
        StartedAt = CyberpunkBenchmarkParser.RunStartTime(runDir) ?? File.GetLastWriteTime(summary),
        DurationSeconds = capture.DurationSeconds, Source = "import", CaptureFormat = CaptureFormats.Cyberpunk,
        GameSettings = CyberpunkBenchmarkParser.ReadSettings(summary), ToolVersion = version,
    }, system, telemetry: null);
    return dir;
}

int ImportCyberpunkRuns(IReadOnlyList<string> runs, string? label, KnowledgeBase kb)
{
    Out.Step($"Importing {runs.Count} Cyberpunk benchmark runs (system details are read now, not from when each run happened)…");
    var system = SystemProbe.Take();
    Console.WriteLine();
    Console.WriteLine($"  {"Run",-32}{"Avg",7}{"1% low",8}{"0.1% low",10}{"Stutters/min",14}");
    int n = 1;
    foreach (var run in runs)
    {
        var session = SessionStore.Load(ImportCyberpunkRun(run, label != null ? $"{label}-{n++}" : null, system));
        var d = Diagnosis.Run(session, kb);
        File.WriteAllText(session.ReportPath, HtmlReportGenerator.Generate(d));
        Console.WriteLine($"  {Path.GetFileName(run),-32}{d.Stats.AvgFps,7:0.0}{d.Stats.OnePercentLowFps,8:0.0}{d.Stats.PointOnePercentLowFps,10:0.0}{d.Stats.StuttersPerMinute,14:0.0}");
    }
    Console.WriteLine();
    Console.WriteLine("  Compare any two with: ftd analyze <session> --baseline <session>   (ids: ftd list)");
    return 0;
}

int Analyze()
{
    var reference = opts.Positional.ElementAtOrDefault(0) ?? "latest";
    var baseline = opts.Get("baseline") is string b ? SessionStore.Load(b) : null;
    return Report(SessionStore.Load(reference), baseline, KnowledgeBase.Load());
}

int List()
{
    var sessions = SessionStore.List();
    if (sessions.Count == 0) { Console.WriteLine($"No sessions yet in {SessionStore.Root}."); return 0; }
    foreach (var s in sessions)
        Console.WriteLine($"  {s.Id,-48} {s.GameName ?? s.ProcessName,-18} {s.DurationSeconds,4:0}s  {s.Source,-8} {s.CaptureFormat}");
    return 0;
}

int Report(Session session, Session? baselineSession, KnowledgeBase kb)
{
    var current = Diagnosis.Run(session, kb);
    var baseline = baselineSession != null ? Diagnosis.Run(baselineSession, kb) : null;

    var retest = session.Info.GameId != null
        ? $"ftd capture --game {session.Info.GameId} --label <what-you-changed> --baseline {session.Info.Id}"
        : $"ftd capture --process {session.Capture.Application} --label <what-you-changed> --baseline {session.Info.Id}";
    File.WriteAllText(session.ReportPath, HtmlReportGenerator.Generate(current, baseline, retest));

    Console.WriteLine();
    PrintStats(current.Stats, baseline?.Stats, current.Context.Bottleneck);
    Console.WriteLine();
    Out.Heading("Findings (ranked by expected impact)");
    PrintFindings(current.Findings, detailed: false);
    if (baseline != null)
    {
        var cmp = new Comparison { Before = baseline, After = current };
        foreach (var f in cmp.Resolved) Out.Good($"Resolved since \"{baseline.Session.Info.Label}\": {f.Title}");
        foreach (var w in cmp.ComparabilityWarnings()) Out.Warn(w);
    }
    Console.WriteLine();
    Console.WriteLine($"  Report: {session.ReportPath}");
    Console.WriteLine($"  Next:   apply the top fix, then run: {retest}");

    if (!opts.Has("no-open"))
    {
        try { Process.Start(new ProcessStartInfo(session.ReportPath) { UseShellExecute = true }); }
        catch { /* headless or no browser association */ }
    }
    return 0;
}

// ---- Console output -----------------------------------------------------------------------

void PrintSystem(SystemSnapshot s)
{
    Console.WriteLine($"  CPU          {s.CpuName}");
    Console.WriteLine($"  Board/BIOS   {s.Motherboard} · {s.BiosVersion}");
    foreach (var m in s.Memory.GroupBy(m => m.PartNumber))
        Console.WriteLine($"  Memory       {m.Count()}× {m.First().CapacityGb:0} GB {m.Key} @ {m.First().ConfiguredMts} MT/s (rated: {RatedSpeedParser.Parse(m.Key)?.ToString() ?? "unknown"})");
    if (s.Gpu != null) Console.WriteLine($"  GPU          {s.Gpu.Name} · driver {s.Gpu.DriverVersion} ({s.Gpu.DriverDate:yyyy-MM-dd})");
    Console.WriteLine($"  Display      {s.Resolution} @ {s.RefreshRateHz} Hz");
    Console.WriteLine($"  Power plan   {s.PowerPlanName}");
    if (s.IsAmdCpu) Console.WriteLine($"  Chipset drv  {s.ChipsetDriverVersion ?? "not installed"}");
    foreach (var w in s.ProbeWarnings) Out.Warn(w);
}

void PrintStats(FrameStats st, FrameStats? before, Bottleneck bottleneck)
{
    string D(double b, double a) => $"  ({(a - b >= 0 ? "+" : "")}{Comparison.DeltaPct(b, a):0.0}%)";
    Out.Heading("Frame times");
    Console.WriteLine($"  1% low       {st.OnePercentLowFps,7:0.0} fps{(before != null ? D(before.OnePercentLowFps, st.OnePercentLowFps) : "")}");
    Console.WriteLine($"  0.1% low     {st.PointOnePercentLowFps,7:0.0} fps{(before != null ? D(before.PointOnePercentLowFps, st.PointOnePercentLowFps) : "")}");
    Console.WriteLine($"  Average      {st.AvgFps,7:0.0} fps{(before != null ? D(before.AvgFps, st.AvgFps) : "")}");
    Console.WriteLine($"  Std dev      {st.StdDevMs,7:0.00} ms");
    Console.WriteLine($"  Stutters     {st.StuttersPerMinute,7:0.0} /min");
    Console.WriteLine($"  Limited by   {bottleneck}");
}

void PrintFindings(IReadOnlyList<Finding> findings, bool detailed)
{
    if (findings.Count == 0) { Out.Good("No known misconfigurations detected."); return; }
    int i = 1;
    foreach (var f in findings)
    {
        Console.ForegroundColor = f.Hit.Confidence == Confidence.High ? ConsoleColor.Yellow : ConsoleColor.Gray;
        Console.WriteLine($"  {i++}. {f.Title}");
        Console.ResetColor();
        Console.WriteLine($"     {f.Hit.Confidence} confidence · expected +{f.ImpactMinPct:0.#}–{f.ImpactMaxPct:0.#}% {f.Rule.Impact.Metric}{(f.Rule.Validated ? "" : " (estimate)")}");
        if (!detailed) continue;
        Console.WriteLine($"     {f.Hit.Evidence}");
        foreach (var d in f.Hit.Details ?? Array.Empty<string>()) Console.WriteLine($"       • {d}");
    }
}

static void Beep(int count)
{
    for (int i = 0; i < count; i++) { try { Console.Beep(880, 120); } catch { /* no audio */ } Thread.Sleep(80); }
}

static class Out
{
    public static void Heading(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor(); }
    public static void Step(string s) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine($"» {s}"); Console.ResetColor(); }
    public static void Good(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"  ✓ {s}"); Console.ResetColor(); }
    public static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"  ! {s}"); Console.ResetColor(); }
    public static void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"error: {s}"); Console.ResetColor(); }
}

sealed class Args
{
    public string? Command { get; private init; }
    public List<string> Positional { get; } = new();
    readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] argv)
    {
        var a = new Args { Command = argv.FirstOrDefault()?.ToLowerInvariant() };
        for (int i = 1; i < argv.Length; i++)
        {
            if (argv[i].StartsWith("--"))
            {
                var key = argv[i][2..];
                var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--");
                a._flags[key] = hasValue ? argv[++i] : null;
            }
            else a.Positional.Add(argv[i]);
        }
        return a;
    }

    public bool Has(string key) => _flags.ContainsKey(key);
    public string? Get(string key) => _flags.TryGetValue(key, out var v) ? v : null;
    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;
}
