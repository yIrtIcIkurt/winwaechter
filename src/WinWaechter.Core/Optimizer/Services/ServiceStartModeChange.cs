using System.ComponentModel;
using System.Security;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Services;

/// <summary>Sets a service from Automatic to Manual (never Disabled, never stops it).</summary>
public sealed class ServiceStartModeChange : IChange
{
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

    readonly IServiceControl _sc;
    readonly IRegistry _reg;
    readonly string _name;

    public ServiceStartModeChange(IServiceControl sc, IRegistry reg, string name)
    {
        _sc = sc; _reg = reg; _name = name;
    }

    public string Kind => "service";
    public string Target => _name;

    public string Description
    {
        get
        {
            string? display = null;
            try { display = _sc.Get(_name)?.DisplayName; }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or Win32Exception) { }
            return DescriptionFor(display);
        }
    }

    string DescriptionFor(string? display) =>
        $"Dienst „{(string.IsNullOrWhiteSpace(display) ? _name : display)}“ auf Manuell stellen";

    public static ServiceStartModeChange FromRecord(ChangeRecord r, IServiceControl sc, IRegistry reg) =>
        new(sc, reg, r.Target);

    public ChangeRecord Apply(SessionContext ctx)
    {
        var live = Read() ?? throw new InvalidOperationException($"Dienst „{_name}“ existiert nicht mehr.");
        var description = DescriptionFor(live.DisplayName);

        // Backup first; a missing key has nothing to export.
        string? backup = null;
        var key = ServicesKey + _name;
        if (_reg.KeyExists(Hive.LocalMachine, key))
        {
            Directory.CreateDirectory(ctx.BackupDir);
            backup = Path.Combine(ctx.BackupDir, $"service_{SafeFileName(_name)}.reg");
            _reg.Export(Hive.LocalMachine, key, backup);
        }

        _sc.SetStartMode(_name, StartMode.Manual, false);

        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, description,
            new() { ["mode"] = live.Mode.ToString(), ["delayed"] = live.DelayedStart.ToString(), ["backup"] = backup },
            new() { ["mode"] = StartMode.Manual.ToString(), ["delayed"] = bool.FalseString },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        // A vanished or unreadable service cannot be restored; report it.
        _ = Read() ?? throw new InvalidOperationException($"Dienst „{_name}“ existiert nicht mehr, Wiederherstellung nicht möglich.");
        var mode = Enum.Parse<StartMode>(record.Before["mode"]!);
        var delayed = bool.Parse(record.Before.GetValueOrDefault("delayed") ?? bool.FalseString);
        _sc.SetStartMode(_name, mode, delayed);   // failures propagate
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var live = Read();
        if (live is null) return DriftState.Gone;
        var mode = Enum.Parse<StartMode>(record.After.GetValueOrDefault("mode") ?? nameof(StartMode.Manual));
        var delayed = bool.Parse(record.After.GetValueOrDefault("delayed") ?? bool.FalseString);
        return live.Mode == mode && live.DelayedStart == delayed ? DriftState.Unchanged : DriftState.Changed;
    }

    // Unreadable keys are an error, never "gone".
    ServiceInfo? Read()
    {
        try { return _sc.Get(_name); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Dienst „{_name}“ kann nicht gelesen werden (Zugriff verweigert).", ex);
        }
    }

    static string SafeFileName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
