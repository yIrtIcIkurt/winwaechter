namespace WinWaechter.App.Views;

public partial class ShellWindow
{
    public ShellWindow()
    {
        InitializeComponent();
        WinWaechter.App.Services.TitleBar.Attach(this);
    }
}
