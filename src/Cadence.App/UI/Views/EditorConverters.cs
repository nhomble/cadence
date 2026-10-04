using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cadence.App.UI.Views;

/// <summary>True gives Collapsed, False gives Visible (the opposite of <see cref="BooleanToVisibilityConverter"/>).</summary>
internal sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
