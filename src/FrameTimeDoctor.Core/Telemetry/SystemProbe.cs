using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FrameTimeDoctor.Core.Telemetry;

/// <summary>Reads static system state via WMI, powercfg, nvidia-smi and the registry. No admin needed.</summary>
public static class SystemProbe
{
    public static SystemSnapshot Take()
    {
        var s = new SystemSnapshot();
        Try(s, "CPU", () => ReadCpu(s));
        Try(s, "motherboard/BIOS", () => ReadBoard(s));
        Try(s, "memory", () => ReadMemory(s));
        Try(s, "GPU", () => ReadGpu(s));
        Try(s, "power plan", () => ReadPowerPlan(s));
        Try(s, "chipset driver", () => ReadChipsetDriver(s));
        return s;
    }

    static void Try(SystemSnapshot s, string what, Action read)
    {
        try { read(); }
        catch (Exception ex) { s.ProbeWarnings.Add($"Could not read {what}: {ex.Message}"); }
    }

    static IEnumerable<ManagementBaseObject> Wmi(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    static void ReadCpu(SystemSnapshot s)
    {
        var cpu = Wmi("SELECT Name, MaxClockSpeed, NumberOfEnabledCore, NumberOfLogicalProcessors, ThreadCount, L3CacheSize FROM Win32_Processor").First();
        s.CpuThreadCapacity = Convert.ToInt32(cpu["ThreadCount"] ?? 0);
        s.CpuL3CacheKb = Convert.ToInt32(cpu["L3CacheSize"] ?? 0);
        s.CpuName = (cpu["Name"] as string ?? "").Trim();
        s.CpuBaseClockMhz = Convert.ToDouble(cpu["MaxClockSpeed"] ?? 0);
        s.CpuCores = Convert.ToInt32(cpu["NumberOfEnabledCore"] ?? 0);
        // WMI rather than Environment.ProcessorCount, which a job object or affinity mask can shrink.
        if (cpu["NumberOfLogicalProcessors"] is uint logical && logical > 0) s.LogicalProcessors = (int)logical;
    }

    static void ReadBoard(SystemSnapshot s)
    {
        var board = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        if (board != null) s.Motherboard = $"{board["Manufacturer"]} {board["Product"]}".Trim();
        var bios = Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        if (bios != null)
        {
            s.BiosVersion = bios["SMBIOSBIOSVersion"] as string;
            s.BiosDate = ParseCimDate(bios["ReleaseDate"] as string);
        }
    }

    static void ReadMemory(SystemSnapshot s)
    {
        foreach (var m in Wmi("SELECT Manufacturer, PartNumber, Speed, ConfiguredClockSpeed, Capacity, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            s.Memory.Add(new MemoryModule(
                (m["Manufacturer"] as string ?? "").Trim(),
                (m["PartNumber"] as string ?? "").Trim(),
                Convert.ToInt32(m["Speed"] ?? 0),
                Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0),
                Convert.ToDouble(m["Capacity"] ?? 0) / (1024.0 * 1024 * 1024),
                Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0)));
        }
    }

    static void ReadGpu(SystemSnapshot s)
    {
        // Prefer the discrete NVIDIA adapter; AM5 CPUs also expose an iGPU.
        var controllers = Wmi("SELECT Name, DriverVersion, DriverDate, CurrentRefreshRate, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController").ToList();
        var gpu = controllers.FirstOrDefault(c => (c["Name"] as string ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                  ?? controllers.FirstOrDefault();
        if (gpu == null) return;

        var name = (gpu["Name"] as string ?? "").Trim();
        var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                   : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
                   : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";
        var wmiVersion = gpu["DriverVersion"] as string ?? "";
        var version = vendor == "NVIDIA" ? (NvidiaSmiDriverVersion() ?? NvidiaVersionFromWindows(wmiVersion) ?? wmiVersion) : wmiVersion;
        s.Gpu = new GpuInfo(name, vendor, version, ParseCimDate(gpu["DriverDate"] as string));

        if (gpu["CurrentRefreshRate"] is uint hz && hz > 0) s.RefreshRateHz = (int)hz;
        if (gpu["CurrentHorizontalResolution"] is uint w && gpu["CurrentVerticalResolution"] is uint h && w > 0)
            s.Resolution = $"{w}x{h}";
    }

    /// <summary>Windows reports NVIDIA drivers as e.g. 32.0.16.1714; the last five digits are 617.14.</summary>
    internal static string? NvidiaVersionFromWindows(string wmiVersion)
    {
        var parts = wmiVersion.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[^2] + parts[^1].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    static string? NvidiaSmiDriverVersion()
    {
        var output = RunTool("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader");
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) || !char.IsDigit(line[0]) ? null : line;
    }

    static void ReadPowerPlan(SystemSnapshot s)
    {
        // "Power Scheme GUID: 8c5e7fda-...  (High performance)"
        var output = RunTool("powercfg", "/getactivescheme") ?? throw new InvalidOperationException("powercfg failed");
        var m = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s*\((.+)\)");
        if (!m.Success) throw new InvalidOperationException($"Unexpected powercfg output: {output.Trim()}");
        s.PowerPlanGuid = m.Groups[1].Value.ToLowerInvariant();
        s.PowerPlanName = m.Groups[2].Value.Trim();
    }

    static void ReadChipsetDriver(SystemSnapshot s)
    {
        if (!s.IsAmdCpu) return;
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                var name = app?.GetValue("DisplayName") as string;
                if (name == null || !name.StartsWith("AMD Chipset", StringComparison.OrdinalIgnoreCase)
                                 && !name.StartsWith("AMD_Chipset", StringComparison.OrdinalIgnoreCase)) continue;
                s.ChipsetDriverVersion ??= app!.GetValue("DisplayVersion") as string;
                if (app!.GetValue("InstallDate") is string d &&
                    DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    s.ChipsetDriverInstallDate ??= date;
            }
        }
    }

    internal static string? RunTool(string exe, string args, int timeoutMs = 10_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(timeoutMs) && p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // tool not installed
        }
    }

    static DateTime? ParseCimDate(string? cim) =>
        cim != null && cim.Length >= 8 &&
        DateTime.TryParseExact(cim[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
