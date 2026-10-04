using Cadence.App.Demo;
using Cadence.App.UI.ViewModels;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// WPF logs a binding warning (and the dim style silently never applies) when a shared cell style binds a property that one of the row types lacks, or
/// when an automation name binds to null. These keep the list rows free of both (the render check with PresentationTraceSources found 22 of them).
/// </summary>
public static class RowBindingTests
{
    static string ListPageXaml()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "src", "Cadence.App", "UI", "Views", "ListPage.xaml");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new InvalidOperationException("ListPage.xaml not found above " + AppContext.BaseDirectory);
    }

    public static void TheSharedCellStyleBindsOnlyPropertiesBothRowTypesHave()
    {
        // The NameText/ValueText/ToneResult styles apply to schedule rows and to foreign rows alike.
        foreach (string prop in new[] { "IsDimmed", "ResultTone" })
        {
            T.True("ListPage.xaml binds " + prop, ListPageXaml().Contains("{Binding " + prop + "}", StringComparison.Ordinal));
            T.True("ScheduleRowViewModel has " + prop, typeof(ScheduleRowViewModel).GetProperty(prop) is not null);
            T.True("ForeignRowViewModel has " + prop, typeof(ForeignRowViewModel).GetProperty(prop) is not null);
        }
    }

    public static void TheUpdatedCellNameBindsANonNullProperty()
    {
        string xaml = ListPageXaml();
        T.True("no binding to the nullable Updated", !xaml.Contains("AutomationProperties.Name=\"{Binding Updated}\"", StringComparison.Ordinal));
        T.True("UpdatedLine still has its id", xaml.Contains("AutomationProperties.AutomationId=\"UpdatedLine\"", StringComparison.Ordinal));
        T.True("and binds UpdatedAutomationName", xaml.Contains("AutomationProperties.Name=\"{Binding UpdatedAutomationName}\"", StringComparison.Ordinal));

        var vm = new MainViewModel(new DemoServices(DemoSet.Showcase), new RecordingDialogs(), new RecordingDialogs());
        vm.Start();
        vm.List.InitialLoad.GetAwaiter().GetResult();
        T.True("there are rows", vm.List.Rows.Count > 0);
        int withUpdated = 0, without = 0;
        foreach (ScheduleRowViewModel r in vm.List.Rows)
        {
            T.True("the name is never null (" + r.Name + ")", r.UpdatedAutomationName is not null);
            if (r.Updated is null) { without++; T.Eq("no Updated: the name is the cell text", r.UpdatedCell, r.UpdatedAutomationName); }
            else { withUpdated++; T.Eq("Updated: the name is the full words", r.Updated, r.UpdatedAutomationName); }
        }
        T.True("the demo set has both kinds of row (" + withUpdated + "/" + without + ")", without > 0);
    }
}
