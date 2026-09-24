namespace FrameTimeDoctor.Core.Capture;

/// <summary>One presented frame, normalized from whichever PresentMon CSV flavour produced it.</summary>
public sealed record FrameRecord(
    double CpuStartMs,
    double FrameTimeMs,
    double? DisplayedTimeMs,
    double? CpuBusyMs,
    double? GpuBusyMs,
    int SyncInterval,
    bool AllowsTearing,
    string PresentMode,
    string FrameType)
{
    public bool WasDisplayed => DisplayedTimeMs is > 0;
}

/// <summary>Per-frame data for the single swap chain we care about (the game's).</summary>
public sealed class FrameCapture
{
    public required string Application { get; init; }
    public required int ProcessId { get; init; }
    public required IReadOnlyList<FrameRecord> Frames { get; init; }

    /// <summary>Other swap chains seen in the CSV (overlays, launchers, DWM) and how many frames each had.</summary>
    public IReadOnlyDictionary<string, int> OtherSources { get; init; } = new Dictionary<string, int>();

    public double DurationSeconds => Frames.Sum(f => f.FrameTimeMs) / 1000.0;
}
