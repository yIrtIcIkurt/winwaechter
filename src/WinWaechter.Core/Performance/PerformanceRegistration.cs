using WinWaechter.Core.Changes;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>Registers the four change kinds of the Leistung page so the journal can undo them.</summary>
public static class PerformanceRegistration
{
    public static void Register(ChangeFactory f, IRegistry reg, IPowerModes power, IAnimationSetting anim,
        IHibernation hib, ISettingBroadcast broadcast)
    {
        f.Register("perfvalue", r => PerfValueChange.FromRecord(r, reg, broadcast));
        f.Register("powermode", r => PowerModeChange.FromRecord(r, power));
        f.Register("spi", r => AnimationChange.FromRecord(r, anim));
        f.Register("hibernate", r => HibernateChange.FromRecord(r, hib));
    }
}
