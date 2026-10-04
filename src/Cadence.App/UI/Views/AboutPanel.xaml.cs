using System.Windows;
using System.Windows.Controls;
using Cadence.Core;

namespace Cadence.App.UI.Views;

/// <summary>The small About dialog's content.</summary>
internal sealed partial class AboutPanel : UserControl
{
    public AboutPanel(string? version = null)
    {
        InitializeComponent();
        VersionText.Text = version ?? AppVersion.Display();
    }

    public event Action? Finished;

    public void FocusFirst() => CloseButton.Focus();

    void OnClose(object sender, RoutedEventArgs e) => Finished?.Invoke();
}
