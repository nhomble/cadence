using System.Diagnostics;
using System.Text;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.Cli;
using Cadence.App.Interop;
using Cadence.App.UI;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.App;

/// <summary>
/// The entry point. Command-line switches (PLAN 7.6) are handled here, before any WPF startup, and print through
/// <see cref="ConsoleOut"/> because the exe is a GUI-subsystem program. No arguments opens the window (single instance).
/// Exit codes: 0 done, 1 the operation failed, 2 the command line is wrong.
/// </summary>
public static class Program
{
    const string MutexName = @"Local\CadenceApp";

    [STAThread]
    public static int Main(string[] args)
    {
        // --mcp is dispatched first: no mutex, no WPF, no ConsoleOut/AttachConsole (stdout is the protocol channel, PLAN 12.1.2).
        bool mcp = args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--mcp=", StringComparison.OrdinalIgnoreCase));
        CliParseResult parsed = CliParser.Parse(args);
        if (mcp)
        {
            if (parsed.Command is { Action: CliAction.Mcp } mcpCmd) return Cadence.Agent.McpHost.Run(mcpCmd.Folder);
            // Any usage error goes to stderr, never stdout.
            Console.Error.Write("ERROR: " + (parsed.Error ?? "--mcp cannot be combined with other switches.") + "\r\nRun Cadence.exe --help for the usage.\r\n");
            return 2;
        }
        if (parsed.Command is not { } cmd)
        {
            ConsoleOut.Write("ERROR: " + parsed.Error + "\r\nRun Cadence.exe --help for the usage.\r\n");
            return 2;
        }

        switch (cmd.Action)
        {
            case CliAction.Gui:
                return RunWindow(measureStartup: false);
            case CliAction.MeasureStartup:
                return RunWindow(measureStartup: true);
            case CliAction.Help:
                ConsoleOut.Write(CliParser.Usage());
                return 0;
            default:
                return RunHeadless(cmd);
        }
    }

    static int RunHeadless(CliCommand cmd)
    {
        var output = new StringBuilder();
        int rc;
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (cmd.Action == CliAction.SelfTest)
            {
                // The default (real) folder, read-only: it only connects and lists.
                var gw = new TaskSchedulerGateway();
                string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                rc = SelfTest.Run(gw, local, root, t => output.Append(t));
            }
            else
            {
                var gw = new TaskSchedulerGateway(new SchedulerOptions(cmd.Folder, cmd.Folder));
                var env = new CliEnv(gw, new RunRecordStore(RunRecordStore.RootFor(cmd.Folder)), () => DateTimeOffset.Now, TimeZoneInfo.Local, t => output.Append(t));
                if (cmd.Action == CliAction.Probe)
                {
                    env.Raw(Probe.Build(env, cmd.All, AppVersion.Display()) + "\r\n");
                    rc = 0;
                }
                else if (cmd.Action == CliAction.Agents)
                {
                    // The owner's side of the agent controls: the only place besides the app that holds the policy STORE (the MCP host never does).
                    var agents = new AgentsEnv(env, new RegistryAgentPolicyStore(cmd.Folder), new FileAuditLog(cmd.Folder), () => ProcessAncestry.Describe());
                    rc = AgentsCommands.Execute(cmd, agents);
                }
                else rc = JobCommands.Execute(cmd, env);
            }
        }
        catch (SchedulerException ex)
        {
            output.Append("ERROR: Task Scheduler: ").Append(ex.Message).Append("\r\n");
            rc = 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            output.Append("ERROR: unexpected ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("\r\n");
            rc = 1;
        }
        ConsoleOut.Write(output.ToString());
        return rc;
    }

    static int RunWindow(bool measureStartup)
    {
        string? bad = FolderPolicy.FromEnvironment(Environment.GetEnvironmentVariable("CADENCE_FOLDER"), out string folder);
        if (bad is not null)
        {
            ConsoleOut.Write("ERROR: " + bad + "\r\n");
            return 2;
        }

        // One window per user session (and per dev folder, so a CadenceTest run never hands over to the real app). A second start asks
        // the first to come to the front and exits. --measure-startup is a dev switch and bypasses it.
        Mutex? mutex = null;
        if (!measureStartup)
        {
            string name = folder == FolderPolicy.Product ? MutexName : MutexName + "." + folder;
            mutex = new Mutex(true, name, out bool first);
            if (!first)
            {
                mutex.Dispose();
                ShowMe.Broadcast();
                return 0;
            }
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            // No Task Scheduler, registry or disk access happens in these constructors: the first read starts after the first frame.
            IAppServices services = new RealAppServices(folder);
            var dialogs = new WpfDialogs();
            var window = new MainWindow(new MainViewModel(services, dialogs, dialogs));
            // Test hook: for the dev folder only, CADENCE_TEST_QUIET=1 opens the window far off-screen (-12000,-12000), without activation and without a taskbar button, so automated UI
            // checks (UI Automation still sees every control) never steal focus or flash on the owner's screen. Ignored for the real folder.
            if (!measureStartup && folder == Cadence.Scheduler.ManagedFolder.Test && Environment.GetEnvironmentVariable("CADENCE_TEST_QUIET") == "1")
            {
                window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                window.WindowState = System.Windows.WindowState.Normal;
                // Not -32000: that is the position Windows gives minimized windows, and it is not honoured as a normal position.
                window.Left = -12000;
                window.Top = -12000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.QuietTestMode = true;
            }
            if (measureStartup)
            {
                window.FirstFrame += () =>
                {
                    double ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                    ConsoleOut.Write(((int)Math.Round(ms)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms to the first frame\r\n");
                    app.Shutdown(0);
                };
            }
            return app.Run(window);
        }
        finally
        {
            mutex?.Dispose();
        }
    }
}
