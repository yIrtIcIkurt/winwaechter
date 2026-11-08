using System.Windows;
using WinWaechter.App.Services;

namespace WinWaechter.App.Views;

/// <summary>Themed confirmation / message dialog. confirmText null = message only.</summary>
public partial class MessageDialog
{
    public MessageDialog(string title, string message, IReadOnlyList<string>? items, string? confirmText, bool final)
    {
        InitializeComponent();
        TitleBar.Attach(this);
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        if (items is { Count: > 0 })
        {
            ItemsList.ItemsSource = items;
            ItemsFrame.Visibility = Visibility.Visible;
        }

        if (confirmText is null)
        {
            // information only: one button that closes
            ConfirmButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Schließen";
            CancelButton.IsDefault = true;
            CancelButton.Style = (Style)FindResource("Button.Primary");
            Loaded += (_, _) => CancelButton.Focus();
            return;
        }

        ConfirmButton.Content = confirmText;
        ConfirmButton.Style = (Style)FindResource(final ? "Button.Final" : "Button.Primary");
        // for final actions the safe choice has the focus, so Enter does not destroy anything
        if (final) { CancelButton.IsDefault = true; Loaded += (_, _) => CancelButton.Focus(); }
        else { ConfirmButton.IsDefault = true; Loaded += (_, _) => ConfirmButton.Focus(); }
    }

    void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
