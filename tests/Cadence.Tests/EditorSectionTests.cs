using Cadence.App.Demo;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// The editor is the DataContext and composes four section view models. An edit in a section raises its own property on the section (that is what the
/// XAML binds) and the editor's dirty state and problems on the editor; each section is reachable and is the same object every time.
/// </summary>
public static class EditorSectionTests
{
    static EditorViewModel Blank() =>
        EditorViewModel.ForTemplate(new DemoServices(DemoSet.Normal), new RecordingDialogs(), EditorHost.None, Templates.All.First(t => t.Id == "blank"));

    static List<string> Watch(System.ComponentModel.INotifyPropertyChanged o)
    {
        var names = new List<string>();
        o.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        return names;
    }

    public static void AnEditInASectionRaisesOnTheSectionAndTheEditor()
    {
        EditorViewModel ed = Blank();
        var onEditor = Watch(ed);
        var onProgram = Watch(ed.Program);
        var onSchedule = Watch(ed.Schedule);

        ed.Program.Exe = @"C:\Tools\backup.exe";
        T.True("Program.Exe raised on the program section", onProgram.Contains("Exe") && onProgram.Contains("HasProgram") && onProgram.Contains("CommandLineText"));
        T.True("the editor re-validated (dirty, footer, problems)", onEditor.Contains("IsDirty") && onEditor.Contains("FooterText") && onEditor.Contains("Problems"));
        T.True("and nothing program-specific leaked onto the editor", !onEditor.Contains("Exe") && !onEditor.Contains("HasProgram"));

        onEditor.Clear();
        ed.Schedule.IsCronMode = true;
        ed.Schedule.CronText = "0 9 * * 1-5";
        T.True("Schedule.CronText raised on the schedule section", onSchedule.Contains("CronText") && onSchedule.Contains("PreviewHeadline") && onSchedule.Contains("IsCronMode"));
        T.True("the editor noticed too", onEditor.Contains("IsDirty") && onEditor.Contains("BlockingProblem"));
        T.True("the preview is the section's", ed.Schedule.Preview.IsValid && ed.Schedule.Sentence.Length > 0);
        T.True("Save is possible now", ed.CanSave);
    }

    public static void TheSectionsAreStableAndOnlyTheNewJobHasNoRunsPanel()
    {
        EditorViewModel ed = Blank();
        T.True("the same section object every time", ReferenceEquals(ed.Program, ed.Program) && ReferenceEquals(ed.Schedule, ed.Schedule) && ReferenceEquals(ed.Runs, ed.Runs) && ReferenceEquals(ed.Notice, ed.Notice));
        T.True("a new schedule has no runs panel, no banner and an open form", !ed.HasRunsPanel && !ed.Notice.HasBanner && ed.Notice.FormEnabled && !ed.Notice.IsLocked);
        T.True("and Run now is off", !ed.Runs.CanRunNow && !ed.Runs.NeedsLivePoll);
    }
}
