using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Autostart;

/// <summary>Disables an autostart entry by writing 03 into its StartupApproved value.</summary>
public sealed class StartupApprovedChange : IChange
{
    readonly IRegistry _reg;
    readonly Hive _hive;
    readonly string _approvedKey;
    readonly string _valueName;

    public StartupApprovedChange(IRegistry r, Hive hive, string approvedKey, string valueName)
    {
        _reg = r; _hive = hive; _approvedKey = approvedKey; _valueName = valueName;
    }

    public string Kind => "autostart";
    public string Target => $"{HiveName(_hive)}\\{_approvedKey}\\{_valueName}";
    public string Description => $"Autostart „{Path.GetFileNameWithoutExtension(_valueName)}“ deaktivieren";

    public static StartupApprovedChange FromRecord(ChangeRecord r, IRegistry reg)
    {
        var s = r.Before;
        var hive = Enum.Parse<Hive>(s["hive"]!);
        return new StartupApprovedChange(reg, hive, s["approvedKey"]!, s["valueName"]!);
    }

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = ReadHex();
        // A missing approved key has nothing to back up (reg export would fail).
        string? file = null;
        if (_reg.KeyExists(_hive, _approvedKey))
        {
            Directory.CreateDirectory(ctx.BackupDir);
            file = Path.Combine(ctx.BackupDir, $"autostart-{Guid.NewGuid():N}.reg");
            _reg.Export(_hive, _approvedKey, file);
        }

        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        _reg.Set(_hive, _approvedKey, _valueName, bytes, RegistryValueKind.Binary);

        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            State(before, file), State(Convert.ToHexString(bytes)), ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var before = record.Before.GetValueOrDefault("value");
        if (before is null) _reg.Delete(_hive, _approvedKey, _valueName);
        else _reg.Set(_hive, _approvedKey, _valueName, Convert.FromHexString(before), RegistryValueKind.Binary);
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var after = record.After.GetValueOrDefault("value");
        var expected = after is { Length: >= 2 } ? Convert.FromHexString(after)[0] : (byte)0x03;
        return _reg.Get(_hive, _approvedKey, _valueName) is byte[] { Length: > 0 } live && live[0] == expected
            ? DriftState.Unchanged : DriftState.Changed;
    }

    string? ReadHex() => _reg.Get(_hive, _approvedKey, _valueName) is byte[] b ? Convert.ToHexString(b) : null;

    Dictionary<string, string?> State(string? hex, string? backup = null) => new()
    {
        ["hive"] = _hive.ToString(),
        ["approvedKey"] = _approvedKey,
        ["valueName"] = _valueName,
        ["value"] = hex,
        ["backup"] = backup,
    };

    static string HiveName(Hive h) => h == Hive.CurrentUser ? "HKCU" : "HKLM";
}
