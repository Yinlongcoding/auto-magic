using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AM.Desktop.Views;

/// <summary>true → Collapsed，false → Visible。用于"没有…"之类的空状态提示。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
