using Cadence.Scheduler;
using Cadence.Scheduler.Com;

namespace Cadence.IntegrationTests;

/// <summary>
/// The ONLY place integration tests may create or delete tasks: the throwaway folder \CadenceTest\ (and its subfolders).
/// The folder name is a constant on purpose. Every delete walks down from \CadenceTest and refuses any path outside it.
/// <see cref="Cleanup"/> runs before the tests, after them, and from process-exit / Ctrl+C hooks; tools\clean-test-tasks.ps1 does the same standalone.
/// </summary>
public static class TestFolder
{
    public const string Name = "CadenceTest";
    public const string Path = "\\CadenceTest";

    /// <summary>The agent-policy key of the test folder under HKCU (PLAN 12.4.2); Cleanup deletes exactly this key.</summary>
    public const string AgentsKey = @"Software\CadenceTest\Agents";

    public static SchedulerOptions Options { get; } = new(Name, Name);

    public static TaskSchedulerGateway NewGateway() => new(Options);

    static string LocalDataDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>Deletes every task (stopping running ones first), every subfolder, the folder itself and %LOCALAPPDATA%\CadenceTest. Throws if anything remains.</summary>
    public static void Cleanup()
    {
        using (TaskServiceSession s = TaskServiceSession.Open())
        {
            object? folder = s.TryGetFolder(Path);
            if (folder is not null)
            {
                try { DeleteContents(folder, Path); }
                finally { ComRelease.Release(folder); }

                object? root = s.TryGetFolder("\\");
                try
                {
                    dynamic r = root!;
                    r.DeleteFolder(Name, 0);
                }
                finally { ComRelease.Release(root); }
            }
        }

        if (Directory.Exists(LocalDataDir))
            Directory.Delete(LocalDataDir, recursive: true);

        // [AG-P17] The agent policy of the test folder. Hard-coded: this is the only registry key tests may delete, and it is never HKCU\Software\Cadence.
        DeleteAgentsKey();

        if (Exists())
            throw new InvalidOperationException("\\" + Name + " still exists after cleanup.");
    }

    /// <summary>
    /// [AG-P20] Deletes only the agent state of the test folder: <c>HKCU\Software\CadenceTest\Agents</c> (and <c>HKCU\Software\CadenceTest</c> itself when nothing else is in it)
    /// and <c>%LOCALAPPDATA%\CadenceTestgents</c> (the audit log). Never touches tasks and never the product's key or folder (names are hard-coded).
    /// </summary>
    public static void CleanAgentState()
    {
        DeleteAgentsKey();
        string agents = System.IO.Path.Combine(LocalDataDir, "agents");
        if (Directory.Exists(agents)) Directory.Delete(agents, recursive: true);
        if (Directory.Exists(LocalDataDir) && !Directory.EnumerateFileSystemEntries(LocalDataDir).Any()) Directory.Delete(LocalDataDir);
    }

    static void DeleteAgentsKey()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(AgentsKey, throwOnMissingSubKey: false);
        // The parent key of the test folder is removed too, but only when it is empty (the app's window settings would live there).
        using (Microsoft.Win32.RegistryKey? parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CadenceTest"))
        {
            if (parent is null || parent.SubKeyCount != 0 || parent.ValueCount != 0) return;
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\CadenceTest", throwOnMissingSubKey: false);
    }

    /// <summary>Cleanup that never throws (for exit hooks). Returns false when something remains.</summary>
    public static bool TryCleanup()
    {
        try
        {
            Cleanup();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cleanup of \\" + Name + " failed: " + ex.Message);
            return false;
        }
    }

    public static bool Exists()
    {
        using TaskServiceSession s = TaskServiceSession.Open();
        object? folder = s.TryGetFolder(Path);
        ComRelease.Release(folder);
        return folder is not null;
    }

    public static void InstallExitHooks()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCleanup();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TryCleanup();
        Console.CancelKeyPress += (_, _) => TryCleanup();
    }

    /// <summary>Names (full paths) of every task outside \Cadence\ and \CadenceTest\, for the before/after comparison.</summary>
    public static SortedSet<string> SnapshotOtherTasks() =>
        new(NewGateway().ListOtherTasks().Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="action"/> with the raw COM task <c>\CadenceTest\name</c> (for what the gateway deliberately does not expose). Releases it afterwards.</summary>
    public static T WithRawTask<T>(string taskName, Func<dynamic, T> action)
    {
        if (taskName.Contains('\\', StringComparison.Ordinal) || taskName.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException("A task name, not a path.", nameof(taskName));
        using TaskServiceSession s = TaskServiceSession.Open();
        object? folder = s.TryGetFolder(Path) ?? throw new InvalidOperationException("\\" + Name + " does not exist.");
        object? task = null;
        try
        {
            dynamic f = folder;
            task = (object)f.GetTask(taskName);
            return action(task);
        }
        finally
        {
            ComRelease.Release(task);
            ComRelease.Release(folder);
        }
    }

    /// <summary>SDDL (owner, group, DACL) of \CadenceTest itself.</summary>
    public static string FolderSddl()
    {
        using TaskServiceSession s = TaskServiceSession.Open();
        object? folder = s.TryGetFolder(Path) ?? throw new InvalidOperationException("\\" + Name + " does not exist.");
        try
        {
            dynamic f = folder;
            return (string)f.GetSecurityDescriptor(0xF);
        }
        finally { ComRelease.Release(folder); }
    }

    static void DeleteContents(object folder, string path)
    {
        Guard(path);
        dynamic f = folder;

        object? tasks = (object)f.GetTasks(1);   // 1 = include hidden tasks
        try
        {
            dynamic c = tasks;
            int n = (int)c.Count;
            var names = new List<string>();
            for (int i = 1; i <= n; i++)
            {
                object? t = null;
                try
                {
                    t = (object)c.Item(i);
                    dynamic task = t;
                    names.Add((string)task.Name);
                    try { if ((int)task.State == 4) task.Stop(0); }
                    catch (Exception e) when (SchedulerException.IsComFailure(e)) { /* already finished */ }
                }
                finally { ComRelease.Release(t); }
            }
            foreach (string name in names) f.DeleteTask(name, 0);
        }
        finally { ComRelease.Release(tasks); }

        object? subs = (object)f.GetFolders(0);
        try
        {
            dynamic sf = subs;
            int n = (int)sf.Count;
            var children = new List<string>();
            for (int i = 1; i <= n; i++)
            {
                object? child = null;
                try
                {
                    child = (object)sf.Item(i);
                    dynamic cf = child;
                    string childName = (string)cf.Name;
                    children.Add(childName);
                    DeleteContents(child, path + "\\" + childName);
                }
                finally { ComRelease.Release(child); }
            }
            foreach (string childName in children) f.DeleteFolder(childName, 0);
        }
        finally { ComRelease.Release(subs); }
    }

    static void Guard(string path)
    {
        if (!(path.Equals(Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Refusing to touch '" + path + "': it is outside " + Path + ".");
    }
}
