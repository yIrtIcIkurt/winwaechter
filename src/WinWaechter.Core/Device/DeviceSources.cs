using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Xml.Linq;
using Microsoft.Win32;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Repair;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Device;

/// <summary>Capacities in mWh; null = not readable.</summary>
public sealed record BatteryState(long? DesignMWh, long? FullMWh, int? Cycles);

public interface IBatteryInfo
{
    /// <summary>Null when the device has no battery.</summary>
    BatteryState? Read();
}

/// <summary>One physical disk. Health: Healthy / Warning / Unhealthy / Unknown. Counters null when the drive does not report them.</summary>
public sealed record DiskState(string Name, string Media, string Health, ulong? Wear, int? TempC, int? TempMaxC,
    ulong? ReadErrorsUncorrected, ulong? WriteErrorsUncorrected, ulong? PowerOnHours);

public interface IDiskHealth
{
    IReadOnlyList<DiskState> Read();
}

/// <summary>A fixed drive with a letter. RecentDiskErrors: Ntfs/disk/volmgr errors of the last 30 days (system drive only).</summary>
public sealed record VolumeState(char Letter, string FileSystem, ulong Size, ulong Free, DateTime? LastOptimized, bool? Dirty, int RecentDiskErrors);

public interface IVolumeInfo
{
    IReadOnlyList<VolumeState> Read();
}

/// <summary>root\wmi battery classes; the design capacity needs admin rights, else the powercfg XML report is read.</summary>
public sealed class WmiBatteryInfo(Func<bool> hasBattery, IProcessRunner run, string windowsDir, string tempDir) : IBatteryInfo
{
    public BatteryState? Read()
    {
        if (!hasBattery()) return null;
        var design = Long("BatteryStaticData", "DesignedCapacity");
        var full = Long("BatteryFullChargedCapacity", "FullChargedCapacity");
        var cycles = Long("BatteryCycleCount", "CycleCount");
        if (design is > 0 && full is > 0) return new BatteryState(design, full, cycles is > 0 ? (int)cycles : null);
        return FromReport() ?? new BatteryState(design is > 0 ? design : null, full is > 0 ? full : null, cycles is > 0 ? (int)cycles : null);
    }

    static long? Long(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery($"SELECT {prop} FROM {cls}"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    if (o[prop] is { } v) return Convert.ToInt64(v);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            FileLog.Info($"Akku: {cls} nicht lesbar ({ex.Message})");
        }
        return null;
    }

    BatteryState? FromReport()
    {
        var file = Path.Combine(tempDir, $"winwaechter-akku-{Guid.NewGuid():N}.xml");
        try
        {
            var (exit, _, _) = run.RunAsync(Path.Combine(windowsDir, "System32", "powercfg.exe"), $"/batteryreport /xml /output \"{file}\"",
                CancellationToken.None).GetAwaiter().GetResult();
            return exit == 0 && File.Exists(file) ? ParseReport(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            FileLog.Warn("Akkubericht nicht lesbar", ex);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The first battery of a powercfg /xml report (namespace ignored); null when unreadable.</summary>
    public static BatteryState? ParseReport(string xml)
    {
        try
        {
            var battery = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
            if (battery is null) return null;
            long? Value(string name) => long.TryParse(battery.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value, out var v) && v > 0
                ? v
                : null;
            var cycles = Value("CycleCount");
            return new BatteryState(Value("DesignCapacity"), Value("FullChargeCapacity"), cycles is { } c ? (int)c : null);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}

/// <summary>MSFT_PhysicalDisk plus its reliability counters (Storage WMI; the counters need admin rights).</summary>
public sealed class CimDiskHealth : IDiskHealth
{
    public IReadOnlyList<DiskState> Read()
    {
        var list = new List<DiskState>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in s.Get())
            {
                using (disk)
                {
                    var name = disk["FriendlyName"] as string ?? "Laufwerk";
                    var media = Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                    var health = Convert.ToInt32(disk["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                    ulong? wear = null, read = null, write = null, hours = null;
                    int? temp = null, tempMax = null;
                    try
                    {
                        foreach (ManagementObject c in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            using (c)
                            {
                                wear = U(c["Wear"]);
                                temp = (int?)U(c["Temperature"]);
                                tempMax = (int?)U(c["TemperatureMax"]);
                                read = U(c["ReadErrorsUncorrected"]);
                                write = U(c["WriteErrorsUncorrected"]);
                                hours = U(c["PowerOnHours"]);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
                    {
                        FileLog.Info($"Zuverlässigkeitswerte von {name} nicht lesbar ({ex.Message})");
                    }
                    // drives report 0 for counters they do not support (temperature, wear): treat as unknown
                    list.Add(new DiskState(name, media, health, wear, temp is > 0 ? temp : null, tempMax is > 0 ? tempMax : null, read, write,
                        hours is > 0 ? hours : null));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            FileLog.Warn("Laufwerkszustand nicht lesbar", ex);
        }
        return list;
    }

    static ulong? U(object? v) => v is null ? null : Convert.ToUInt64(v);
}

public interface IDiskErrorEvents
{
    /// <summary>Errors of Ntfs, disk and volmgr in the System log since sinceUtc.</summary>
    int CountSince(DateTime sinceUtc);
}

public sealed class WinDiskErrorEvents : IDiskErrorEvents
{
    public int CountSince(DateTime sinceUtc)
    {
        var xpath = "*[System[Provider[@Name='Ntfs' or @Name='disk' or @Name='volmgr' or @Name='Microsoft-Windows-Ntfs'] and Level=2 and TimeCreated[@SystemTime>='"
                    + sinceUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "']]]";
        var n = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
            for (var rec = reader.ReadEvent(); rec is not null && n < 1000; rec = reader.ReadEvent())
                using (rec) n++;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            FileLog.Warn("Ereignisprotokoll nicht lesbar", ex);
        }
        return n;
    }
}

/// <summary>Fixed drives with a letter (Win32_Volume), their last optimization (defrag statistics) and dirty bit.</summary>
public sealed class WinVolumeInfo(RepairChecks checks, IDiskErrorEvents events, char systemDrive) : IVolumeInfo
{
    public IReadOnlyList<VolumeState> Read()
    {
        var list = new List<VolumeState>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DriveLetter, DeviceID, FileSystem, Capacity, FreeSpace FROM Win32_Volume WHERE DriveType = 3");
            foreach (ManagementBaseObject v in s.Get())
            {
                using (v)
                {
                    if (v["DriveLetter"] is not string { Length: >= 1 } letterText || !char.IsAsciiLetterUpper(char.ToUpperInvariant(letterText[0])))
                        continue;
                    var letter = char.ToUpperInvariant(letterText[0]);
                    var id = v["DeviceID"] as string;
                    bool? dirty = null;
                    try { dirty = checks.IsDirtyAsync(letter, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { FileLog.Info($"Dirty-Bit von {letter}: nicht lesbar ({ex.Message})"); }
                    list.Add(new VolumeState(letter, v["FileSystem"] as string ?? "", Convert.ToUInt64(v["Capacity"] ?? 0UL),
                        Convert.ToUInt64(v["FreeSpace"] ?? 0UL), id is null ? null : LastOptimized(id), dirty,
                        letter == systemDrive ? events.CountSince(DateTime.UtcNow.AddDays(-30)) : 0));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            FileLog.Warn("Laufwerke nicht lesbar", ex);
        }
        return list;
    }

    // DeviceID "\\?\Volume{guid}\" → HKLM\SOFTWARE\Microsoft\Dfrg\Statistics\Volume{guid}\LastRunTime (SYSTEMTIME)
    static DateTime? LastOptimized(string deviceId)
    {
        var start = deviceId.IndexOf("Volume{", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var name = deviceId[start..].TrimEnd('\\');
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Dfrg\Statistics\{name}");
            return key?.GetValue("LastRunTime") is byte[] raw ? FromSystemTime(raw) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>A 16-byte SYSTEMTIME (local time); null for zeros or anything invalid.</summary>
    public static DateTime? FromSystemTime(byte[] raw)
    {
        if (raw.Length < 16) return null;
        int W(int i) => raw[i * 2] | (raw[i * 2 + 1] << 8);
        try
        {
            var (year, month, day) = (W(0), W(1), W(3));
            if (year < 1601) return null;
            return new DateTime(year, month, day, W(4), W(5), W(6));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
