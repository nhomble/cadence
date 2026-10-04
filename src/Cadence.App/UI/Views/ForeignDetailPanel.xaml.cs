using System.Windows;
using System.Windows.Controls;
using Cadence.App.UI.Logic;

namespace Cadence.App.UI.Views;

/// <summary>The read-only detail of a task Cadence does not manage (opened from the "Other Task Scheduler tasks" rows). Its one action is "Open in Task Scheduler".</summary>
internal sealed partial class ForeignDetailPanel : UserControl
{
    public ForeignDetailPanel(ForeignDetailText detail)
    {
        InitializeComponent();
        TitleText.Text = detail.Name;
        PathText.Text = detail.Path;
        RowsList.ItemsSource = detail.Rows;
        NoteText.Text = detail.Note;
    }

    /// <summary>True when the user chose "Open in Task Scheduler".</summary>
    public event Action<bool>? Finished;

    public void FocusFirst() => OpenButton.Focus();

    void OnClose(object sender, RoutedEventArgs e) => Finished?.Invoke(false);

    void OnOpen(object sender, RoutedEventArgs e) => Finished?.Invoke(true);
}
