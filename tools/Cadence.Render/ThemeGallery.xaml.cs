using System.Windows.Controls;

namespace Cadence.App.UI.Views;

/// <summary>Every styled control in one place; used only by the <c>theme</c> render state to check the templates by eye.</summary>
internal sealed partial class ThemeGallery : UserControl
{
    public ThemeGallery()
    {
        InitializeComponent();
        Sample.ItemsSource = new[]
        {
            new SampleRow("Sync notes", "every 15 min", "Ok · 14 min ago"),
            new SampleRow("Photo backup", "daily", "Failed (exit 3) · 9 h ago"),
            new SampleRow("Disk cleanup", "weekly", "Couldn't start (access denied)"),
        };
    }

    /// <summary>A row of the gallery's sample table.</summary>
    sealed record SampleRow(string Name, string Rate, string Result);
}
