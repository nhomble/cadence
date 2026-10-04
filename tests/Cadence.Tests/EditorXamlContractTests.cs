using System.Reflection;
using System.Text.RegularExpressions;
using Cadence.App.UI.ViewModels;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// The editor's view model is split into section view models (Program, Schedule, Runs, Notice), so a binding that still names a moved property compiles
/// but silently shows nothing. These read the XAML text: every section path resolves, no moved property is bound bare, every {x:Static} points at a real
/// member, and every name or id the UI scripts (tools\live-ui-agents.ps1, tools\acceptance.ps1) look up is still declared.
/// </summary>
public static class EditorXamlContractTests
{
    static readonly Dictionary<string, Type> Sections = new()
    {
        ["Program"] = typeof(ProgramSectionViewModel),
        ["Schedule"] = typeof(ScheduleSectionViewModel),
        ["Runs"] = typeof(RunsSectionViewModel),
        ["Notice"] = typeof(EditorNoticeViewModel),
    };

    static string Root()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Cadence.slnx"))) return d.FullName;
        throw new InvalidOperationException("Cadence.slnx not found above " + AppContext.BaseDirectory);
    }

    static string Views(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Cadence.App", "UI", "Views", file));

    static bool HasMember(Type t, string name, bool isStatic = false) =>
        t.GetMember(name, MemberTypes.Property | MemberTypes.Field | MemberTypes.Method, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance)).Length > 0;

    public static void EveryEditorBindingNamesAPropertyThatExistsWhereItIsBound()
    {
        int sectionPaths = 0;
        foreach (string file in new[] { "EditorView.xaml", "ProgramDropZone.xaml" })
        {
            // The resources hold the item templates (their DataContext is a row, not the editor): only the page itself is checked.
            string xaml = Views(file);
            xaml = xaml[(xaml.IndexOf("</UserControl.Resources>", StringComparison.Ordinal) + 1)..];
            foreach (Match m in Regex.Matches(xaml, @"\{Binding ([A-Za-z_]\w*(?:\.\w+)*)"))
            {
                string[] parts = m.Groups[1].Value.Split('.');
                string label = file + ": {Binding " + m.Groups[1].Value + "}";
                if (Sections.TryGetValue(parts[0], out Type? section) && parts.Length > 1)
                {
                    sectionPaths++;
                    T.True(label + " resolves on " + section.Name, HasMember(section, parts[1]));
                }
                else if (!HasMember(typeof(EditorViewModel), parts[0]) && Sections.Values.Any(s => HasMember(s, parts[0])))
                {
                    T.True(label + " names a property that moved into a section (use Section." + parts[0] + ")", false);
                }
            }
        }
        T.True("the XAML binds through the sections (" + sectionPaths + " paths)", sectionPaths > 80);
    }

    public static void EveryStaticReferenceInTheEditorXamlExists()
    {
        Assembly asm = typeof(EditorViewModel).Assembly;
        int n = 0;
        foreach (Match m in Regex.Matches(Views("EditorView.xaml"), @"\{x:Static vm:(\w+)\.(\w+)\}"))
        {
            n++;
            Type? t = asm.GetType("Cadence.App.UI.ViewModels." + m.Groups[1].Value);
            T.True(m.Value + ": the type exists", t is not null);
            if (t is not null) T.True(m.Value + ": the member exists", HasMember(t, m.Groups[2].Value, isStatic: true));
        }
        T.True("there are static references (" + n + ")", n >= 8);
    }

    public static void EverythingTheUiScriptsLookUpIsStillThere()
    {
        string xaml = string.Concat(Directory.GetFiles(Path.Combine(Root(), "src", "Cadence.App", "UI"), "*.xaml", SearchOption.AllDirectories).Select(File.ReadAllText));
        string code = string.Concat(Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(File.ReadAllText));

        // The ids and names that the owner-run scripts find rows and buttons by: declared in the XAML itself.
        foreach (string id in new[] { "UpdatedLine", "AgentsPill", "MenuButton", "MetaLine", "AgentLockItem", "RunFeedback", "ChangeSummary", "Status", "BannerTitle", "OutdatedLine", "DropZone" })
            T.True("AutomationId or x:Name '" + id + "' is declared", xaml.Contains("AutomationId=\"" + id + "\"", StringComparison.Ordinal) || xaml.Contains("x:Name=\"" + id + "\"", StringComparison.Ordinal));
        foreach (string name in new[] { "Run now", "Delete", "Open folder", "Copy path", "Save", "Cancel", "Back", "Browse", "Schedule menu", "Simple view", "Cron view", "Cron schedule", "Interval", "Name", "Enabled" })
            T.True("AutomationProperties.Name '" + name + "' is declared", xaml.Contains("AutomationProperties.Name=\"" + name + "\"", StringComparison.Ordinal));

        // And every literal the two scripts pass to their find helpers appears somewhere in the XAML or the sources (names built from data are skipped).
        var literals = new HashSet<string>(StringComparer.Ordinal);
        // The scripts are owner-run and git-ignored, so a fresh clone does not have them: compare only when both are present.
        string[] scripts = { "live-ui-agents.ps1", "acceptance.ps1" };
        bool haveScripts = scripts.All(s => File.Exists(Path.Combine(Root(), "tools", s)));
        if (!haveScripts)
            T.True("owner-run UI scripts not in this checkout: comparison skipped", true);
        foreach (string script in haveScripts ? scripts : Array.Empty<string>())
        {
            string text = File.ReadAllText(Path.Combine(Root(), "tools", script));
            foreach (Match m in Regex.Matches(text, @"\b(?:ById|ByName|Ui-Find|Ui-FindId|Ui-Need)\s+\$\w+\s+'([^']+)'"))
                literals.Add(m.Groups[1].Value);
        }
        if (haveScripts)
            T.True("the scripts look things up (" + literals.Count + ")", literals.Count >= 20);
        foreach (string lit in literals)
            T.True("'" + lit + "' is declared in the XAML or the sources", xaml.Contains("\"" + lit + "\"", StringComparison.Ordinal) || code.Contains("\"" + lit, StringComparison.Ordinal) || code.Contains(lit, StringComparison.Ordinal));
    }
}
