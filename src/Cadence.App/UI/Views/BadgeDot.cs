using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Cadence.App.UI.Logic;

namespace Cadence.App.UI.Views;

/// <summary>
/// The brushes of a <see cref="BadgeKind"/> as TEXT ([P25]: the dots are gone; state is said in words, and only problems and "Running" are colored).
/// </summary>
internal static class BadgeBrushes
{
    /// <summary>The brush for a result word: colored for problems and running, plain Fg for ok, quiet for the rest.</summary>
    public static Brush ForText(BadgeKind kind)
    {
        string key = kind switch
        {
            BadgeKind.Ok => "FgBrush",
            BadgeKind.Failed or BadgeKind.NotReported => "FailedBrush",
            BadgeKind.TimedOut => "TimedOutBrush",
            BadgeKind.Running => "RunningBrush",
            BadgeKind.Drifted => "FgBrush",
            BadgeKind.Warning => "WarningBrush",
            _ => "DimTextBrush",
        };
        return (Brush)(System.Windows.Application.Current?.TryFindResource(key) ?? Brushes.Gray);
    }
}

/// <summary>Binds a <see cref="BadgeKind"/> to the brush used for the result title.</summary>
internal sealed class BadgeTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is BadgeKind kind ? BadgeBrushes.ForText(kind) : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resolves a theme brush by key (for example "BlueBrush") so a view model can name an accent without referencing WPF.</summary>
internal sealed class BrushKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? System.Windows.Application.Current?.TryFindResource(key) ?? Brushes.Gray : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Columns of the template grid for a given width: one on a very narrow window, two on a narrow one, else three.</summary>
internal sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double w ? (w < 520 ? 1 : w < 780 ? 2 : 3) : 3;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
