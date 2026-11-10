using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Performance;

/// <summary>Tells running programs that a system setting changed, so they pick it up without a restart.</summary>
public interface ISettingBroadcast
{
    void ImmersiveColorSetChanged();
}

public sealed class WinSettingBroadcast : ISettingBroadcast
{
    public void ImmersiveColorSetChanged()
    {
        try
        {
            SettingChangeNative.SendMessageTimeout(SettingChangeNative.HWND_BROADCAST, SettingChangeNative.WM_SETTINGCHANGE,
                UIntPtr.Zero, "ImmersiveColorSet", SettingChangeNative.SMTO_ABORTIFHUNG, 2000, out _);
        }
        catch
        {
            // Best effort: programs pick the change up on their next start anyway.
        }
    }
}
