using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Cadence.App.Interop;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Views;

namespace Cadence.App.UI.Services;

/// <summary>Modal dialogs (themed, dark title bar) and the two shell actions. Dialogs are owned by the main window and centered on it.</summary>
internal sealed class WpfDialogs : IDialogs, IShell
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) =>
        Task.FromResult(ShowPanel(title, new ConfirmPanel(title, message, confirmText, danger)));

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger, string cancelText) =>
        Task.FromResult(ShowPanel(title, new ConfirmPanel(title, message, confirmText, danger, cancelText: cancelText)));

    public Task<bool> ShowForeignDetailAsync(ForeignDetailText detail) =>
        Task.FromResult(ShowPanel(detail.Name, new ForeignDetailPanel(detail)));

    public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) =>
        Task.FromResult(ShowPanel(title, new ConfirmPanel(title, message, confirmText, danger: true, typedWord: word)));

    public Task InfoAsync(string title, string message)
    {
        ShowPanel(title, new ConfirmPanel(title, message, null, danger: false));
        return Task.CompletedTask;
    }

    public void ShowAbout() => ShowPanel("About Cadence", new AboutPanel());

    IPanelHost? _panels;

    /// <summary>[P23] In-app panels (the agent features): drawn inside the main window by <see cref="IPanelHost"/>, never as a window.</summary>
    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<PanelChoice> choices) => _panels?.ShowChoiceAsync(title, message, choices) ?? Task.FromResult(-1);

    public void AttachPanels(IPanelHost host) => _panels = host;

    public string? PickProgram()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program, shortcut or script",
            Filter = "Programs and scripts|*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.py;*.js|All files|*.*",
            CheckFileExists = true,
        };
        Window? owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null;
        bool? ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FileName : null;
    }

    public void OpenTaskScheduler()
    {
        try { Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing sensible to do */ }
    }

    public void OpenLink(string uri)
    {
        // Only the links the app itself offers (Windows Security); never a string that came from a task.
        if (!uri.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing sensible to do */ }
    }

    public bool OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        return true;
    }

    public bool RevealFile(string path)
    {
        // A quote can't be part of a Windows path, so a string that holds one is not a path to hand to Explorer's command line.
        if (string.IsNullOrEmpty(path) || path.Contains('"', StringComparison.Ordinal) || !File.Exists(path)) return false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        return true;
    }

    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* the clipboard is busy: nothing sensible to do */ }
    }

    static bool ShowPanel(string title, FrameworkElement panel)
    {
        bool result = false;
        Window? owner = Application.Current.MainWindow is { IsVisible: true } main ? main : null;
        var w = new Window
        {
            Title = title,
            Content = panel,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        w.SourceInitialized += (_, _) => DarkTitleBar.Apply(w);
        w.ContentRendered += (_, _) =>
        {
            if (panel is ConfirmPanel c) c.FocusFirst();
            else if (panel is AboutPanel a) a.FocusFirst();
            else if (panel is ForeignDetailPanel f) f.FocusFirst();
        };
        if (panel is ConfirmPanel confirm) confirm.Finished += ok => { result = ok; w.Close(); };
        if (panel is AboutPanel about) about.Finished += () => w.Close();
        if (panel is ForeignDetailPanel foreign) foreign.Finished += open => { result = open; w.Close(); };
        w.ShowDialog();
        return result;
    }
}

/// <summary>Dark title bar through <c>DwmSetWindowAttribute</c> (attribute 20, falling back to 19 on older Windows 10 builds).</summary>
internal static class DarkTitleBar
{
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            Native.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }
}
