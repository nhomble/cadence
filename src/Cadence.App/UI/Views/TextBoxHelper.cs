using System.Windows;

namespace Cadence.App.UI.Views;

/// <summary>Attached <c>Placeholder</c> text for the themed TextBox: shown in Dim while the box is empty.</summary>
internal static class TextBoxHelper
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(TextBoxHelper), new PropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);
}
