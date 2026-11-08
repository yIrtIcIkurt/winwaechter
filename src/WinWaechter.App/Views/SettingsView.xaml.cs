using System.Windows.Controls;
using System.Windows.Input;

namespace WinWaechter.App.Views;

public partial class SettingsView
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Enter commits the Ablage days like leaving the field does (no save per keystroke).
    void OnAblageDaysKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
