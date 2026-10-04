using System.Reflection;
using System.Windows;
using Cadence.App.UI.Views;
using Cadence.TestHarness;

namespace Cadence.App.Tests;

/// <summary>
/// Phase 14 wiring, the WPF half: the drag events themselves, raised on a real WPF element with a constructed data object (nothing dropped is ever run).
/// They moved here from Cadence.Tests (Phase 6 of docs\REFACTOR-modules.md) because FileDrop lives in the WPF app.
/// </summary>
public static class FileDropTests
{
    // ---- running WPF objects on an STA thread ----

    static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA test failed: " + error.Message, error);
    }

    static DragEventArgs Drag(RoutedEvent routed, IDataObject data, UIElement source)
    {
        ConstructorInfo? ctor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)]);
        var e = (DragEventArgs)ctor!.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, source, new Point(10, 10)]);
        e.RoutedEvent = routed;
        e.Source = source;
        return e;
    }

    static DataObject Files(params string[] paths) => new(DataFormats.FileDrop, paths);

    // ---- the drag events ----

    public static void RaisingDragEventsOnAnElementReportsHoverAndDrops()
    {
        Sta(() =>
        {
            var target = new System.Windows.Controls.Border();
            var hovers = new List<bool>();
            var dropped = new List<IReadOnlyList<string>>();
            FileDrop.Attach(target, hovers.Add, paths => { dropped.Add(paths); return Task.CompletedTask; });
            T.True("AllowDrop is switched on", target.AllowDrop);

            DragEventArgs enter = Drag(UIElement.PreviewDragEnterEvent, Files(@"C:\s\a.py"), target);
            target.RaiseEvent(enter);
            T.True("DragEnter with a file: hover on", hovers.SequenceEqual([true]));
            T.Eq("copy cursor", DragDropEffects.Copy, enter.Effects);
            T.True("handled", enter.Handled);

            DragEventArgs over = Drag(UIElement.PreviewDragOverEvent, Files(@"C:\s\a.py"), target);
            target.RaiseEvent(over);
            T.Eq("DragOver keeps the copy cursor", DragDropEffects.Copy, over.Effects);

            target.RaiseEvent(Drag(UIElement.PreviewDragLeaveEvent, Files(@"C:\s\a.py"), target));
            T.True("DragLeave: hover off", hovers.Last() == false);

            target.RaiseEvent(Drag(UIElement.PreviewDragEnterEvent, Files(@"C:\s\a.py"), target));
            DragEventArgs drop = Drag(UIElement.PreviewDropEvent, Files(@"C:\s\a.py", @"C:\s\b.ps1"), target);
            target.RaiseEvent(drop);
            T.True("Drop: hover off", hovers.Last() == false);
            T.Eq("one callback", 1, dropped.Count);
            T.True("both paths in drop order", dropped[0].SequenceEqual([@"C:\s\a.py", @"C:\s\b.ps1"]));
            T.Eq("drop effect", DragDropEffects.Copy, drop.Effects);
            T.True("drop handled", drop.Handled);
        });
    }

    public static void DraggingSomethingThatIsNotAFileIsRefused()
    {
        Sta(() =>
        {
            var target = new System.Windows.Controls.Border();
            var hovers = new List<bool>();
            int drops = 0;
            FileDrop.Attach(target, hovers.Add, _ => { drops++; return Task.CompletedTask; });
            var text = new DataObject(DataFormats.Text, "just some text");
            DragEventArgs enter = Drag(UIElement.PreviewDragEnterEvent, text, target);
            target.RaiseEvent(enter);
            T.Eq("no cursor", DragDropEffects.None, enter.Effects);
            T.True("no hover", hovers.SequenceEqual([false]));
            DragEventArgs drop = Drag(UIElement.PreviewDropEvent, text, target);
            target.RaiseEvent(drop);
            T.Eq("nothing dropped", 0, drops);
            T.Eq("no effect", DragDropEffects.None, drop.Effects);
        });
    }

    public static void GetPathsReadsTheFileDropFormat()
    {
        Sta(() =>
        {
            T.True("null data", FileDrop.GetPaths(null).Count == 0 && !FileDrop.HasFiles(null));
            T.True("text data", FileDrop.GetPaths(new DataObject(DataFormats.Text, "x")).Count == 0);
            T.True("paths", FileDrop.GetPaths(Files(@"C:\a b\x.exe", @"\\srv\share\y.lnk")).SequenceEqual([@"C:\a b\x.exe", @"\\srv\share\y.lnk"]));
            T.True("blank entries are dropped", FileDrop.GetPaths(Files(@"C:\x.exe", "", "  ")).SequenceEqual([@"C:\x.exe"]));
            T.True("HasFiles", FileDrop.HasFiles(Files(@"C:\x.exe")));
        });
    }
}
