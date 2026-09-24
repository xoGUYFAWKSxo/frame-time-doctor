using FrameTimeDoctor.Core.Rules;
using FrameTimeDoctor.Core.Telemetry;
using static FrameTimeDoctor.Tests.Synthetic;

namespace FrameTimeDoctor.Tests;

public class KnowledgeBaseTests
{
    [Fact]
    public void EveryRuleHasAKnowledgeBaseEntryAndViceVersa()
    {
        var engineIds = new IRule[]
        {
            new ThermalThrottlingRule(), new PowerPlanRule(), new MemoryProfileRule(), new CoreCountRule(), new CpuBoostRule(), new BackgroundProcessRule(),
            new FrameCapBelowRefreshRule(), new VsyncJudderRule(), new ComposedPresentationRule(), new GpuDriverRule(), new ChipsetDriverRule(),
        }.Select(r => r.Id).OrderBy(x => x);
        Assert.Equal(engineIds, Kb.Rules.Select(r => r.Id).OrderBy(x => x));
        Assert.All(Kb.Rules, r => { Assert.NotEmpty(r.Fix); Assert.True(r.Impact.MaxPct >= r.Impact.MinPct); });
    }

    [Theory]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", "9950X")]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", "9950X3D")]
    [InlineData("AMD Ryzen 5 7600 6-Core Processor", "7600")]
    [InlineData("AMD Ryzen 5 7600X 6-Core Processor", "7600X")]
    public void MatchesCpuModelsExactly(string name, string model) => Assert.Equal(model, Kb.MatchCpu(name)?.Model);

    [Theory]
    [InlineData("KF560C32-48", 6000)]
    [InlineData("KF552C40BBK2-32", 5200)]
    [InlineData("F5-6000J3038F16G", 6000)]
    [InlineData("CMK32GX5M2B6000C36", 6000)]
    [InlineData("CMH32GX5M2B6400C32", 6400)]
    [InlineData("FF3D532G6000HC38ADC01", 6000)]
    [InlineData("CP2K16G60C36U5B", 6000)]
    [InlineData("CT16G48C40U5", 4800)]
    [InlineData("PVV532G600C36K", 6000)]
    [InlineData("AX5U6000C3016G-DCLARBK", 6000)]
    [InlineData("M425R2GA3BB0-CQKOL", null)]
    [InlineData("", null)]
    public void DecodesRatedSpeedFromPartNumber(string part, int? expected) => Assert.Equal(expected, RatedSpeedParser.Parse(part));
}

public class ConfigurationRuleTests
{
    [Fact]
    public void FlagsExpoOffForKnownKit()
    {
        var sys = Rig(s => s.Memory = new() { new("Kingston", "KF560C32-48", 4800, 4800, 48, 34) });
        var f = Assert.Single(Run(Context(system: sys)));
        Assert.Equal("memory-profile-off", f.Rule.Id);
        Assert.Equal(Confidence.High, f.Hit.Confidence);
        Assert.Contains("6000", f.Hit.Evidence);
        Assert.Contains(f.FixSteps, s => s.Contains("rated 6000 MT/s"));
    }

    [Fact]
    public void NoMemoryFindingWhenRunningAtRatedSpeed() => Assert.Empty(Run(Context()));

    [Fact]
    public void UnknownDdr5KitAtJedecIsOnlyMediumConfidence()
    {
        var sys = Rig(s => s.Memory = new() { new("Samsung", "M425R2GA3BB0-CQKOL", 4800, 4800, 16, 34) });
        Assert.Equal(Confidence.Medium, Assert.Single(Run(Context(system: sys))).Hit.Confidence);
    }

    [Fact]
    public void UnknownKitAboveJedecIsNotFlagged()
    {
        var sys = Rig(s => s.Memory = new() { new("Samsung", "M425R2GA3BB0-CQKOL", 4800, 5600, 16, 34) });
        Assert.Empty(Run(Context(system: sys)));
    }

    [Fact]
    public void PowerSaverIsAlwaysFlagged()
    {
        var sys = Rig(s => { s.PowerPlanGuid = PowerPlanRule.PowerSaver; s.PowerPlanName = "Power saver"; });
        Assert.Equal("power-plan", Assert.Single(Run(Context(system: sys))).Rule.Id);
    }

    [Fact]
    public void BalancedIsNotFlaggedWithoutMeasuredClockDips()
    {
        var sys = Rig(s => { s.PowerPlanGuid = PowerPlanRule.Balanced; s.PowerPlanName = "Balanced"; });
        Assert.Empty(Run(Context(system: sys)));
        // CPU-bound, but clocks stay high: still fine.
        var cpuBound = Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5);
        Assert.DoesNotContain(Run(Context(cpuBound, sys, Telemetry(gpuUtil: 60, cpuMaxMhz: 5600))), f => f.Rule.Id == "power-plan");
    }

    [Fact]
    public void BalancedIsFlaggedWhenCpuBoundAndClocksDip()
    {
        var sys = Rig(s => { s.PowerPlanGuid = PowerPlanRule.Balanced; s.PowerPlanName = "Balanced"; });
        var cpuBound = Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5);
        var telemetry = Telemetry(gpuUtil: 60, cpuMhzAt: i => i % 3 == 0 ? 3000 : 5600);
        var findings = Run(Context(cpuBound, sys, telemetry));
        Assert.Contains(findings, f => f.Rule.Id == "power-plan");
        Assert.DoesNotContain(findings, f => f.Rule.Id == "pbo-stock"); // power plan explains it
    }

    [Fact]
    public void MissingChipsetDriverOnDualCcdX3dIsWeightedUp()
    {
        var x3d = Rig(s => { s.CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor"; s.ChipsetDriverVersion = null; });
        var plain = Rig(s => s.ChipsetDriverVersion = null);
        var fx = Assert.Single(Run(Context(system: x3d)));
        var fp = Assert.Single(Run(Context(system: plain)));
        Assert.True(fx.ImpactMaxPct > fp.ImpactMaxPct);
        Assert.Contains("V-Cache", fx.Hit.Evidence);
    }

    [Fact]
    public void FlagsDisabledCoresAndSmt()
    {
        // Matches the author's rig: firmware reports 8 cores, 32 MB L3 (one CCD), no SMT.
        var sys = Rig(s => { s.CpuCores = 8; s.LogicalProcessors = 8; s.CpuThreadCapacity = 32; s.CpuL3CacheKb = 32768; });
        var f = Assert.Single(Run(Context(system: sys)));
        Assert.Equal("cores-disabled", f.Rule.Id);
        Assert.Contains("only 8 of 16 cores are enabled and SMT", f.Hit.Evidence);
        Assert.Equal(Confidence.Low, f.Hit.Confidence); // exactly one CCD: possibly deliberate
        Assert.Contains(f.Hit.Details!, d => d.Contains("32 MB of L3") && d.Contains("msconfig cannot"));
        Assert.Contains(f.Hit.Details!, d => d.Contains("SMT is disabled in the BIOS"));
        Assert.DoesNotContain(f.Hit.Details!, d => d.Contains("Windows is limiting"));
    }

    [Fact]
    public void AttributesHiddenThreadsToWindowsWhenBiosEnablesAllCores()
    {
        var f = Assert.Single(Run(Context(system: Rig(s => { s.CpuCores = 16; s.LogicalProcessors = 24; }))));
        Assert.Contains(f.Hit.Details!, d => d.Contains("Windows is limiting") && d.Contains("msconfig"));
        Assert.DoesNotContain(f.Hit.Details!, d => d.Contains("BIOS: Windows settings"));
    }

    [Fact]
    public void FlagsSmtOffAtMediumConfidence()
    {
        var f = Assert.Single(Run(Context(system: Rig(s => { s.CpuCores = 16; s.LogicalProcessors = 16; }))));
        Assert.Equal(Confidence.Medium, f.Hit.Confidence);
        Assert.Contains("SMT", f.Hit.Evidence);
    }

    [Fact]
    public void FullThreadCountIsFine() => Assert.Empty(Run(Context(system: Rig(s => { s.CpuCores = 16; s.LogicalProcessors = 32; }))));

    [Fact]
    public void OldGpuDriverIsFlaggedByAge()
    {
        var sys = Rig(s => s.Gpu = s.Gpu! with { DriverDate = DateTime.Today.AddDays(-400) });
        Assert.Equal("gpu-driver-outdated", Assert.Single(Run(Context(system: sys))).Rule.Id);
    }
}

public class MeasuredRuleTests
{
    [Fact]
    public void HealthyGpuBoundRunHasNoFindings()
    {
        var cap = Capture(Jittery(6000, 10, 3));
        Assert.Empty(Run(Context(cap, telemetry: Telemetry())));
    }

    [Fact]
    public void GpuBoundRunDiscountsMemoryFinding()
    {
        var sys = Rig(s => s.Memory = new() { new("Kingston", "KF560C32-48", 4800, 4800, 48, 34) });
        var gpuBound = Run(Context(Capture(Jittery(3000, 10, 3)), sys, Telemetry()));
        var cpuBound = Run(Context(Capture(Jittery(3000, 10, 3), gpuBusy: ft => ft * 0.5), sys, Telemetry(gpuUtil: 60)));
        Assert.True(gpuBound.Single().ImpactMaxPct < cpuBound.Single().ImpactMaxPct);
    }

    [Fact]
    public void DetectsVsyncHalfRateJudder()
    {
        // 120 Hz: most frames at 8.33 ms, 8% held for 16.67 ms.
        var ft = Enumerable.Range(0, 3000).Select(i => i % 12 == 0 ? 16.67 : 8.33);
        var f = Assert.Single(Run(Context(Capture(ft, syncInterval: 1, tearing: false), telemetry: Telemetry())), x => x.Rule.Id == "vsync-judder");
        Assert.Equal(Confidence.High, f.Hit.Confidence);
        Assert.Contains("16.7", f.Why);
    }

    [Fact]
    public void GsyncStyleVariableFrameTimesAreNotJudder()
    {
        var cap = Capture(Jittery(3000, 10, 2.5), syncInterval: 1, tearing: false);
        Assert.DoesNotContain(Run(Context(cap, telemetry: Telemetry())), f => f.Rule.Id == "vsync-judder");
    }

    [Fact]
    public void DetectsFrameCapWellBelowRefreshWithGpuHeadroom()
    {
        var cap = Capture(Jittery(3600, 16.67, 0.3), gpuBusy: ft => ft * 0.4);
        var f = Assert.Single(Run(Context(cap, telemetry: Telemetry(gpuUtil: 45))), x => x.Rule.Id == "frame-cap-below-refresh");
        Assert.Contains("60 fps", f.Why);
        Assert.Contains("116", string.Join(" ", f.FixSteps));
    }

    [Fact]
    public void CapNearRefreshIsFine()
    {
        var cap = Capture(Jittery(3600, 8.6, 0.2), gpuBusy: ft => ft * 0.6); // ~116 fps on 120 Hz
        Assert.DoesNotContain(Run(Context(cap, telemetry: Telemetry(gpuUtil: 70))), f => f.Rule.Id == "frame-cap-below-refresh");
    }

    [Fact]
    public void FlagsComposedPresentation()
    {
        var cap = Capture(Jittery(2000, 10, 3), mode: "Composed: Flip");
        Assert.Contains(Run(Context(cap, telemetry: Telemetry())), f => f.Rule.Id == "composed-presentation");
    }

    [Fact]
    public void FlagsHeavyAndKnownBackgroundProcessesButNotTheGame()
    {
        var telemetry = Telemetry(gpuUtil: 60, processes: new[]
        {
            new ProcessCpuUsage("Game", 1, 6.0, 18),
            new ProcessCpuUsage("MsMpEng", 1, 1.4, 4),
            new ProcessCpuUsage("Discord", 3, 0.2, 0.6),
            new ProcessCpuUsage("dwm", 1, 0.9, 2.8),
            new ProcessCpuUsage("notepad", 1, 0.05, 0.1),
        });
        var f = Assert.Single(Run(Context(Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5), telemetry: telemetry)), x => x.Rule.Id == "background-processes");
        Assert.Equal(2, f.Hit.Details!.Count);
        Assert.Contains(f.Hit.Details, d => d.StartsWith("Microsoft Defender"));
        Assert.DoesNotContain(f.Hit.Details, d => d.Contains("Game") || d.Contains("dwm"));
    }

    [Fact]
    public void GpuThermalFlagsBeatPboAdvice()
    {
        var cpuBound = Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5);
        var findings = Run(Context(cpuBound, telemetry: Telemetry(gpuUtil: 60, cpuMaxMhz: 4800, gpuThermal: true)));
        Assert.Contains(findings, f => f.Rule.Id == "thermal-throttling" && f.Hit.Confidence == Confidence.High);
        Assert.DoesNotContain(findings, f => f.Rule.Id == "pbo-stock");
    }

    [Fact]
    public void DetectsCpuHeatSoak()
    {
        var cap = Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5);
        var telemetry = Telemetry(samples: 120, gpuUtil: 60, cpuMhzAt: i => 5600 - i * 6); // 5600 → ~4890 MHz
        var f = Assert.Single(Run(Context(cap, telemetry: telemetry)), x => x.Rule.Id == "thermal-throttling");
        Assert.Equal(Confidence.Medium, f.Hit.Confidence);
        Assert.Contains("HWiNFO", f.Hit.Evidence);
    }

    [Fact]
    public void LowBoostOnCpuBoundRunSuggestsPbo()
    {
        var cap = Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5);
        var f = Assert.Single(Run(Context(cap, telemetry: Telemetry(gpuUtil: 60, cpuMaxMhz: 5000))), x => x.Rule.Id == "pbo-stock");
        Assert.Equal(Confidence.Medium, f.Hit.Confidence);
    }

    [Fact]
    public void LowBoostOnGpuBoundRunIsIgnored()
    {
        Assert.DoesNotContain(Run(Context(Capture(Jittery(3000, 10, 3)), telemetry: Telemetry(cpuMaxMhz: 5000))), f => f.Rule.Id == "pbo-stock");
    }

    [Fact]
    public void FindingsAreRankedByScore()
    {
        var sys = Rig(s => { s.Memory = new() { new("Kingston", "KF560C32-48", 4800, 4800, 48, 34) }; s.Gpu = s.Gpu! with { DriverDate = DateTime.Today.AddDays(-400) }; });
        var findings = Run(Context(Capture(Jittery(3000, 8, 3), gpuBusy: ft => ft * 0.5), sys, Telemetry(gpuUtil: 60)));
        Assert.Equal(findings.OrderByDescending(f => f.Score).Select(f => f.Rule.Id), findings.Select(f => f.Rule.Id));
        Assert.Equal("memory-profile-off", findings[0].Rule.Id);
    }
}
