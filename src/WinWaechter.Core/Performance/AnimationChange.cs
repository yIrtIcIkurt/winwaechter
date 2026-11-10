using System.ComponentModel;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Performance;

public interface IAnimationSetting
{
    bool Get();
    void Set(bool on);
}

/// <summary>Windows "Animationseffekte" (client area animation) via SystemParametersInfo.</summary>
public sealed class WinAnimationSetting : IAnimationSetting
{
    public bool Get()
    {
        if (!SpiNative.GetInt(SpiNative.SPI_GETCLIENTAREAANIMATION, 0, out var v, 0))
            throw new Win32Exception();
        return v != 0;
    }

    public void Set(bool on)
    {
        if (!SpiNative.SetValue(SpiNative.SPI_SETCLIENTAREAANIMATION, 0, on ? 1 : IntPtr.Zero,
                SpiNative.SPIF_UPDATEINIFILE | SpiNative.SPIF_SENDCHANGE))
            throw new Win32Exception();
    }
}

/// <summary>Switches Windows animation effects on or off.</summary>
public sealed class AnimationChange(IAnimationSetting setting, bool target) : IChange
{
    public string Kind => "spi";
    public string Target => "Animationseffekte";
    public string Description => target ? "Animationseffekte einschalten" : "Animationseffekte ausschalten";

    public static AnimationChange FromRecord(ChangeRecord r, IAnimationSetting setting) =>
        r.After.GetValueOrDefault("on") switch
        {
            "true" => new AnimationChange(setting, true),
            "false" => new AnimationChange(setting, false),
            _ => throw new InvalidOperationException("Der Wert der Animationseffekte ist nicht gültig – Rückgängig abgelehnt."),
        };

    static string Text(bool on) => on ? "true" : "false";

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = new Dictionary<string, string?> { ["on"] = Text(setting.Get()) };
        setting.Set(target);
        var after = new Dictionary<string, string?> { ["on"] = Text(target) };
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            before, after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var old = record.Before.GetValueOrDefault("on") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException("Der vorherige Wert der Animationseffekte ist nicht lesbar – Rückgängig nicht möglich."),
        };
        setting.Set(old);
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        try { return setting.Get() == target ? DriftState.Unchanged : DriftState.Changed; }
        catch { return DriftState.Changed; }
    }
}
