using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.Services;

/// <summary>Follows the Windows app theme (light/dark) and switches live.</summary>
public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static readonly Uri LightUri = new("pack://application:,,,/WinWaechter.App;component/Theme/Light.xaml", UriKind.Absolute);
    static readonly Uri DarkUri = new("pack://application:,,,/WinWaechter.App;component/Theme/Dark.xaml", UriKind.Absolute);
    static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static void Start(Application app)
    {
        Apply(app, ReadIsDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => Apply(app, ReadIsDark()));
        };
    }

    public static bool ReadIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Farbschema nicht lesbar", ex);
            return false;
        }
    }

    static void Apply(Application app, bool dark)
    {
        if (_current is not null && dark == IsDark) return;
        var dict = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = app.Resources.MergedDictionaries;
        var index = _current is null ? -1 : merged.IndexOf(_current);
        if (index >= 0) merged[index] = dict; else merged.Insert(1, dict);   // after Tokens, before Controls
        _current = dict;
        IsDark = dark;
        foreach (Window w in app.Windows) TitleBar.Apply(w);
    }
}

/// <summary>Keeps the standard Windows title bar but matches it to the theme (DWM, Windows 11).</summary>
public static class TitleBar
{
    const int DwmwaUseImmersiveDarkMode = 20, DwmwaCaptionColor = 35, DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Window w)
    {
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
    }

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (Application.Current.TryFindResource("CanvasColor") is Color bg)
        {
            var caption = bg.R | (bg.G << 8) | (bg.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        if (Application.Current.TryFindResource("Ink") is SolidColorBrush ink)
        {
            var text = ink.Color.R | (ink.Color.G << 8) | (ink.Color.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        }
    }
}
