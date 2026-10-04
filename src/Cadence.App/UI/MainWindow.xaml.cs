using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Cadence.App.Interop;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;

namespace Cadence.App.UI;

/// <summary>
/// The one window (PLAN 7.2): dark title bar, single-instance "show me" hook, activation / 30 s refresh policy and Escape handling.
/// Everything else lives in <see cref="MainViewModel"/> and the views.
/// </summary>
internal sealed partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly DispatcherTimer _poll = new() { Interval = MainViewModel.PollInterval };
    bool _closeConfirmed;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Main.DataContext = vm;
        RestorePlacement();

        Activated += (_, _) => vm.SetWindowActive(true);
        Deactivated += (_, _) => vm.SetWindowActive(false);
        StateChanged += (_, _) => vm.SetWindowActive(WindowState != WindowState.Minimized && IsActive);
        _poll.Tick += (_, _) => vm.OnPollTick();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    /// <summary>The window opens where it was last closed (fitted to the screens that exist now), else centered at the default size.</summary>
    void RestorePlacement()
    {
        if (!WindowPlacement.TryParse(_vm.Services.Settings.WindowPlacement, out PlacementData saved)) return;
        var screen = new ScreenRect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        PlacementData fit = WindowPlacement.Fit(saved, [screen]);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = fit.Left;
        Top = fit.Top;
        Width = fit.Width;
        Height = fit.Height;
        if (fit.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlacement()
    {
        Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || r.Width < 100 || r.Height < 100) return;
        _vm.Services.Settings.WindowPlacement = WindowPlacement.Format(new PlacementData(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized));
    }

    /// <summary>Closing with an editor that has unsaved changes asks "Discard changes?" first (the answer arrives asynchronously, so the close is cancelled and repeated).</summary>
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeConfirmed && _vm.HasUnsavedChanges)
        {
            e.Cancel = true;
            _ = AskThenCloseAsync();
            return;
        }
        SavePlacement();
    }

    async Task AskThenCloseAsync()
    {
        if (!await _vm.ConfirmCloseAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Raised after the first frame is on screen (also used by <c>--measure-startup</c>).</summary>
    public event Action? FirstFrame;

    /// <summary>
    /// Test hook (see Program.cs): the window is a tool window that never takes the focus. Tiling window managers such as GlazeWM do not manage tool windows,
    /// so an automated UI check does not get pulled into the owner's layout or pop up on their screen (UI Automation still sees every control).
    /// </summary>
    public bool QuietTestMode { get; set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
        if (QuietTestMode)
        {
            const int GwlExStyle = -20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExAppWindow = 0x40000;
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Cadence.App.Interop.Native.SetWindowLong(hwnd, GwlExStyle, (Cadence.App.Interop.Native.GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow);
        }
        // A second start of Cadence broadcasts CADENCE_SHOWME; bring this window to the front.
        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FirstFrame?.Invoke();
        // The first load starts only after the first frame, so the window is on screen before any Task Scheduler call is made.
        Dispatcher.BeginInvoke(() =>
        {
            _vm.Start();
            _poll.Start();
        }, DispatcherPriority.Background);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.HandleEscape()) e.Handled = true;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == ShowMe.Id)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
