using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cadence.App.UI.ViewModels;

namespace Cadence.App.UI.Views;

/// <summary>
/// The schedule editor. Two columns (the form, then the runs) from 1100 px wide; below that the runs move under the form. A job that is not
/// saved yet has no runs, so its form is centered on its own. DataContext is an <see cref="EditorViewModel"/>.
/// </summary>
internal sealed partial class EditorView : UserControl
{
    const double WideBreakpoint = 1100;

    // While a run is going (or was just started with Run now) the runs are looked at again every 3 s (PLAN 7.3). The view model decides whether
    // a tick does anything (RunsSectionViewModel.NeedsLivePoll), so this timer is idle most of the time; it does nothing while the window is minimized.
    readonly DispatcherTimer _live = new() { Interval = RunsSectionViewModel.LivePollInterval };

    public EditorView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
        DataContextChanged += OnDataContextChanged;
        _live.Tick += async (_, _) =>
        {
            if (DataContext is EditorViewModel vm && Window.GetWindow(this) is { WindowState: not WindowState.Minimized })
                await vm.Runs.LiveTickAsync();
        };
        Loaded += (_, _) => _live.Start();
        Unloaded += (_, _) => _live.Stop();
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
        if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnVmChanged;
        UpdateLayoutMode();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.HasRunsPanel)) UpdateLayoutMode();
    }

    void UpdateLayoutMode()
    {
        bool hasRuns = DataContext is EditorViewModel { HasRunsPanel: true };
        bool wide = ActualWidth >= WideBreakpoint;
        if (hasRuns && wide)
        {
            Layout.MaxWidth = 1180;
            GapCol.Width = new GridLength(1, GridUnitType.Star);
            GapCol.MinWidth = 32;
            RunsCol.Width = new GridLength(430);
            Grid.SetRow(RunsPanel, 0);
            Grid.SetColumn(RunsPanel, 2);
            RunsPanel.Margin = new Thickness(0);
        }
        else if (hasRuns)
        {
            Layout.MaxWidth = 660;
            GapCol.Width = new GridLength(0);
            GapCol.MinWidth = 0;
            RunsCol.Width = new GridLength(0);
            Grid.SetRow(RunsPanel, 1);
            Grid.SetColumn(RunsPanel, 0);
            RunsPanel.Margin = new Thickness(0, 28, 0, 0);
        }
        else
        {
            Layout.MaxWidth = 660;
            GapCol.Width = new GridLength(0);
            GapCol.MinWidth = 0;
            RunsCol.Width = new GridLength(0);
        }
        // [CP-C] Back (and the editor's ⋯) line up with the form's left edge and the page's right edge in every layout.
        TopBar.MaxWidth = Layout.MaxWidth;
        MenuLayer.MaxWidth = Layout.MaxWidth;
    }
}
