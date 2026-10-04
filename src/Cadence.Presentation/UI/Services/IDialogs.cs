using Cadence.App.UI.Logic;

namespace Cadence.App.UI.Services;

/// <summary>One button of an in-app panel question.</summary>
public sealed record PanelChoice(string Label, bool IsPrimary = false, bool IsDanger = false);

/// <summary>The window's in-app panel layer (the main view model): shows a question INSIDE the window, never a separate window.</summary>
public interface IPanelHost
{
    /// <summary>The index of the chosen choice, or -1 when dismissed (Esc, a click outside).</summary>
    Task<int> ShowChoiceAsync(string title, string message, IReadOnlyList<PanelChoice> choices);
}

/// <summary>The few modal questions the app asks. The WPF implementation is <c>WpfDialogs</c>; render mode and tests use <see cref="NoDialogs"/>.</summary>
public interface IDialogs
{
    /// <summary>A yes/no question. <paramref name="danger"/> paints the confirm button Red.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger);

    /// <summary>The same question with a custom label on the cancel button ("Close" for a read-only notice). The default ignores the label.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger, string cancelText) => ConfirmAsync(title, message, confirmText, danger);

    /// <summary>The read-only detail of a task Cadence does not manage. True when the user chose "Open in Task Scheduler".</summary>
    Task<bool> ShowForeignDetailAsync(ForeignDetailText detail) => Task.FromResult(false);

    /// <summary>Asks the user to type <paramref name="word"/> before the confirm button is enabled (Remove all).</summary>
    Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText);

    /// <summary>A message with one OK button.</summary>
    Task InfoAsync(string title, string message);

    void ShowAbout();

    /// <summary>[P23] A question drawn inside the window (the agent features never open a window of their own). Returns the chosen index, or -1 when dismissed or when no window is attached.</summary>
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<PanelChoice> choices) => Task.FromResult(-1);

    /// <summary>Called once by the window's view model so <see cref="ChooseAsync"/> can reach it.</summary>
    void AttachPanels(IPanelHost host) { }

    /// <summary>The Browse… dialog of the editor (programs, shortcuts and scripts). Null when cancelled.</summary>
    string? PickProgram();
}

/// <summary>Things the app asks Windows to do (start Task Scheduler, open a folder).</summary>
public interface IShell
{
    void OpenTaskScheduler();

    /// <summary>Opens the folder in Explorer. Returns false when it does not exist (nothing is created).</summary>
    bool OpenFolder(string path);

    /// <summary>Opens Explorer with the file highlighted (explorer.exe /select). Returns false when it couldn't be asked. The default does nothing.</summary>
    bool RevealFile(string path) => false;

    /// <summary>Puts text on the clipboard.</summary>
    void CopyText(string text);

    /// <summary>Opens a link such as <c>windowsdefender://</c> (the Windows Security app, nothing more) with its registered app. The default does nothing.</summary>
    void OpenLink(string uri) { }
}

/// <summary>Answers every question with "no" and does nothing: for render mode.</summary>
public sealed class NoDialogs : IDialogs, IShell
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) => Task.FromResult(false);

    public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) => Task.FromResult(false);

    public Task InfoAsync(string title, string message) => Task.CompletedTask;

    public void ShowAbout() { }

    IPanelHost? _panels;

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<PanelChoice> choices) => _panels?.ShowChoiceAsync(title, message, choices) ?? Task.FromResult(-1);

    public void AttachPanels(IPanelHost host) => _panels = host;

    public string? PickProgram() => null;

    public void OpenTaskScheduler() { }

    public bool OpenFolder(string path) => false;

    public void CopyText(string text) { }
}
