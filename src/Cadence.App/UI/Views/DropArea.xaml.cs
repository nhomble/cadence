using System.Windows.Controls;

namespace Cadence.App.UI.Views;

/// <summary>The dashed "…or drop a program, shortcut or script here" area of the empty state and the template sheet. The window (FileDrop on MainView) takes the drop; this only invites it.</summary>
internal sealed partial class DropArea : UserControl
{
    public DropArea()
    {
        InitializeComponent();
    }
}
