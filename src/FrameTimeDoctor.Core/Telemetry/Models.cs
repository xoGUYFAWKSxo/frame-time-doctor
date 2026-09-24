namespace FrameTimeDoctor.Core.Telemetry;

/// <summary>Static system state read once per session (hardware, drivers, OS config).</summary>
public sealed class SystemSnapshot
{
    public DateTime TakenAt { get; set; } = DateTime.Now;
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;

    public string CpuName { get; set; } = "";
    public double CpuBaseClockMhz { get; set; }
    /// <summary>Enabled cores and logical processors as Windows sees them (after BIOS/msconfig limits).</summary>
    public int CpuCores { get; set; }
    public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
    /// <summary>Threads the package supports per SMBIOS, regardless of what's enabled.</summary>
    public int CpuThreadCapacity { get; set; }
    /// <summary>L3 the firmware reports. Halves on Ryzen when a CCD is disabled.</summary>
    public int CpuL3CacheKb { get; set; }

    public string? Motherboard { get; set; }
    public string? BiosVersion { get; set; }
    public DateTime? BiosDate { get; set; }

    public List<MemoryModule> Memory { get; set; } = new();

    public GpuInfo? Gpu { get; set; }
    public int? RefreshRateHz { get; set; }
    public string? Resolution { get; set; }

    public string? PowerPlanGuid { get; set; }
    public string? PowerPlanName { get; set; }

    public string? ChipsetDriverVersion { get; set; }
    public DateTime? ChipsetDriverInstallDate { get; set; }

    /// <summary>Anything the probe couldn't read; surfaced in the report so gaps aren't silent.</summary>
    public List<string> ProbeWarnings { get; set; } = new();

    public bool IsAmdCpu => CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
}

/// <param name="SpeedMts">SPD/JEDEC speed Windows reports (not the XMP/EXPO rating).</param>
/// <param name="ConfiguredMts">Speed the memory is actually running at.</param>
/// <param name="SmbiosMemoryType">26 = DDR4, 34 = DDR5.</param>
public sealed record MemoryModule(
    string Manufacturer, string PartNumber, int SpeedMts, int ConfiguredMts, double CapacityGb, int SmbiosMemoryType)
{
    public bool IsDdr5 => SmbiosMemoryType == 34;
}

public sealed record GpuInfo(string Name, string Vendor, string DriverVersion, DateTime? DriverDate)
{
    public bool IsNvidia => Vendor == "NVIDIA";
}

public sealed record GpuSample(
    double TimeSeconds, double? UtilPct, double? ClockMhz, double? MemClockMhz, double? TempC,
    double? PowerW, double? PowerLimitW, bool? ThermalSlowdown, bool? PowerCapped);

/// <param name="MaxCoreMhz">Effective clock of the fastest logical core in this sample.</param>
public sealed record CpuSample(double TimeSeconds, double UtilPct, double MaxCoreMhz, double AvgCoreMhz);

/// <param name="CoresUsed">CPU time used during the capture divided by capture length (1.0 = one full core).</param>
public sealed record ProcessCpuUsage(string Name, int Instances, double CoresUsed, double PctOfTotal);

/// <summary>Time-series telemetry recorded alongside a PresentMon capture.</summary>
public sealed class TelemetryLog
{
    public double DurationSeconds { get; set; }
    public List<GpuSample> Gpu { get; set; } = new();
    public List<CpuSample> Cpu { get; set; } = new();
    public List<ProcessCpuUsage> Processes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
