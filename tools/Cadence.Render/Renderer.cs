using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.Demo;
using Cadence.App.Interop;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.App.UI.Views;
using Cadence.Core.Planning;

namespace Cadence.App.UI.Render;

/// <summary>
/// The dev-only screenshot tool (PLAN 6.5): builds a named UI state from the test fixtures' sample data (never from Task Scheduler), lays it out off-screen at a fixed size and
/// saves it as a PNG with <see cref="RenderTargetBitmap"/>. The fixture clock is fixed, so the images are identical between runs. No window is shown.
/// The window's title bar is drawn by Windows and is therefore not part of the images.
/// </summary>
internal static class Renderer
{
    public static readonly string[] States =
    [
        // [P25] the home table
        "home", "home-states", "home-problems", "home-empty", "home-empty-narrow", "home-loading", "home-narrow", "home-sorted-name", "home-filtered", "home-selection", "home-foreign", "home-drop-hover",
        "template-sheet", "template-picked", "about", "confirm-delete", "theme",
        "editor-simple", "editor-cron", "editor-invalid-cron", "editor-alternative", "editor-runs", "editor-advanced", "editor-feb29", "editor-skipped-preview", "editor-narrow",
        "drop-hover", "drop-picker", "drop-shortcut", "drop-script", "drop-batch", "drop-rejected",
        "drift-banner", "drift-different-user", "outdated", "newer-version", "corrupt", "duplicate-id", "confirm-untrack", "confirm-reapply", "run-in-progress", "blocked-run",
        "foreign-detail", "editor-menu", "unsaved-prompt",
        // [P23] the agent surface (docs\screens\phase23)
        "agents-allowed", "agents-suspended-by-owner", "agents-suspended-by-agent", "agents-resumed-by-agent", "agents-resumed-outside", "agents-unreadable",
        "list-updated-by", "editor-agent-lock", "stale-save", "agent-activity", "agent-limits", "toggle-panel",
    ];

    /// <summary>Names the plan uses for a state ("Cadence.Render drift" in Phase 15's acceptance).</summary>
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drift"] = "drift-banner", ["confirm-resume-agents"] = "toggle-panel",
        // [P25] the home states were renamed; the old names still work.
        ["list"] = "home", ["list-states"] = "home-states", ["list-problems"] = "home-problems", ["empty"] = "home-empty", ["empty-narrow"] = "home-empty-narrow",
        ["list-foreign"] = "home-foreign", ["drop-hover-list"] = "home-drop-hover",
    };

    /// <summary>The editor states are tall (the whole form and its preview are shown, not a scroll position).</summary>
    const int EditorHeight = 1480;

    /// <summary>The drift states carry a notice above the form.</summary>
    const int DriftHeight = 1640;

    /// <summary>The clock of the Thanksgiving preview: the day before 26 November 2026.</summary>
    static readonly DateTimeOffset BeforeThanksgiving = new(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(-5));

    /// <summary>Runs the render command on a fresh WPF application with a dispatcher; returns the process exit code (0 done, 1 failed, 2 wrong command line).</summary>
    public static int Run(string target, string? outPath, int width, int height)
    {
        if (Aliases.TryGetValue(target, out string? aliased)) target = aliased;
        string? state = States.FirstOrDefault(s => string.Equals(s, target, StringComparison.OrdinalIgnoreCase));
        if (state is not null && string.IsNullOrWhiteSpace(outPath))
        {
            ConsoleOut.Write("ERROR: Cadence.Render " + state + " needs --out <file.png>.\r\n");
            return 2;
        }
        if (state is null && (target.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || outPath is not null))
        {
            ConsoleOut.Write("ERROR: '" + target + "' is not a render state (" + string.Join(", ", States) + "). To render every state, give a folder: Cadence.Render <folder>.\r\n");
            return 2;
        }

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 0;
        var log = new StringBuilder();

        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (state is not null)
                {
                    string file = Path.GetFullPath(outPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await RenderOne(state, file, width, height);
                    log.Append(state).Append(" -> ").Append(file).Append("\r\n");
                }
                else
                {
                    string dir = Path.GetFullPath(target);
                    Directory.CreateDirectory(dir);
                    foreach (string s in States)
                    {
                        string file = Path.Combine(dir, s + ".png");
                        await RenderOne(s, file, width, height);
                        log.Append(s).Append(" -> ").Append(file).Append("\r\n");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                log.Append("ERROR: render failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("\r\n");
                rc = 1;
            }
            finally
            {
                app.Shutdown(rc);
            }
        });
        app.Run();
        ConsoleOut.Write(log.ToString());
        return rc;
    }

    /// <summary>Builds one state, lays it out at the given size (dialogs use their own width and natural height) and writes the PNG.</summary>
    static async Task RenderOne(string state, string file, int width, int height)
    {
        (FrameworkElement root, int w, int h) = await Build(state, width, height);
        await SavePng(root, w, h, file);
        // The one state that proves the table's UI Automation (names, Invoke, the Updated line): a failure makes the render fail.
        if (state == "home") CheckAutomation(root);
    }

    /// <summary>
    /// The owner-run UI scripts find a schedule by name and Invoke it, and read the "UpdatedLine" cell: check that the table's peers offer that
    /// (the render is laid out off-screen, but peers do not need a window).
    /// </summary>
    static void CheckAutomation(FrameworkElement root)
    {
        ScheduleGrid? table = Descendants(root).OfType<ScheduleGrid>().FirstOrDefault(g => g.Name == "Table");
        if (table is null) throw new InvalidOperationException("automation check: the schedule table was not found");
        System.Windows.Automation.Peers.AutomationPeer peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(table);
        System.Windows.Automation.Peers.AutomationPeer? row = peer.GetChildren().FirstOrDefault(c => c.GetName() == "Sync notes");
        if (row is null) throw new InvalidOperationException("automation check: no row named 'Sync notes' (" + string.Join(", ", peer.GetChildren().Select(c => c.GetName())) + ")");
        if (row.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is not System.Windows.Automation.Provider.IInvokeProvider)
            throw new InvalidOperationException("automation check: a schedule row has no Invoke pattern");
        if (peer.GetChildren().Any(c => c.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is null))
            throw new InvalidOperationException("automation check: a row without Invoke");
        System.Windows.Automation.Peers.AutomationPeer? updated = Flatten(row).FirstOrDefault(p => p.GetAutomationId() == "UpdatedLine");
        if (updated is null || !updated.GetName().StartsWith("Updated ", StringComparison.Ordinal))
            throw new InvalidOperationException("automation check: no UpdatedLine in the row (" + updated?.GetName() + ")");
        System.Windows.Automation.Peers.AutomationPeer? pill = Flatten(UIElementPeer(root)).FirstOrDefault(p => p.GetAutomationId() == "AgentsPill");
        if (pill is null || pill.GetName().Length == 0) throw new InvalidOperationException("automation check: the agents switch (AgentsPill) is missing from the window");
    }

    static System.Windows.Automation.Peers.AutomationPeer UIElementPeer(FrameworkElement e) => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(e);

    static IEnumerable<System.Windows.Automation.Peers.AutomationPeer> Flatten(System.Windows.Automation.Peers.AutomationPeer peer)
    {
        foreach (System.Windows.Automation.Peers.AutomationPeer child in peer.GetChildren() ?? [])
        {
            yield return child;
            foreach (System.Windows.Automation.Peers.AutomationPeer deeper in Flatten(child)) yield return deeper;
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject deeper in Descendants(child)) yield return deeper;
        }
    }

    static async Task<(FrameworkElement Root, int Width, int Height)> Build(string state, int width, int height)
    {
        switch (state)
        {
            case "home":
                return (await Home(DemoSet.Normal, null), width, height);
            case "home-states":
                return (await Home(DemoSet.Showcase, null), width, Math.Max(height, 1100));
            case "home-problems":
                return (await Home(DemoSet.WithProblems, null), width, height);
            case "home-empty":
                return (await Home(DemoSet.Empty, null), width, height);
            case "home-empty-narrow":
                return (await Home(DemoSet.Empty, null), 760, 600);
            case "home-loading":
            {
                // The first read has not finished: Start() is never called.
                var vm = new MainViewModel(new DemoServices(DemoSet.Normal), new NoDialogs(), new NoDialogs());
                return (new MainView { DataContext = vm }, width, height);
            }
            case "home-narrow":
                return (await Home(DemoSet.Normal, null), 900, 700);
            case "home-sorted-name":
                return (await Home(DemoSet.Normal, vm => vm.List.SortBy(GridColumn.Name)), width, height);
            case "home-filtered":
                return (await Home(DemoSet.Normal, vm => vm.List.FilterText = "backup"), width, height);
            case "home-selection":
                return (await Home(DemoSet.Normal, vm => vm.List.Select(["Nightly backup", "Photo backup", "Sync notes"])), width, height);
            case "home-foreign":
                return (await HomeAsync(DemoSet.WithProblems, async vm =>
                {
                    vm.ShowForeign = true;
                    await vm.List.RefreshAsync(refreshOthers: true);
                    for (int i = 0; i < 100 && vm.List.ForeignRatesPending; i++) await Task.Delay(20);   // the rates are counted in the background
                }), width, Math.Max(height, 1200));
            case "home-drop-hover":
                return (await Home(DemoSet.Normal, vm => vm.SetDropHover(true)), width, height);
            case "template-sheet":
                return (await Home(DemoSet.Normal, vm => vm.NewCommand.Execute(null)), width, height);
            case "template-picked":
                return (await NewEditor("good-morning", null, null, DemoSet.Empty), width, Math.Max(height, EditorHeight));
            case "editor-simple":
                return (await EditorFor("Stock ingest (Dad)", null), width, Math.Max(height, EditorHeight));
            case "editor-cron":
                return (await EditorFor("Stock ingest (Dad)", e => e.Schedule.IsCronMode = true), width, Math.Max(height, EditorHeight));
            case "editor-invalid-cron":
                return (await NewEditor("blank", e => { e.Program.UseFile(@"C:\Tools\backup.exe"); e.Schedule.IsCronMode = true; e.Schedule.CronText = "0 9 15W * *"; }, null), width, Math.Max(height, EditorHeight));
            case "editor-alternative":
                return (await NewEditor("blank", e => { e.Program.UseFile(@"C:\Tools\backup.exe"); e.Schedule.IsCronMode = true; e.Schedule.CronText = "@reboot"; }, null), width, Math.Max(height, EditorHeight));
            case "editor-runs":
                return (await EditorFor("Nightly photo sync", e => { e.Runs.RunRows[1].ToggleCommand.Execute(null); }, DemoSet.RunsShowcase), width, Math.Max(height, EditorHeight));
            case "editor-advanced":
                return (await EditorFor("YouTube feed download", e =>
                {
                    e.Program.IsAdvancedOpen = true;
                    e.Program.SelectedTimeout = ProgramSectionViewModel.TimeoutChoices[3];
                    e.Program.AddEnvCommand.Execute(null);
                    e.Program.Env[0].Name = "PYTHONIOENCODING";
                    e.Program.Env[0].Value = "utf-8";
                    e.Program.AddEnvCommand.Execute(null);
                    e.Program.Env[1].Name = "YT_COOKIES";
                    e.Program.Env[1].Value = @"C:\Users\tester\cookies.txt";
                    e.Program.Utf8Output = true;
                }), width, Math.Max(height, 1700));
            case "editor-feb29":
                return (await NewEditor("blank", e => { e.Program.UseFile(@"C:\Tools\leapday.exe"); e.Schedule.IsCronMode = true; e.Schedule.CronText = "0 0 29 2 *"; e.Name = "Leap day job"; }, null), width, Math.Max(height, EditorHeight));
            case "editor-skipped-preview":
                return (await NewEditor("weekdays-6pm", e => { e.Program.UseFile(@"C:\Users\Dad\stocks\ingest.exe"); e.Schedule.IsCronMode = true; }, BeforeThanksgiving), width, Math.Max(height, EditorHeight));
            case "editor-narrow":
                return (await EditorFor("Stock ingest (Dad)", null), 900, 2300);
            case "drop-hover":
                return (await NewEditor("blank", e => e.Program.IsDropHover = true, null), width, Math.Max(height, EditorHeight));
            case "drop-picker":
                return (await DroppedEditor(@"C:\Users\Dad\Desktop\Stock ingestion.lnk", null, null), width, height);
            case "drop-shortcut":
                return (await DroppedEditor(@"C:\Users\Dad\Desktop\Stock ingestion.lnk", "blank", null), width, Math.Max(height, EditorHeight));
            case "drop-script":
                return (await DroppedEditor(@"C:\Users\Dad\stocks\ingest_stocks.py", "weekdays-6pm", null), width, Math.Max(height, EditorHeight));
            case "drop-batch":
                return (await DroppedEditor(@"C:\Users\tester\Desktop\Tidy downloads.lnk", "blank", e => e.Program.IsAdvancedOpen = true), width, Math.Max(height, 1700));
            case "drop-rejected":
                return (await NewEditor("blank", e => e.Program.UseFile(@"C:\Users\tester\Documents\notes.txt"), null), width, Math.Max(height, EditorHeight));
            case "drift-banner":
                return (await EditorForTask("Weekly report", null, DemoSet.Showcase), width, Math.Max(height, DriftHeight));
            case "drift-different-user":
                return (await EditorForTask("Month-end export", null, DemoSet.Showcase), width, Math.Max(height, DriftHeight));
            case "outdated":
                return (await EditorForTask("Archive photos", null, DemoSet.Showcase), width, Math.Max(height, EditorHeight));
            case "duplicate-id":
                return (await EditorForTask("Stock ingest (Dad) (copy)", null, DemoSet.Showcase), width, Math.Max(height, DriftHeight));
            case "run-in-progress":
                return (await EditorForTask("Good morning", e => e.Runs.RunNowAsync().GetAwaiter().GetResult(), DemoSet.Showcase), width, Math.Max(height, EditorHeight));
            case "blocked-run":
                return (await EditorForTask("Disk cleanup", e => e.Runs.RunRows[0].ToggleCommand.Execute(null), DemoSet.Showcase), width, Math.Max(height, EditorHeight));
            case "editor-menu":
                return (await EditorForTask("Stock ingest (Dad)", e => e.IsMenuOpen = true, DemoSet.Showcase), width, Math.Max(height, 900));
            case "newer-version":
                return (new ConfirmPanel(DriftText.NewerTitle, DriftText.NewerMessage, "Open in Task Scheduler", danger: false, cancelText: "Close"), 520, -1);
            case "corrupt":
                return (new ConfirmPanel(DriftText.CorruptTitle, DriftText.CorruptMessage, "Delete", danger: true), 520, -1);
            case "confirm-untrack":
            {
                ConfirmText ask = DriftText.UntrackConfirm(await TaskOf("Stock ingest (Dad)"));
                return (new ConfirmPanel(ask.Title, ask.Message, ask.Confirm, danger: true), 520, -1);
            }
            case "confirm-reapply":
            {
                ConfirmText ask = DriftText.ReapplyConfirm(await TaskOf("Weekly report"));
                return (new ConfirmPanel(ask.Title, ask.Message, ask.Confirm, danger: false), 520, -1);
            }
            case "foreign-detail":
            {
                var services = new DemoServices(DemoSet.Normal);
                IReadOnlyList<ClassifiedTask> others = await services.LoadOthersAsync(false);
                ClassifiedTask defrag = others.First(t => t.Observed.Name == "ScheduledDefrag");
                return (new ForeignDetailPanel(ForeignDetail.From(defrag, services.Now, services.Zone)), 520, -1);
            }
            case "unsaved-prompt":
                return (new ConfirmPanel("Discard changes?", "You have changes that are not saved. Closing Cadence will discard them.", "Discard and close", danger: true, cancelText: "Keep editing"), 520, -1);
            case "about":
                return (new AboutPanel(), 520, -1);
            case "confirm-delete":
                return (new ConfirmPanel("Delete \"Stock ingest (Dad)\"?",
                    "This removes the schedule from Task Scheduler and deletes its run history. The program itself is not touched.", "Delete", danger: true), 520, -1);
            case "theme":
                return (new ThemeGallery(), 1040, 790);

            // ---- [P23] the agent surface ----
            case "agents-allowed":
                return (await Home(DemoSet.Normal, null), width, height);
            case "agents-suspended-by-owner":
                return (await Home(DemoSet.Normal, vm => vm.ShowTooltipPreview = true, SuspendedBy("app", "app", null)), width, height);
            case "agents-suspended-by-agent":
                return (await Home(DemoSet.Normal, vm => vm.ShowTooltipPreview = true, SuspendedBy("agent:Claude Code", "agent:Claude Code", "Finished the clean-up; pausing until you have looked at it.")), width, height);
            case "agents-resumed-by-agent":
                return (await Home(DemoSet.Normal, null, new AgentPolicy(false, At(8, 12), "agent:Claude Code", 5, 20, null, ChangedBy: "agent:Claude Code", ChangedAt: At(8, 47),
                    ChangedReason: "Review is done, carrying on with the schedules.")), width, height);
            case "agents-resumed-outside":
                return (await Home(DemoSet.Normal, null, new AgentPolicy(false, At(8, 12), "app", 5, 20, null, ChangedBy: "app", ChangedAt: At(8, 12))), width, height);
            case "agents-unreadable":
                return (await Home(DemoSet.Normal, vm => vm.ShowTooltipPreview = true, AgentPolicy.Unreadable("the registry key Software\\Cadence\\Agents could not be read (SecurityException)")), width, height);
            case "list-updated-by":
                return (await Home(DemoSet.Normal, null), width, Math.Max(height, 900));
            case "editor-agent-lock":
                return (await EditorFor("Photo backup", e => e.IsMenuOpen = true), width, Math.Max(height, 900));
            case "stale-save":
            {
                var services = new DemoServices(DemoSet.Normal);
                ManagedSnapshot snapshot = await services.LoadManagedAsync();
                ManagedEntry entry = snapshot.Entries.First(e => e.Task.Job?.Name == "Sync notes");
                var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
                var view = new MainView { DataContext = vm };
                vm.Start();
                await vm.InitialLoad;
                EditorViewModel editor = EditorViewModel.ForEntry(services, new NoDialogs(), vm.EditorHostForRender(), entry);
                await editor.RefreshPlanAsync();
                vm.ShowEditor(editor);
                _ = vm.ShowChoiceAsync(AgentText.StaleTitle, AgentText.StaleMessage(entry.Task, services.Now, services.Zone) + "\n\n" + AgentText.StaleExplanation,
                    [new PanelChoice(AgentText.KeepEditingButton), new PanelChoice(AgentText.ReloadButton, IsPrimary: true)]);
                return (view, width, Math.Max(height, 900));
            }
            case "agent-activity":
                return (await Home(DemoSet.Normal, vm => vm.OpenActivityAsync().GetAwaiter().GetResult(), null, DemoServices.SampleLog), width, Math.Max(height, 760));
            case "agent-limits":
                return (await Home(DemoSet.Normal, vm => vm.OpenLimitsCommand.Execute(null)), width, height);
            case "toggle-panel":
                return (await Home(DemoSet.Normal, vm => { _ = vm.ToggleAgentsAsync(); }, SuspendedBy("app", "app", null)), width, height);
            default:
                throw new ArgumentException("Unknown render state '" + state + "'.");
        }
    }

    /// <summary>The editor of a new job from a template, shown in the window; <paramref name="arrange"/> fills in the form and <paramref name="now"/> overrides the clock.</summary>
    static async Task<FrameworkElement> NewEditor(string templateId, Action<EditorViewModel>? arrange, DateTimeOffset? now, DemoSet set = DemoSet.Normal)
    {
        var services = new DemoServices(set);
        var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
        var view = new MainView { DataContext = vm };
        vm.Start();
        await vm.InitialLoad;
        EditorViewModel editor = EditorViewModel.ForTemplate(services, new NoDialogs(), vm.EditorHostForRender(), Cadence.Core.Text.Templates.All.First(t => t.Id == templateId), now);
        arrange?.Invoke(editor);
        await editor.RefreshPlanAsync();
        vm.ShowEditor(editor);
        return view;
    }

    /// <summary>
    /// The real drop flow ([CP-C]): a file dropped on the list opens a draft for it straight away ("blank": shown as it comes out). With no
    /// template the "Use a preset…" sheet is shown over the draft; with another template that preset is picked from the sheet.
    /// </summary>
    static async Task<FrameworkElement> DroppedEditor(string path, string? templateId, Action<EditorViewModel>? arrange)
    {
        var services = new DemoServices(DemoSet.Empty);
        var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
        var view = new MainView { DataContext = vm };
        vm.Start();
        await vm.InitialLoad;
        await vm.HandleDropAsync([path]);
        var editor = (EditorViewModel)vm.CurrentPage;
        if (templateId is null)
        {
            await editor.RefreshPlanAsync();
            editor.Schedule.PickPresetCommand.Execute(null);   // the sheet "When should X run?" over the draft
            return view;
        }
        if (templateId != "blank")
        {
            editor.Schedule.PickPresetCommand.Execute(null);
            vm.PickTemplateById(templateId);
        }
        arrange?.Invoke(editor);
        await editor.RefreshPlanAsync();
        return view;
    }

    /// <summary>The editor of the demo schedule whose TASK name is <paramref name="taskName"/> (a copy shares its original's job name).</summary>
    static async Task<FrameworkElement> EditorForTask(string taskName, Action<EditorViewModel>? arrange, DemoSet set)
    {
        var services = new DemoServices(set);
        ManagedSnapshot snapshot = await services.LoadManagedAsync();
        ManagedEntry entry = snapshot.Entries.First(e => e.Task.Observed.Name == taskName);
        return await EditorForEntry(entry, arrange, services);
    }

    static async Task<ClassifiedTask> TaskOf(string name)
    {
        ManagedSnapshot snapshot = await new DemoServices(DemoSet.Showcase).LoadManagedAsync();
        return snapshot.Entries.First(e => e.Task.Observed.Name == name).Task;
    }

    static async Task<FrameworkElement> EditorFor(string jobName, Action<EditorViewModel>? arrange, DemoSet set = DemoSet.Normal)
    {
        var services = new DemoServices(set);
        ManagedSnapshot snapshot = await services.LoadManagedAsync();
        ManagedEntry entry = snapshot.Entries.First(e => e.Task.Job?.Name == jobName);
        return await EditorForEntry(entry, arrange, services);
    }

    static async Task<FrameworkElement> EditorForEntry(ManagedEntry entry, Action<EditorViewModel>? arrange, DemoServices? services = null)
    {
        services ??= new DemoServices(DemoSet.Normal);
        var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
        var view = new MainView { DataContext = vm };
        vm.Start();
        await vm.InitialLoad;
        EditorViewModel editor = EditorViewModel.ForEntry(services, new NoDialogs(), vm.EditorHostForRender(), entry);
        arrange?.Invoke(editor);
        await editor.RefreshPlanAsync();
        vm.ShowEditor(editor);
        return view;
    }

    static DateTimeOffset At(int hour, int minute) => new(2026, 11, 27, hour, minute, 0, TimeSpan.FromHours(-5));

    /// <summary>A switch that is on: who turned it on (the token of 12.4.1), when (this morning) and the optional reason an agent gave.</summary>
    static AgentPolicy SuspendedBy(string suspendedBy, string changedBy, string? reason) =>
        new(true, At(8, 12), suspendedBy, 5, 20, null, ChangedBy: changedBy, ChangedAt: At(8, 12), ChangedReason: reason);

    static async Task<FrameworkElement> HomeAsync(DemoSet set, Func<MainViewModel, Task> arrange)
    {
        var services = new DemoServices(set);
        var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
        var view = new MainView { DataContext = vm };
        vm.Start();
        await vm.InitialLoad;
        await arrange(vm);
        return view;
    }

    static async Task<FrameworkElement> Home(DemoSet set, Action<MainViewModel>? arrange, AgentPolicy? policy = null, IReadOnlyList<AuditEntry>? log = null)
    {
        var services = new DemoServices(set, policy, log);
        var vm = new MainViewModel(services, new NoDialogs(), new NoDialogs());
        var view = new MainView { DataContext = vm };
        vm.Start();
        await vm.InitialLoad;
        arrange?.Invoke(vm);
        return view;
    }

    static async Task SavePng(FrameworkElement root, int width, int height, string path)
    {
        var host = new Border { Background = (Brush)Application.Current.FindResource("BgBrush"), Child = root, SnapsToDevicePixels = true, UseLayoutRounding = true };
        if (height < 0)
        {
            host.Measure(new Size(width, double.PositiveInfinity));
            height = (int)Math.Ceiling(host.DesiredSize.Height);
        }
        host.Width = width;
        host.Height = height;
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        // Let bindings, item containers and templates settle, then lay out once more before drawing.
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
