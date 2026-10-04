using System.Security.AccessControl;
using System.Security.Principal;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>PLAN 6.3 #1 plus the CP-A items: gateway CRUD, error mapping, hidden tasks, run/stop, listing, user identity, security descriptors, threads.</summary>
public static class GatewayTests
{
    public static void Crud()
    {
        var gw = Fx.Gateway();
        T.Eq("folder path", "\\CadenceTest", gw.FolderPath);
        T.Eq("no tasks yet", 0, gw.ListManagedFolder().Count);
        T.True("Get on a missing task is null", gw.Get("crud one") is null);
        gw.Delete("crud one");   // no-op, even when the folder does not exist
        T.True("Delete of a missing task is a no-op", true);

        RegisteredTaskInfo info = gw.Register("crud one", Fx.Xml(Fx.FarTrigger));
        T.Eq("name", "crud one", info.Name);
        T.Eq("path", "\\CadenceTest\\crud one", info.Path);
        T.Eq("disabled state", TaskState.Disabled, info.State);
        T.Eq("not enabled", false, info.Enabled);
        T.Eq("never ran: LastRunTime null", (DateTime?)null, info.LastRunTime);
        T.Eq("never ran: LastTaskResult null", (int?)null, info.LastTaskResult);
        T.Eq("NextRunTime works for a disabled task", new DateTime(2099, 1, 1, 0, 0, 0), info.NextRunTime);
        T.Eq("no missed runs", 0, info.NumberOfMissedRuns);
        T.True("stored XML is normalized (carries a URI)", info.Xml.Contains("<URI>\\CadenceTest\\crud one</URI>", StringComparison.Ordinal));

        T.Eq("Get returns the same snapshot", info.Xml, gw.Get("crud one")?.Xml);
        T.Eq("listed once", 1, gw.ListManagedFolder().Count(t => t.Name == "crud one"));

        RegisteredTaskInfo updated = gw.Register("crud one", Fx.Xml(Fx.FarTrigger, args: "/c echo test"));
        T.True("CREATE_OR_UPDATE changed the action", updated.Xml.Contains("/c echo test", StringComparison.Ordinal));
        T.Eq("still one task", 1, gw.ListManagedFolder().Count);

        gw.Delete("crud one");
        T.True("deleted", gw.Get("crud one") is null);
        gw.Delete("crud one");
        T.True("second Delete is a no-op", true);

        gw.DeleteFolderIfEmpty();
        T.Eq("empty folder removed", false, TestFolder.Exists());
        T.Eq("listing a missing folder gives nothing", 0, gw.ListManagedFolder().Count);
        gw.DeleteFolderIfEmpty();
        T.True("DeleteFolderIfEmpty on a missing folder is a no-op", true);

        gw.Register("crud two", Fx.Xml(Fx.FarTrigger));
        T.Eq("Register recreates the folder", true, TestFolder.Exists());
        gw.DeleteFolderIfEmpty();
        T.Eq("a non-empty folder is kept", true, TestFolder.Exists());
        gw.Delete("crud two");
    }

    public static void NamesCannotEscapeTheFolder()
    {
        var gw = Fx.Gateway();
        string[] bad = ["", "  ", ".", "..", "a\\b", "..\\evil", "\\Cadence\\x", "a/b", "x\0y"];
        foreach (string name in bad)
        {
            string shown = name.Replace("\0", "\\0", StringComparison.Ordinal);
            T.Throws<ArgumentException>("Register refuses '" + shown + "'", () => gw.Register(name, Fx.Xml(Fx.FarTrigger)));
            T.Throws<ArgumentException>("Delete refuses '" + shown + "'", () => gw.Delete(name));
            T.Throws<ArgumentException>("Get refuses '" + shown + "'", () => gw.Get(name));
            T.Throws<ArgumentException>("Run refuses '" + shown + "'", () => gw.Run(name));
            T.Throws<ArgumentException>("Stop refuses '" + shown + "'", () => gw.Stop(name));
        }
        T.Throws<ArgumentException>("a folder name with a separator is refused", () => new TaskSchedulerGateway(new SchedulerOptions("A\\B")));
        T.Eq("nothing was created", 0, gw.ListManagedFolder().Count);
    }

    public static void HiddenTasksAreListed()
    {
        var gw = Fx.Gateway();
        gw.Register("hidden one", Fx.Xml(Fx.FarTrigger, hiddenTask: true));
        gw.Register("visible one", Fx.Xml(Fx.FarTrigger));
        string[] names = gw.ListManagedFolder().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        T.Eq("ListManagedFolder includes the hidden task (flag 1)", "hidden one|visible one", string.Join("|", names));

        int plain = TestFolder.WithRawTask("hidden one", _ => 0);
        T.Eq("the hidden task is reachable by name", 0, plain);
    }

    public static void ErrorMapping()
    {
        var gw = Fx.Gateway();

        var ex = Assert<SchedulerException>(() => gw.Register("bad interval",
            Fx.Xml("<TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Repetition><Interval>PT30S</Interval></Repetition></TimeTrigger>")));
        T.Eq("PT30S: kind", SchedulerErrorKind.InvalidXml, ex?.Kind);
        T.True("PT30S: line > 0", ex?.Line > 0, ex?.Message);
        T.True("PT30S: column > 0", ex?.Column > 0, ex?.Message);
        T.Eq("PT30S: element", "Interval", ex?.Element);
        T.Eq("PT30S: value", "PT30S", ex?.Value);
        T.True("PT30S: nothing registered", gw.Get("bad interval") is null);

        var ex2 = Assert<SchedulerException>(() => gw.Register("bad duration",
            Fx.Xml("<TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Repetition><Interval>PT2H</Interval><Duration>PT1H</Duration></Repetition></TimeTrigger>")));
        T.Eq("interval > duration: kind", SchedulerErrorKind.InvalidXml, ex2?.Kind);
        T.Eq("interval > duration: element", "Duration", ex2?.Element);

        var ex3 = Assert<SchedulerException>(() => gw.Register("not xml", "this is not xml"));
        Fx.Info("registering text that is not XML gives kind " + ex3?.Kind + ", hresult 0x" + ((uint)(ex3?.HResultCode ?? 0)).ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ", message '" + ex3?.Message + "'");
        T.Eq("not XML: kind", SchedulerErrorKind.InvalidXml, ex3?.Kind);
        T.Eq("not XML: line", 1, ex3?.Line);

        // A standard user may not register a boot trigger (measured in the spike). If this machine runs the tests elevated, the task is simply created and cleaned up.
        SchedulerException? boot = null;
        try { gw.Register("boot", Fx.Xml("<BootTrigger><Enabled>false</Enabled></BootTrigger>")); }
        catch (SchedulerException e) { boot = e; }
        T.Eq("BootTrigger as a standard user: AccessDenied", SchedulerErrorKind.AccessDenied, boot?.Kind);

        var missingRun = Assert<SchedulerException>(() => gw.Run("does not exist"));
        T.Eq("Run on a missing task: NotFound", SchedulerErrorKind.NotFound, missingRun?.Kind);
        var missingStop = Assert<SchedulerException>(() => gw.Stop("does not exist"));
        T.Eq("Stop on a missing task: NotFound", SchedulerErrorKind.NotFound, missingStop?.Kind);
    }

    public static void RunAndStop()
    {
        var gw = Fx.Gateway();

        // A manual-only task (no trigger) that exits at once: the "run now" check. Nothing is written anywhere.
        gw.Register("run now", Fx.Xml("", enabled: true));
        gw.Run("run now");
        bool ran = Fx.WaitFor(() => gw.Get("run now") is { LastRunTime: not null, State: TaskState.Ready }, TimeSpan.FromSeconds(30));
        RegisteredTaskInfo? after = gw.Get("run now");
        T.True("Run: the task ran and is Ready again", ran, "state " + after?.State + " last " + Fx.Iso(after?.LastRunTime));
        T.Eq("Run: LastTaskResult 0 (cmd /c exit 0)", (int?)0, after?.LastTaskResult);

        // Stop: ping for 30 s, stopped after it is seen Running.
        gw.Register("run stop", Fx.Xml("", enabled: true, command: "%SystemRoot%\\System32\\PING.EXE", args: "-n 30 127.0.0.1"));
        gw.Run("run stop");
        bool running = Fx.WaitFor(() => gw.Get("run stop")?.State == TaskState.Running, TimeSpan.FromSeconds(20));
        T.True("Run: the long task is Running", running);
        gw.Stop("run stop");
        bool stopped = Fx.WaitFor(() => gw.Get("run stop")?.State != TaskState.Running, TimeSpan.FromSeconds(20));
        T.True("Stop: the task is no longer Running", stopped);

        SchedulerException? notRunning = null;
        try { gw.Stop("run now"); }
        catch (SchedulerException e) { notRunning = e; }
        Fx.Info("Stop on a task that is not running: " + (notRunning is null ? "no error" : notRunning.Kind + " 0x" + ((uint)notRunning.HResultCode).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)));
        T.True("Stop on an idle task does not hang or crash", true);
    }

    public static void ListOtherTasks()
    {
        var gw = Fx.Gateway();
        gw.Register("excluded from other", Fx.Xml(Fx.FarTrigger));
        IReadOnlyList<RegisteredTaskInfo> others = gw.ListOtherTasks();
        T.True("the machine has other tasks", others.Count > 0);
        T.Eq("none is in \\CadenceTest", 0, others.Count(t => t.Path.StartsWith("\\CadenceTest\\", StringComparison.OrdinalIgnoreCase)));
        T.Eq("none is in \\Cadence", 0, others.Count(t => t.Path.StartsWith("\\Cadence\\", StringComparison.OrdinalIgnoreCase)));
        T.True("all paths are absolute", others.All(t => t.Path.StartsWith('\\')));
        T.True("name is the last path segment", others.All(t => t.Path.EndsWith("\\" + t.Name, StringComparison.Ordinal)));
        int unreadable = others.Count(t => t.Xml.Length == 0);
        Fx.Info(others.Count + " other tasks listed; " + unreadable + " have no readable XML");
        T.True("most other tasks have readable XML", unreadable * 2 < others.Count);
        T.Eq("max is honoured", 3, gw.ListOtherTasks(3).Count);
        T.Eq("max 0 gives nothing", 0, gw.ListOtherTasks(0).Count);
    }

    /// <summary>[CP-B] TASK_CREATE / TASK_UPDATE / CREATE_OR_UPDATE: Create never overwrites, Update never creates.</summary>
    public static void RegisterModes()
    {
        var gw = Fx.Gateway();
        int others = gw.ListManagedFolder().Count;   // other suites may have left tasks in the folder; only the difference counts
        gw.Register("mode one", Fx.Xml(Fx.FarTrigger), RegisterMode.Create);
        T.True("Create made the task", gw.Get("mode one") is not null);
        string before = gw.Get("mode one")!.Xml;

        SchedulerException? ex = null;
        try { gw.Register("mode one", Fx.Xml(Fx.FarTrigger, args: "/c echo other"), RegisterMode.Create); }
        catch (SchedulerException e) { ex = e; }
        T.Eq("Create on an existing task fails with AlreadyExists", SchedulerErrorKind.AlreadyExists, ex?.Kind ?? SchedulerErrorKind.Other);
        T.Eq("...and the task is byte for byte unchanged", before, gw.Get("mode one")!.Xml);
        T.Eq("...and there is still one task of ours", others + 1, gw.ListManagedFolder().Count);

        RegisteredTaskInfo updated = gw.Register("mode one", Fx.Xml(Fx.FarTrigger, args: "/c echo updated"), RegisterMode.Update);
        T.True("Update changed the existing task", updated.Xml.Contains("/c echo updated", StringComparison.Ordinal));

        ex = null;
        try { gw.Register("mode two", Fx.Xml(Fx.FarTrigger), RegisterMode.Update); }
        catch (SchedulerException e) { ex = e; }
        T.True("Update on a missing task fails", ex is not null, "no exception");
        T.Eq("...with NotFound", SchedulerErrorKind.NotFound, ex?.Kind ?? SchedulerErrorKind.Other);
        T.True("...and creates nothing", gw.Get("mode two") is null);

        gw.Register("mode two", Fx.Xml(Fx.FarTrigger));
        T.True("the default is CreateOrUpdate (creates)", gw.Get("mode two") is not null);
        gw.Register("mode two", Fx.Xml(Fx.FarTrigger, args: "/c echo again"), RegisterMode.CreateOrUpdate);
        T.True("CreateOrUpdate updates", gw.Get("mode two")!.Xml.Contains("/c echo again", StringComparison.Ordinal));
        T.Throws<ArgumentOutOfRangeException>("an undefined mode is refused", () => gw.Register("mode three", Fx.Xml(Fx.FarTrigger), (RegisterMode)3));
        T.True("...and creates nothing", gw.Get("mode three") is null);
    }

    public static void ConnectedUser()
    {
        var gw = Fx.Gateway();
        string id = gw.ConnectedUserId;
        T.True("ConnectedUserId is DOMAIN\\user", id.Contains('\\', StringComparison.Ordinal), id);
        string sid = gw.ConnectedUserSid;
        T.Eq("ConnectedUserSid equals the process user's SID", WindowsIdentity.GetCurrent().User?.Value, sid);
        T.Eq("IsConnectedUser(SID)", true, gw.IsConnectedUser(sid));
        T.Eq("IsConnectedUser(DOMAIN\\user)", true, gw.IsConnectedUser(id));
        T.Eq("IsConnectedUser is case-insensitive for names", true, gw.IsConnectedUser(id.ToUpperInvariant()));
        T.Eq("SYSTEM is another user", false, gw.IsConnectedUser("S-1-5-18"));
        T.Eq("SYSTEM by name is another user", false, gw.IsConnectedUser("NT AUTHORITY\\SYSTEM"));
        T.Eq("an unknown name is false", false, gw.IsConnectedUser("NOSUCHDOMAIN\\nobody-here"));
        T.Eq("garbage is false", false, gw.IsConnectedUser("S-1-garbage"));
        T.Eq("blank is false", false, gw.IsConnectedUser("  "));
        T.Eq("null is false", false, gw.IsConnectedUser(null));
    }

    /// <summary>CP-A step 8. Measured: the folder grants Authenticated Users (AU) a container-inherit write ACE, but a task does not inherit it.</summary>
    public static void SecurityDescriptors()
    {
        var gw = Fx.Gateway();
        gw.Register("sddl probe", Fx.Xml(Fx.FarTrigger));
        string folderSddl = TestFolder.FolderSddl();
        string taskSddl = TestFolder.WithRawTask("sddl probe", t => (string)t.GetSecurityDescriptor(0xF));
        Fx.Info("FOLDER SDDL " + folderSddl);
        Fx.Info("TASK   SDDL " + taskSddl);

        var sd = new RawSecurityDescriptor(taskSddl);
        string me = gw.ConnectedUserSid;
        string[] allowed = [me, "S-1-5-32-544" /* Administrators */, "S-1-5-18" /* SYSTEM */];
        const int writeBits = 0x2 /* FILE_WRITE_DATA */ | 0x4 /* FILE_APPEND_DATA */ | 0x10000 /* DELETE */ | 0x40000 /* WRITE_DAC */ | 0x80000 /* WRITE_OWNER */;
        var offenders = new List<string>();
        foreach (GenericAce ace in sd.DiscretionaryAcl ?? throw new InvalidOperationException("The task has no DACL."))
        {
            if (ace is CommonAce { AceType: AceType.AccessAllowed } c
                && (c.AccessMask & writeBits) != 0
                && !allowed.Contains(c.SecurityIdentifier.Value, StringComparer.OrdinalIgnoreCase))
                offenders.Add(c.SecurityIdentifier.Value + " mask 0x" + c.AccessMask.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }
        T.True("no other principal can write, delete or re-ACL a newly registered task", offenders.Count == 0, string.Join("; ", offenders));
        T.Eq("the task is owned by the connected user", me, sd.Owner?.Value);
    }

    public static void ThreadSafety()
    {
        // The gateway is called from thread-pool threads: every call opens its own session. Eight tasks, in parallel.
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                var gw = Fx.Gateway();
                string name = "thread " + i;
                gw.Register(name, Fx.Xml(Fx.FarTrigger));
                if (gw.Get(name) is null) errors.Add(name + " missing after Register");
                _ = gw.ListManagedFolder();
                gw.Delete(name);
                if (gw.Get(name) is not null) errors.Add(name + " still there after Delete");
            }
            catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        });
        T.True("8 parallel register/get/list/delete cycles", errors.IsEmpty, string.Join("; ", errors));
    }

    /// <summary>
    /// [CP-B] Regression: listing the managed folder while another caller deletes tasks from it must not throw. A task removed between
    /// GetTasks and reading its properties used to surface as NotFound (0x80070002) from ListManagedFolder (flaky ThreadSafety above).
    /// The race is provoked many times rather than reproduced deterministically.
    /// </summary>
    public static void ListingWhileTasksAreDeleted()
    {
        var gw = Fx.Gateway();
        const int n = 40;
        for (int i = 0; i < n; i++) gw.Register("vanish " + i, Fx.Xml(Fx.FarTrigger));
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        int listings = 0;
        using var done = new ManualResetEventSlim(false);
        using var listing = new ManualResetEventSlim(false);
        var deleter = Task.Run(() =>
        {
            try
            {
                listing.Wait(TimeSpan.FromSeconds(10));   // start once the first listing is under way
                for (int i = 0; i < n; i++)
                {
                    Fx.Gateway().Delete("vanish " + i);
                    Thread.Sleep(25);   // spread the deletes over many listings
                }
            }
            finally { done.Set(); }
        });
        while (!done.IsSet)
        {
            listing.Set();
            try
            {
                IReadOnlyList<RegisteredTaskInfo> seen = gw.ListManagedFolder();
                listings++;
                if (seen.Any(t => string.IsNullOrEmpty(t.Name))) errors.Add("a listed task has no name");
            }
            catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        }
        deleter.Wait();
        T.True("listing the managed folder while " + n + " tasks are deleted never throws (" + listings + " listings)", errors.IsEmpty, string.Join("; ", errors.Distinct()));
        T.True("the race was really exercised (at least 2 listings overlapped the deletes)", listings >= 2, listings.ToString(System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("all deleted afterwards", 0, gw.ListManagedFolder().Count(t => t.Name.StartsWith("vanish ", StringComparison.Ordinal)));
    }

    /// <summary>The cleanup guarantee: a subfolder, a hidden task and a running task do not stop \CadenceTest from being removed.</summary>
    public static void CleanupRemovesEverything()
    {
        var gw = Fx.Gateway();
        gw.Register("cleanup hidden", Fx.Xml(Fx.FarTrigger, hiddenTask: true));
        gw.Register("cleanup running", Fx.Xml("", enabled: true, command: "%SystemRoot%\\System32\\PING.EXE", args: "-n 30 127.0.0.1"));
        gw.Run("cleanup running");
        T.True("the task is running before cleanup", Fx.WaitFor(() => gw.Get("cleanup running")?.State == TaskState.Running, TimeSpan.FromSeconds(20)));
        using (var s = Cadence.Scheduler.Com.TaskServiceSession.Open())
        {
            object? folder = s.TryGetFolder(TestFolder.Path);
            object? sub = null, task = null;
            try
            {
                dynamic f = folder!;
                sub = (object)f.CreateFolder("nested", null);
                dynamic sf = sub;
                task = (object)sf.RegisterTask("inner", Fx.Xml(Fx.FarTrigger), 6, null, null, 3, null);
            }
            finally
            {
                Cadence.Scheduler.Com.ComRelease.Release(task);
                Cadence.Scheduler.Com.ComRelease.Release(sub);
                Cadence.Scheduler.Com.ComRelease.Release(folder);
            }
        }
        TestFolder.Cleanup();
        T.Eq("\\CadenceTest is gone", false, TestFolder.Exists());
        TestFolder.Cleanup();
        T.True("cleanup of a missing folder is a no-op", true);
    }

    static TEx? Assert<TEx>(Action action) where TEx : Exception
    {
        try { action(); }
        catch (TEx e) { return e; }
        T.True("expected " + typeof(TEx).Name, false, "nothing thrown");
        return null;
    }
}
