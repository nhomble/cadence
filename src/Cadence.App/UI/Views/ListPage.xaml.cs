using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cadence.App.UI.Logic;
using Cadence.App.UI.ViewModels;

namespace Cadence.App.UI.Views;

/// <summary>
/// The home content: the table of schedules (columns fitted to the width, click-to-sort headers, multi-select, context menu), its one-line bodies for loading,
/// empty and "no match", and the table of other tasks. The rules (which columns fit, how rows sort and filter) are in <see cref="GridLogic"/>; this class only
/// puts them on screen.
/// </summary>
internal sealed partial class ListPage : UserControl
{
    /// <summary>The scroll bar's width plus a hair: the table is this much narrower than its control.</summary>
    const double ScrollReserve = 12;

    ListPageViewModel? _vm;
    bool _restoring;
    bool _focusWasInTable;
    List<GridColumn> _shown = [];

    public ListPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Table.SelectionChanged += OnSelectionChanged;
        Table.PreviewKeyDown += OnTableKeyDown;
        Table.MouseDoubleClick += OnTableDoubleClick;
        Table.PreviewMouseRightButtonDown += OnTableRightClick;
        Table.PreviewMouseRightButtonUp += OnTableRightUp;
        Table.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        ForeignTable.PreviewKeyDown += OnForeignKeyDown;
        ForeignTable.MouseDoubleClick += OnForeignDoubleClick;
        // A table built after the selection was made (back from the editor, a render) shows it as soon as its rows exist.
        Table.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (Table.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated && !_restoring && _vm is { SelectedRows.Count: > 0 } && Table.SelectedItems.Count == 0)
                OnRestoreSelection(this, EventArgs.Empty);
        };
        SizeChanged += (_, _) => ApplyColumns();
        Loaded += (_, _) => ApplyColumns();
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SelectionRestoreRequested -= OnRestoreSelection;
            _vm.RowsRebuilding -= OnRowsRebuilding;
            _vm.FocusTableRequested -= OnFocusTable;
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as ListPageViewModel;
        if (_vm is null) return;
        _vm.SelectionRestoreRequested += OnRestoreSelection;
        _vm.RowsRebuilding += OnRowsRebuilding;
        _vm.FocusTableRequested += OnFocusTable;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyColumns(force: true);
    }

    void OnColumnsChanged(object? sender, EventArgs e) => ApplyColumns(force: true);

    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ListPageViewModel.Sort)) ApplyColumns(force: true);
    }

    // ---- columns ----

    DataTemplate Cell(GridColumn column) => (DataTemplate)Resources[column switch
    {
        GridColumn.Name => "NameCell",
        GridColumn.Rate => "RateCell",
        GridColumn.Schedule => "ScheduleCell",
        GridColumn.NextRun => "NextCell",
        GridColumn.LastResult => "ResultCell",
        GridColumn.AvgTime => "AvgCell",
        GridColumn.Success => "SuccessCell",
        GridColumn.State => "StateCell",
        _ => "UpdatedCell",
    }];

    /// <summary>Fits the columns to the width (priority order, see <see cref="GridLogic.Fit"/>); rebuilds the headers when the set or the sort changed, else only the widths.</summary>
    void ApplyColumns(bool force = false)
    {
        if (_vm is null || Table.ActualWidth < 1) return;
        IReadOnlyList<ColumnLayout> layout = GridLogic.Fit(Math.Max(0, Table.ActualWidth - ScrollReserve), _vm.HiddenColumns);
        List<GridColumn> wanted = layout.Select(c => c.Column).ToList();
        if (!force && wanted.SequenceEqual(_shown) && TableView.Columns.Count == wanted.Count)
        {
            for (int i = 0; i < layout.Count; i++) TableView.Columns[i].Width = layout[i].Width;
        }
        else
        {
            _shown = wanted;
            TableView.Columns.Clear();
            foreach (ColumnLayout c in layout)
            {
                ColumnSpec spec = GridLogic.Spec(c.Column);
                bool sorted = _vm.SortColumn == c.Column;
                var header = new StackPanel { Orientation = Orientation.Horizontal, Tag = c.Column, HorizontalAlignment = spec.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left };
                header.Children.Add(new TextBlock { Text = spec.Header, Foreground = (Brush)FindResource(sorted ? "FgBrush" : "DimTextBrush"), VerticalAlignment = VerticalAlignment.Center });
                if (sorted)
                    header.Children.Add(new TextBlock
                    {
                        Text = _vm.SortDescending ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = (Brush)FindResource("FgBrush"),
                        Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    });
                var column = new GridViewColumn { Header = header, Width = c.Width, CellTemplate = Cell(c.Column) };
                if (spec.RightAligned)
                {
                    var style = new Style(typeof(GridViewColumnHeader), (Style)FindResource("GridHeader"));
                    style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                    column.HeaderContainerStyle = style;
                }
                TableView.Columns.Add(column);
            }
        }
        ApplyForeignColumns();
    }

    void ApplyForeignColumns()
    {
        double width = Math.Max(0, ForeignTable.ActualWidth - ScrollReserve);
        if (width < 1) return;
        // Name, Folder, Rate, Next run, Last result, State in proportion; the narrower the window the fewer the columns (Folder, State, Rate go first).
        (string Header, string Cell, double Share, double Min)[] all =
        [
            ("Name", "ForeignNameCell", 0.24, 150), ("Folder", "ForeignFolderCell", 0.20, 140), ("Rate", "ForeignRateCell", 0.11, 90),
            ("Next run", "ForeignNextCell", 0.14, 110), ("Last result", "ForeignResultCell", 0.22, 150), ("State", "ForeignStateCell", 0.09, 80),
        ];
        var use = all.ToList();
        foreach (string drop in new[] { "Folder", "State", "Rate" })
            if (use.Sum(c => c.Min) > width) use.RemoveAll(c => c.Header == drop);
        double share = use.Sum(c => c.Share);
        bool same = ForeignView.Columns.Count == use.Count
            && ForeignView.Columns.Select(c => (c.Header as TextBlock)?.Text).SequenceEqual(use.Select(c => c.Header));
        if (same)
        {
            for (int i = 0; i < use.Count; i++) ForeignView.Columns[i].Width = Math.Floor(width * use[i].Share / share);
            return;
        }
        ForeignView.Columns.Clear();
        foreach (var c in use)
            ForeignView.Columns.Add(new GridViewColumn
            {
                Header = new TextBlock { Text = c.Header, Foreground = (Brush)FindResource("DimTextBrush") },
                Width = Math.Floor(width * c.Share / share),
                CellTemplate = (DataTemplate)Resources[c.Cell],
            });
    }

    // ---- header: click sorts, right-click chooses the columns ----

    void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Content: FrameworkElement { Tag: GridColumn column } } && _vm is not null)
        {
            _vm.SortCommand.Execute(column);
            e.Handled = true;
        }
    }

    void OnTableRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is null) return;
        var menu = new ContextMenu { PlacementTarget = Table, Placement = PlacementMode.MousePoint };
        foreach (ColumnSpec spec in GridLogic.Columns)
        {
            GridColumn column = spec.Column;
            var item = new MenuItem { Header = spec.Header, IsCheckable = true, IsChecked = !_vm.HiddenColumns.Contains(column), IsEnabled = column != GridColumn.Name };
            item.Click += (_, _) => _vm.ToggleColumn(column);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- selection, keys, double-click ----

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || _vm is null) return;
        _vm.SetSelection(Table.SelectedItems.OfType<ScheduleRowViewModel>());
    }

    void OnRowsRebuilding(object? sender, EventArgs e) => _focusWasInTable = Table.IsKeyboardFocusWithin;

    void OnRestoreSelection(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _restoring = true;
        try
        {
            Table.SelectedItems.Clear();
            foreach (ScheduleRowViewModel row in _vm.SelectedRows) Table.SelectedItems.Add(row);
        }
        finally { _restoring = false; }
        if (!_focusWasInTable) return;
        _focusWasInTable = false;
        // The rows were rebuilt (a refresh, a sort): the keyboard focus goes back to the first selected row, or the table.
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm.SelectedRows.Count > 0 && Table.ItemContainerGenerator.ContainerFromItem(_vm.SelectedRows[0]) is ListViewItem item) item.Focus();
            else Table.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void OnFocusTable(object? sender, EventArgs e)
    {
        if (Table.SelectedItem is null && Table.Items.Count > 0) Table.SelectedIndex = 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (Table.SelectedItem is not null && Table.ItemContainerGenerator.ContainerFromItem(Table.SelectedItem) is ListViewItem item) item.Focus();
            else Table.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Enter && _vm.OpenSelectedCommand.CanExecute(null)) { _vm.OpenSelectedCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete && _vm.DeleteSelectedCommand.CanExecute(null)) { _vm.DeleteSelectedCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.R && ctrl) { if (_vm.RunSelectedCommand.CanExecute(null)) _vm.RunSelectedCommand.Execute(null); e.Handled = true; }
    }

    void OnTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is null) return;
        if (_vm.OpenSelectedCommand.CanExecute(null)) _vm.OpenSelectedCommand.Execute(null);
    }

    /// <summary>A right-click on a row that is not selected selects just that row (as in Explorer), so the menu acts on what was clicked.</summary>
    void OnTableRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { IsSelected: false } item)
        {
            Table.SelectedItems.Clear();
            item.IsSelected = true;
            item.Focus();
        }
    }

    void OnForeignKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ForeignTable.SelectedItem is IOpenRow row) { row.OpenCommand.Execute(null); e.Handled = true; }
    }

    void OnForeignDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { DataContext: IOpenRow row }) row.OpenCommand.Execute(null);
    }

    static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        while (from is not null)
        {
            if (from is T found) return found;
            from = from is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(from) : LogicalTreeHelper.GetParent(from);
        }
        return null;
    }
}
