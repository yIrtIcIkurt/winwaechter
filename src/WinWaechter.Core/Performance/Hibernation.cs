using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

public interface IHibernation
{
    bool IsEnabled { get; }
    long FileBytes { get; }
    void Set(bool on);
}

/// <summary>Hibernation via the fixed powercfg calls "/hibernate off" and "/hibernate on".</summary>
public sealed class WinHibernation(IRegistry reg, IProcessRunner runner, string systemRoot, string systemDrive) : IHibernation
{
    const string Key = @"SYSTEM\CurrentControlSet\Control\Power";

    public bool IsEnabled => reg.Get(Hive.LocalMachine, Key, "HibernateEnabled") is int i && i == 1;

    public long FileBytes
    {
        get
        {
            try
            {
                var file = Path.Combine(systemDrive.TrimEnd('\\', '/') + "\\", "hiberfil.sys");
                var info = new FileInfo(file);
                return info.Exists ? info.Length : 0;
            }
            catch { return 0; }
        }
    }

    public void Set(bool on)
    {
        var exe = Path.Combine(systemRoot, "System32", "powercfg.exe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (exit, _, _) = runner.RunAsync(exe, on ? "/hibernate on" : "/hibernate off", cts.Token)
            .GetAwaiter().GetResult();
        if (exit != 0 || IsEnabled != on)
            throw new InvalidOperationException($"powercfg hat den Ruhezustand nicht geändert (Rückgabewert {exit}).");
    }
}

/// <summary>Switches hibernation off (frees hiberfil.sys) or back on.</summary>
public sealed class HibernateChange(IHibernation hibernation, bool target) : IChange
{
    public string Kind => "hibernate";
    public string Target => "Ruhezustand";

    public string Description => Describe(hibernation.FileBytes);

    string Describe(long bytes) => target
        ? "Ruhezustand einschalten"
        : $"Ruhezustand ausschalten (gibt {SizeText.Format(bytes)} frei)";

    public static HibernateChange FromRecord(ChangeRecord r, IHibernation hibernation) =>
        r.After.GetValueOrDefault("on") switch
        {
            "true" => new HibernateChange(hibernation, true),
            "false" => new HibernateChange(hibernation, false),
            _ => throw new InvalidOperationException("Der Wert des Ruhezustands ist nicht gültig – Rückgängig abgelehnt."),
        };

    static string Text(bool on) => on ? "true" : "false";

    public ChangeRecord Apply(SessionContext ctx)
    {
        var bytes = hibernation.FileBytes;
        var before = new Dictionary<string, string?>
        {
            ["on"] = Text(hibernation.IsEnabled),
            ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
        };
        var desc = Describe(bytes);
        hibernation.Set(target);
        var after = new Dictionary<string, string?> { ["on"] = Text(target) };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, desc,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var old = record.Before.GetValueOrDefault("on") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException("Der vorherige Wert des Ruhezustands ist nicht lesbar – Rückgängig nicht möglich."),
        };
        hibernation.Set(old);
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        hibernation.IsEnabled == target ? DriftState.Unchanged : DriftState.Changed;
}
