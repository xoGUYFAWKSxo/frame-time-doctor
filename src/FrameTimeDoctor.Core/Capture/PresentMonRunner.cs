using System.Diagnostics;
using System.Security.Principal;

namespace FrameTimeDoctor.Core.Capture;

/// <summary>Thin wrapper around the PresentMon console app for a timed, single-process capture.</summary>
public sealed class PresentMonRunner
{
    const string SessionName = "FrameTimeDoctor";

    public string ExePath { get; }

    public PresentMonRunner(string exePath) => ExePath = exePath;

    /// <summary>Finds PresentMon: FTD_PRESENTMON env var, then tools\ next to the app, then PATH.</summary>
    public static PresentMonRunner Locate()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("FTD_PRESENTMON"),
            Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p, "PresentMon.exe")));

        var found = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        return found != null
            ? new PresentMonRunner(found)
            : throw new FileNotFoundException("PresentMon.exe not found. Put it in tools\\ next to ftd.exe or set FTD_PRESENTMON.");
    }

    /// <summary>ETW trace sessions need admin or membership in "Performance Log Users".</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator)
            || principal.IsInRole(new SecurityIdentifier("S-1-5-32-559")); // BUILTIN\Performance Log Users
    }

    public async Task<PresentMonResult> CaptureAsync(string processName, int seconds, string outputCsv, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "--process_name", processName,
            "--output_file", outputCsv,
            "--timed", seconds.ToString(),
            "--terminate_after_timed",
            "--no_console_stats",
            "--stop_existing_session",
            "--session_name", SessionName,
        }) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PresentMon.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* already gone */ }
            throw;
        }

        var output = (await stdout) + (await stderr);
        return new PresentMonResult(proc.ExitCode, output, File.Exists(outputCsv) ? outputCsv : null);
    }
}

public sealed record PresentMonResult(int ExitCode, string Output, string? CsvPath)
{
    public bool AccessDenied => Output.Contains("access denied", StringComparison.OrdinalIgnoreCase);
    public bool Succeeded => CsvPath != null && ExitCode == 0;
}
