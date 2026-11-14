using System.Globalization;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Repair;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Device;

public enum DeviceLevel { Good, Attention, Bad, Unknown }

/// <summary>One line of the Gerät page. Fix: what repairs it (null when nothing can); Extra: an additional action.</summary>
public sealed record DeviceRow(string Id, string Group, string Title, string Detail, DeviceLevel Level, string Why, SystemAction? Fix,
    SystemAction? Extra = null);

/// <summary>Battery, disk and drive health with the levels of the 0.6 spec.</summary>
public sealed class DeviceScanner(IBatteryInfo battery, IDiskHealth disks, IVolumeInfo volumes, RepairActions repair, IProcessRunner run,
    string downloadsDir, DateTime now, string windowsDir = @"C:\Windows")
{
    public const string GroupBattery = "Akku", GroupDisks = "Laufwerke (Hardware)", GroupVolumes = "Laufwerke (Windows)";
    static readonly CultureInfo De = new("de-DE");

    public IReadOnlyList<DeviceRow> Scan()
    {
        var rows = new List<DeviceRow>();
        if (battery.Read() is { } b) rows.Add(Battery(b));
        rows.AddRange(disks.Read().Select(Disk));
        foreach (var v in volumes.Read()) rows.AddRange(Volume(v));
        return rows;
    }

    DeviceRow Battery(BatteryState b)
    {
        var extra = new SystemAction("battery-report", "Ausführlichen Akkubericht speichern", "Bericht speichern", null, false, async (_, _) =>
        {
            var name = $"Akkubericht-{now:yyyyMMdd}.html";
            var (exit, _, err) = await run.RunAsync(Path.Combine(windowsDir, "System32", "powercfg.exe"),
                $"/batteryreport /output \"{Path.Combine(downloadsDir, name)}\"", CancellationToken.None).ConfigureAwait(false);
            // the HTML is not opened from the elevated app (a browser would start elevated too)
            return exit == 0
                ? new ActionOutcome(true, $"Gespeichert in Downloads: {name}")
                : new ActionOutcome(false, $"Der Bericht ließ sich nicht erstellen (Code {exit}). {err.Trim()}".Trim());
        });
        if (b.DesignMWh is not { } design || b.FullMWh is not { } full || design <= 0)
            return new DeviceRow("battery", GroupBattery, "Akku", "Ursprüngliche Kapazität nicht lesbar", DeviceLevel.Unknown,
                "Ohne die ursprüngliche Kapazität lässt sich der Verschleiß nicht berechnen.", null, extra);
        var lost = Math.Max(design - full, 0);
        var percent = (int)Math.Round(lost * 100.0 / design);
        var level = lost * 100 < 20 * design ? DeviceLevel.Good : lost * 100 <= 40 * design ? DeviceLevel.Attention : DeviceLevel.Bad;
        var detail = $"Kapazität heute {full.ToString("N0", De)} mWh von ursprünglich {design.ToString("N0", De)} mWh ({percent} % Verschleiß)"
                     + (b.Cycles is { } c ? $", {c} Ladezyklen" : "");
        var why = level switch
        {
            DeviceLevel.Good => "Der Akku ist in gutem Zustand.",
            DeviceLevel.Attention => "Der Akku ist gealtert. Beheben lässt sich das nicht; die Laufzeit wird kürzer.",
            _ => "Akku stark gealtert – ein Austausch lohnt sich, wenn die Laufzeit nicht mehr reicht.",
        };
        return new DeviceRow("battery", GroupBattery, "Akku", detail, level, why, null, extra);
    }

    static DeviceRow Disk(DiskState d)
    {
        var errors = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
        var level = d.Health is "Warning" or "Unhealthy" || d.Wear >= 90 ? DeviceLevel.Bad
            : d.Wear >= 70 || errors > 0 || (d.TempC is { } t && d.TempMaxC is { } m && t > m) ? DeviceLevel.Attention
            : d.Health == "Unknown" ? DeviceLevel.Unknown
            : DeviceLevel.Good;
        var parts = new List<string>();
        if (d.Wear is { } w) parts.Add($"Verschleiß {w} %");
        if (d.TempC is { } temp) parts.Add($"{temp} °C");
        if (d.PowerOnHours is { } h) parts.Add($"{h} Betriebsstunden");
        if (errors > 0) parts.Add($"{errors} nicht behebbare Lese-/Schreibfehler");
        var why = level switch
        {
            DeviceLevel.Bad => "Sichern Sie Ihre Daten bald.",
            DeviceLevel.Attention => "Das Laufwerk zeigt erste Abnutzung oder Fehler. Sichern Sie wichtige Daten regelmäßig.",
            DeviceLevel.Unknown => "Windows meldet keinen Zustand für dieses Laufwerk.",
            _ => "Windows meldet das Laufwerk als gesund.",
        };
        var title = d.Media.Length > 0 ? $"{d.Name} ({d.Media})" : d.Name;
        return new DeviceRow("disk:" + d.Name, GroupDisks, title, parts.Count > 0 ? string.Join(", ", parts) : "Weitere Werte nicht verfügbar",
            level, why, null);
    }

    IEnumerable<DeviceRow> Volume(VolumeState v)
    {
        var drive = $"Laufwerk {v.Letter}:";
        var stale = v.LastOptimized is not { } last || (now - last).TotalDays > 30;
        var optimize = new SystemAction($"optimize-{v.Letter}", $"{drive} optimieren", "Optimieren", null, false, async (p, _) =>
        {
            p?.Report($"{drive} wird optimiert …");
            var (exit, _, _) = await run.RunAsync(Path.Combine(windowsDir, "System32", "defrag.exe"), $"{v.Letter}: /O /U",
                line => { if (line.Contains('%')) p?.Report($"{drive} wird optimiert … {line.Trim()}"); }, CancellationToken.None).ConfigureAwait(false);
            return exit == 0 ? new ActionOutcome(true, $"{drive} wurde optimiert.") : new ActionOutcome(false, $"Die Optimierung ist fehlgeschlagen (Code {exit}).");
        }) { SkipGate = true, LockKey = "disk", Stamp = true };
        yield return new DeviceRow($"volume:{v.Letter}", GroupVolumes, drive,
            v.LastOptimized is { } at ? $"Zuletzt optimiert am {at.ToString("dd.MM.yyyy", De)}" : "Noch nie optimiert",
            stale ? DeviceLevel.Attention : DeviceLevel.Good,
            stale ? "Windows optimiert Laufwerke sonst regelmäßig; die letzte Optimierung ist über 30 Tage her." : "Das Laufwerk ist optimiert.",
            stale ? optimize : null);

        if (v.Dirty == true || v.RecentDiskErrors > 0)
            yield return new DeviceRow($"volume-errors:{v.Letter}", GroupVolumes, $"{drive} Fehler im Dateisystem",
                v.Dirty == true ? "Windows hat das Laufwerk zur Prüfung vorgemerkt." : $"{v.RecentDiskErrors} Laufwerksfehler in den letzten 30 Tagen",
                DeviceLevel.Attention, "Eine Prüfung findet beschädigte Stellen, bevor Dateien verloren gehen.", repair.ScanDisk(v.Letter));

        if (v.Size > 0 && v.Free * 10 < v.Size)
            yield return new DeviceRow($"volume-space:{v.Letter}", GroupVolumes, $"{drive} wenig freier Platz",
                $"{v.Free / (1UL << 30)} GB von {v.Size / (1UL << 30)} GB frei", DeviceLevel.Attention,
                "Unter 10 % freiem Platz wird Windows langsamer und Updates scheitern. Die Seite „Speicherplatz“ zeigt, was Platz belegt.", null);
    }
}
