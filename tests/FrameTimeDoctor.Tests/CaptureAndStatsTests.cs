using FrameTimeDoctor.Core.Analysis;
using FrameTimeDoctor.Core.Capture;
using FrameTimeDoctor.Core.Telemetry;

namespace FrameTimeDoctor.Tests;

public class PresentMonCsvParserTests
{
    // Column layout of PresentMon 2.4+ default output.
    const string CurrentHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime," +
        "MsBetweenSimulationStart,MsPCLatency,MsBetweenPresents,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsInPresentAPI,MsGPULatency," +
        "MsGPUTime,MsGPUBusy,MsGPUWait,MsBetweenDisplayChange,MsUntilDisplayed,MsRenderPresentLatency,MsAnimationError,AnimationTime,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    static string Row(string app, int pid, string swap, double start, string between, string gpuBusy = "7.5", string displayChange = "8.3") =>
        $"{app},{pid},{swap},DXGI,0,512,1,Hardware: Independent Flip,{start},NA,NA,{between},8.3,4.1,4.2,0.2,0.5,7.9,{gpuBusy},0.4,{displayChange},9.0,8.0,0.1,{start},NA,NA";

    [Fact]
    public void ParsesCurrentLayoutAndPicksGameSwapChain()
    {
        var csv = string.Join('\n', new[]
        {
            CurrentHeader,
            Row("Cyberpunk2077.exe", 100, "0xA", 0, "NA"),   // first frame: no interval, skipped
            Row("Cyberpunk2077.exe", 100, "0xA", 8.3, "8.3"),
            Row("Discord.exe", 200, "0xB", 9, "16.6"),
            Row("Cyberpunk2077.exe", 100, "0xA", 16.6, "8.4", displayChange: "NA"),
            Row("Cyberpunk2077.exe", 100, "0xA", 25.0, "8.2"),
        });
        var cap = PresentMonCsvParser.Parse(new StringReader(csv));

        Assert.Equal("Cyberpunk2077.exe", cap.Application);
        Assert.Equal(3, cap.Frames.Count);
        Assert.Equal(8.4, cap.Frames[1].FrameTimeMs);
        Assert.False(cap.Frames[1].WasDisplayed);            // "NA" displayed time = dropped
        Assert.Equal(7.5, cap.Frames[0].GpuBusyMs);
        Assert.Equal("Hardware: Independent Flip", cap.Frames[0].PresentMode);
        Assert.Equal(1, cap.OtherSources["Discord.exe"]);
    }

    [Fact]
    public void ParsesRealPresentMon26Output()
    {
        // Verbatim header and first rows from a live PresentMon 2.6.0 capture of Cyberpunk 2077.
        const string csv = """
            Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency
            Cyberpunk2077.exe,30656,0x225D4BB6E30,DXGI,0,512,1,Hardware: Independent Flip,28.6901,NA,8.66510000000000,13.83310000000000,0.26980000000000,23.40610000000000,23.4061,20.3133,8.6466,8.3768,0.2698,17.9498,13.8331,13.8220,0.0111,NA,20.3133,NA,NA,NA
            Cyberpunk2077.exe,30656,0x225D4BB6E30,DXGI,0,512,1,Hardware: Independent Flip,41.0977,NA,12.40760000000000,14.05400000000000,0.25780000000000,25.05250000000000,25.0525,28.9599,12.3956,12.1378,0.2578,23.1363,14.0540,14.0420,0.0120,-5.4074,28.9599,NA,NA,NA
            """;
        var cap = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.Equal(2, cap.Frames.Count);
        Assert.Equal(20.3133, cap.Frames[0].CpuStartMs, 4);   // read from CPUStartTimeInMs, not reconstructed
        Assert.Equal(28.9599, cap.Frames[1].CpuStartMs, 4);
        Assert.Equal(8.6651, cap.Frames[0].FrameTimeMs, 4);
        Assert.Equal(13.822, cap.Frames[0].GpuBusyMs!.Value, 3);
        Assert.Equal(13.8331, cap.Frames[0].DisplayedTimeMs!.Value, 4);
    }

    [Fact]
    public void FiltersByProcessNameWithOrWithoutExtension()
    {
        var csv = string.Join('\n', CurrentHeader, Row("a.exe", 1, "0x1", 0, "5"), Row("b.exe", 2, "0x2", 0, "6"), Row("b.exe", 2, "0x2", 6, "6"));
        Assert.Equal("a.exe", PresentMonCsvParser.Parse(new StringReader(csv), "a").Application);
        Assert.Equal("b.exe", PresentMonCsvParser.Parse(new StringReader(csv)).Application); // most frames wins
    }

    [Fact]
    public void ParsesV1LayoutWithSecondsAndDroppedColumn()
    {
        var csv = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsUntilRenderComplete,MsUntilDisplayed\n" +
                  "game.exe,1,0x1,DXGI,1,0,0,Hardware: Legacy Flip,0,1.000,16.7,16.7,0.1,5,10\n" +
                  "game.exe,1,0x1,DXGI,1,0,0,Hardware: Legacy Flip,1,1.017,16.6,0,0.1,5,10\n";
        var cap = PresentMonCsvParser.Parse(new StringReader(csv));
        Assert.Equal(2, cap.Frames.Count);
        Assert.Equal(1000, cap.Frames[0].CpuStartMs, 3);
        Assert.Equal(1, cap.Frames[0].SyncInterval);
        Assert.False(cap.Frames[0].AllowsTearing);
        Assert.False(cap.Frames[1].WasDisplayed);
    }

    [Fact]
    public void RejectsCsvWithoutFrameTimeColumn()
    {
        Assert.Throws<InvalidDataException>(() => PresentMonCsvParser.Parse(new StringReader("Application,ProcessID\nx,1")));
    }
}

public class FrameStatsTests
{
    [Fact]
    public void SteadyFramesGiveEqualAverageAndLows()
    {
        var stats = FrameStats.Compute(Synthetic.Capture(Enumerable.Repeat(10.0, 1000)));
        Assert.Equal(100, stats.AvgFps, 3);
        Assert.Equal(100, stats.OnePercentLowFps, 3);
        Assert.Equal(0, stats.StdDevMs, 6);
        Assert.Equal(0, stats.StutterCount);
    }

    [Fact]
    public void OnePercentLowReflectsSpikesThatAverageHides()
    {
        // 2% of frames take 40 ms: average barely moves, 1% low collapses to 25 fps.
        var ft = Enumerable.Range(0, 5000).Select(i => i % 50 == 25 ? 40.0 : 10.0).ToList();
        var stats = FrameStats.Compute(Synthetic.Capture(ft));
        Assert.InRange(stats.AvgFps, 94, 95);
        Assert.Equal(25, stats.OnePercentLowFps, 1);
        Assert.Equal(100, stats.StutterCount);
        Assert.InRange(stats.StuttersPerMinute, 110, 116); // 100 stutters in 53 s
    }

    [Fact]
    public void SmallVariationIsNotAStutter()
    {
        var ft = Enumerable.Range(0, 1000).Select(i => i % 10 == 0 ? 14.0 : 7.0); // 2x but only +7 ms
        Assert.Equal(0, FrameStats.Compute(Synthetic.Capture(ft)).StutterCount);
    }

    [Fact]
    public void PercentileInterpolates()
    {
        Assert.Equal(2.5, FrameStats.Percentile(new[] { 1.0, 2, 3, 4 }, 0.5));
        Assert.Equal(4, FrameStats.Percentile(new[] { 1.0, 2, 3, 4 }, 1));
    }
}

public class TelemetryParsingTests
{
    [Fact]
    public void ParsesNvidiaSmiLineWithThrottleReasons()
    {
        var s = TelemetrySampler.ParseGpuLine("98, 2715, 14001, 83, 241.50, 250.00, Active, Not Active, Not Active", 1.5)!;
        Assert.Equal(98, s.UtilPct);
        Assert.Equal(2715, s.ClockMhz);
        Assert.Equal(83, s.TempC);
        Assert.True(s.ThermalSlowdown);
        Assert.False(s.PowerCapped);
    }

    [Fact]
    public void IgnoresNonNumericLines()
    {
        Assert.Null(TelemetrySampler.ParseGpuLine("utilization.gpu [%], clocks.gr [MHz], a, b, c, d", 0));
    }

    [Theory]
    [InlineData("32.0.16.1714", "617.14")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5222", "552.22")]
    public void ConvertsWindowsNvidiaDriverVersion(string windows, string nvidia)
    {
        Assert.Equal(nvidia, SystemProbe.NvidiaVersionFromWindows(windows));
    }

    [Fact]
    public void ProcessCpuDiffGroupsByNameAndIgnoresRecycledPids()
    {
        var start = new Dictionary<int, (string, TimeSpan)> { [1] = ("chrome", TimeSpan.FromSeconds(10)), [2] = ("chrome", TimeSpan.FromSeconds(5)), [3] = ("old", TimeSpan.Zero) };
        var end = new Dictionary<int, (string, TimeSpan)> { [1] = ("chrome", TimeSpan.FromSeconds(40)), [2] = ("chrome", TimeSpan.FromSeconds(35)), [3] = ("new", TimeSpan.FromSeconds(60)) };
        var usage = TelemetrySampler.DiffProcessCpu(start, end, 60);
        var chrome = Assert.Single(usage);
        Assert.Equal("chrome", chrome.Name);
        Assert.Equal(2, chrome.Instances);
        Assert.Equal(1.0, chrome.CoresUsed, 3);
    }
}
