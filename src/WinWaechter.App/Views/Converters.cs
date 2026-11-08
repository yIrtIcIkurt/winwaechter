using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinWaechter.App.Views;

/// <summary>Visible when the value is "truthy" (true, non-null, non-empty, count &gt; 0). Parameter "not" inverts.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "not") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
