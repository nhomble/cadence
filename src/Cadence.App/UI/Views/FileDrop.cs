using System.Runtime.InteropServices;
using System.Windows;

namespace Cadence.App.UI.Views;

/// <summary>
/// File drag and drop for one element (the main view): files from Explorer (<see cref="DataFormats.FileDrop"/>) are accepted with the Copy
/// cursor and reported to <c>hover</c> while they are over the element; anything else is refused. The handlers listen to the Preview (tunnelling)
/// events so a text box under the pointer can't take the drop. Dropping never blocks Explorer: the <c>dropped</c> callback is started and not waited for.
/// </summary>
internal static class FileDrop
{
    /// <summary>The file and folder paths of a drag, in drop order; empty when it holds no files or can't be read.</summary>
    public static IReadOnlyList<string> GetPaths(IDataObject? data)
    {
        if (!HasFiles(data)) return [];
        try
        {
            return data!.GetData(DataFormats.FileDrop) is string[] files ? files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() : [];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or ExternalException)
        {
            return [];
        }
    }

    public static bool HasFiles(IDataObject? data)
    {
        if (data is null) return false;
        try { return data.GetDataPresent(DataFormats.FileDrop); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException) { return false; }
    }

    /// <summary>Makes <paramref name="target"/> a drop target for files. <paramref name="hover"/> gets true while a file is over it and false when it leaves or drops.</summary>
    public static void Attach(UIElement target, Action<bool> hover, Func<IReadOnlyList<string>, Task> dropped)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(hover);
        ArgumentNullException.ThrowIfNull(dropped);
        target.AllowDrop = true;
        target.PreviewDragEnter += (_, e) => Over(e, hover);
        target.PreviewDragOver += (_, e) => Over(e, hover);
        target.PreviewDragLeave += (_, e) => { hover(false); e.Handled = true; };
        target.PreviewDrop += (_, e) => Dropped(e, hover, dropped);
    }

    /// <summary>The handler logic of DragEnter and DragOver (also called directly by tests with a constructed data object).</summary>
    public static void Over(DragEventArgs e, Action<bool> hover)
    {
        bool files = HasFiles(e.Data);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        hover(files);
        e.Handled = true;
    }

    /// <summary>The handler logic of Drop.</summary>
    public static void Dropped(DragEventArgs e, Action<bool> hover, Func<IReadOnlyList<string>, Task> dropped)
    {
        hover(false);
        IReadOnlyList<string> paths = GetPaths(e.Data);
        e.Effects = paths.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (paths.Count > 0) _ = dropped(paths);
    }
}
