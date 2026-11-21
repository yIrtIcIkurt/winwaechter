using System.Windows.Controls;
using System.Windows.Input;

namespace WinWaechter.App.Views;

public partial class ShellWindow
{
    public ShellWindow()
    {
        InitializeComponent();
        WinWaechter.App.Services.TitleBar.Attach(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && OpenNavItem(Keyboard.FocusedElement)) e.Handled = true;
        };
    }

    /// <summary>Enter opens the navigation item that has the keyboard focus (arrows only move the focus, Space checks).</summary>
    internal static bool OpenNavItem(object? focused)
    {
        if (focused is not RadioButton { GroupName: "Nav" } item) return false;
        item.IsChecked = true;
        return true;
    }
}
