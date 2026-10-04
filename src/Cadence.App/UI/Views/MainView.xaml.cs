using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Cadence.App.UI.ViewModels;

namespace Cadence.App.UI.Views;

/// <summary>The window's content: the toolbar (list only), the list or editor, the PLUS sheet, the toast and the status bar. DataContext is a <see cref="MainViewModel"/>.</summary>
internal sealed partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.List.FocusFilterRequested -= OnFocusFilter;
            if (e.NewValue is MainViewModel vm) vm.List.FocusFilterRequested += OnFocusFilter;
        };
        // A file dropped anywhere on the window: in an editor it sets the program, otherwise it starts a new job (MainViewModel.HandleDropAsync).
        FileDrop.Attach(this,
            hovering => (DataContext as MainViewModel)?.SetDropHover(hovering),
            paths => DataContext is MainViewModel vm ? vm.HandleDropAsync(paths) : Task.CompletedTask);
    }

    /// <summary>Ctrl+F: the keyboard goes into the filter box with its text selected.</summary>
    void OnFocusFilter(object? sender, EventArgs e)
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    /// <summary>Esc clears the filter, and a second Esc moves on to the table; Down goes to the table at once.</summary>
    void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            if (vm.List.FilterText.Length > 0) vm.List.FilterText = "";
            else vm.List.RequestFocusTable();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            vm.List.RequestFocusTable();
            e.Handled = true;
        }
    }

    void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)Resources["MainMenu"];
        menu.DataContext = DataContext;
        // [CP-D] Opening the menu acknowledges the Orange "AI agents were turned back on" note.
        (DataContext as MainViewModel)?.AcknowledgeAgentNote();
        menu.PlacementTarget = MenuButton;
        // Right-aligned under the button, whatever the menu's width turns out to be.
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.None)];
        menu.IsOpen = true;
    }
}
