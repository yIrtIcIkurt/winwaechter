using System.Management;
using System.Text.RegularExpressions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>Computerschutz (System Protection) of one drive: state and the one action that switches it on.</summary>
public interface ISystemRestoreConfig
{
    /// <summary>True/false when readable, null when the state cannot be told.</summary>
    bool? IsEnabled(string drive);

    /// <summary>Switches the protection on for the drive (e.g. "C:\"); throws when Windows refuses.</summary>
    void Enable(string drive);
}

/// <summary>Registry for the state, WMI SystemRestore.Enable for the switch. Never runs in tests.</summary>
public sealed class WinSystemRestoreConfig(IRegistry reg) : ISystemRestoreConfig
{
    const string ConfigKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    // RPSessionInterval is machine-wide: 1 = on, 0 = off; anything else (or missing) cannot be told.
    public bool? IsEnabled(string drive) => reg.Get(Hive.LocalMachine, ConfigKey, "RPSessionInterval") switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    public void Enable(string drive)
    {
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();
        using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var args = cls.GetMethodParameters("Enable");
        args["Drive"] = drive;
        using var result = cls.InvokeMethod("Enable", args, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 0u);
        if (code != 0)
            throw new InvalidOperationException($"Der Computerschutz ließ sich nicht einschalten (Code {code}).");
    }
}

public static class SystemProtection
{
    /// <summary>Volume Shadow Copy and its software provider: both must be startable for restore points.</summary>
    public static readonly string[] Services = ["VSS", "swprv"];

    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    const string Why = "Ohne Computerschutz gibt es keine Wiederherstellungspunkte, auch nicht vor Änderungen durch WinWächter.";

    public static SecurityItem Check(IServiceControl sc, ISystemRestoreConfig cfg, IRegistry reg, IPolicyOwnership own, string drive)
    {
        SecurityItem Row(Light l, string finding, IReadOnlyList<string>? fix = null) => fix is not null
            ? new(SecurityIds.SystemProtection, SecurityTitles.SystemProtection, l, finding, Why, SecurityActionKind.Change, "Einschalten",
                Change: new SystemProtectChange(sc, cfg, drive, fix), Recommended: true)
            : new(SecurityIds.SystemProtection, SecurityTitles.SystemProtection, l, finding, Why);

        if (new[] { "DisableSR", "DisableConfig" }.Any(n => PolicyValues.State(reg, own, Hive.LocalMachine, PolicyKey, n) != PolicyState.None))
            return Row(Light.Yellow, PolicyValues.ForeignText);

        var disabled = Services.Where(n => sc.Get(n)?.Mode == StartMode.Disabled).ToList();
        var on = cfg.IsEnabled(drive);
        if (on == false) return Row(Light.Red, "Ausgeschaltet", disabled);
        if (disabled.Count > 0) return Row(Light.Red, "Ein nötiger Dienst ist deaktiviert", disabled);
        if (on is null) return Row(Light.Grey, "Nicht prüfbar");
        return Row(Light.Green, "Eingeschaltet");
    }
}

/// <summary>
/// Switches System Protection on: services VSS/swprv from Disabled to Manual, then the protection of the drive.
/// Undo puts only the services back; the protection is never switched off again.
/// </summary>
public sealed class SystemProtectChange : IChange, IUndoLimit
{
    static readonly Regex DriveRule = new(@"^[A-Z]:\\$", RegexOptions.CultureInvariant);

    readonly IServiceControl _sc;
    readonly ISystemRestoreConfig _cfg;
    readonly string _drive;
    readonly string[] _services;

    public SystemProtectChange(IServiceControl sc, ISystemRestoreConfig cfg, string drive, IReadOnlyList<string> disabledServices)
    {
        if (!DriveRule.IsMatch(drive))
            throw new ArgumentException("Das Laufwerk muss die Form „C:\\“ haben.", nameof(drive));
        if (disabledServices.Any(s => !SystemProtection.Services.Contains(s)))
            throw new ArgumentException("Dieser Dienst gehört nicht zum Computerschutz.", nameof(disabledServices));
        _sc = sc; _cfg = cfg; _drive = drive;
        _services = disabledServices.Distinct().ToArray();
    }

    public string Kind => "sysprotect";
    public string Target => $"Computerschutz {_drive}";
    public string Description => $"Computerschutz für {_drive} einschalten";
    public string? UndoLimit => UndoLimits.Partial;

    public static SystemProtectChange FromRecord(ChangeRecord r, IServiceControl sc, ISystemRestoreConfig cfg)
    {
        try
        {
            var services = (r.Before.GetValueOrDefault("services") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            return new SystemProtectChange(sc, cfg, r.Before.GetValueOrDefault("drive") ?? "", services);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Dieser Eintrag gehört nicht zum Computerschutz – Rückgängig abgelehnt.");
        }
    }

    public static void Register(ChangeFactory f, IServiceControl sc, ISystemRestoreConfig cfg) =>
        f.Register("sysprotect", r => FromRecord(r, sc, cfg));

    // Journalled before Apply, so a failure midway can still put the services back.
    public ChangeRecord Plan(SessionContext ctx) =>
        new(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["drive"] = _drive, ["services"] = string.Join(',', _services) }, new(), ChangeStatus.Pending, DateTimeOffset.Now);

    public ChangeRecord Apply(SessionContext ctx)
    {
        var protection = _cfg.IsEnabled(_drive);
        var changed = new List<string>();
        foreach (var name in _services)
        {
            if (_sc.Get(name)?.Mode != StartMode.Disabled) continue;
            _sc.SetStartMode(name, StartMode.Manual, false);
            changed.Add(name);
        }
        if (protection != true) _cfg.Enable(_drive);

        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["drive"] = _drive, ["services"] = string.Join(',', changed), ["protection"] = protection?.ToString().ToLowerInvariant() ?? "unknown" },
            new() { ["mode"] = nameof(StartMode.Manual), ["protection"] = "true" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new() { ["drive"] = _drive, ["services"] = string.Join(',', _services) },
         new() { ["mode"] = nameof(StartMode.Manual), ["protection"] = "true" });

    // Only the services that were Disabled before go back to Disabled; the protection stays on.
    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        foreach (var name in RecordedServices(record))
            _sc.SetStartMode(name, StartMode.Disabled, false);
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        foreach (var name in RecordedServices(record))
        {
            var live = _sc.Get(name);
            if (live is null) return DriftState.Gone;
            if (live.Mode != StartMode.Manual) return DriftState.Changed;
        }
        return DriftState.Unchanged;
    }

    // Never trusts the journal: names outside the fixed list are ignored.
    IEnumerable<string> RecordedServices(ChangeRecord record) =>
        record.Before.TryGetValue("services", out var s)
            ? (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(n => SystemProtection.Services.Contains(n)).Distinct()
            : _services;
}
