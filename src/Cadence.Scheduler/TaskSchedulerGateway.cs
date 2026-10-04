using System.Runtime.InteropServices;
using System.Security.Principal;
using Cadence.Scheduler.Com;

namespace Cadence.Scheduler;

/// <summary>Late-bound COM implementation of <see cref="ITaskSchedulerGateway"/>. Holds no COM object between calls.</summary>
public sealed class TaskSchedulerGateway : ITaskSchedulerGateway
{
    const int LogonInteractiveToken = 3;
    const int FlagHidden = 1;

    readonly SchedulerOptions _options;
    readonly string _folderPath;
    string? _userId;
    string? _userSid;

    public TaskSchedulerGateway(SchedulerOptions? options = null)
    {
        _options = options ?? new SchedulerOptions();
        ValidateSegment(_options.FolderName, "folder name");
        // [Review R] Defence in depth: whatever a caller passes, the real gateway only ever manages \Cadence or \CadenceTest (canonical spelling:
        // the name is also part of the case-sensitive writer-lock mutex name and of the runs root path).
        if (!ManagedFolder.IsCanonical(_options.FolderName) || !ManagedFolder.IsCanonical(_options.RunsRootName))
            throw new ArgumentException("The managed folder must be exactly " + ManagedFolder.Product + " or " + ManagedFolder.Test + " (got '"
                + _options.FolderName + "', runs root '" + _options.RunsRootName + "').", nameof(options));
        _folderPath = "\\" + _options.FolderName;
    }

    public SchedulerOptions Options => _options;

    /// <summary>The managed folder path, for example <c>\Cadence</c>.</summary>
    public string FolderPath => _folderPath;

    public string ConnectedUserId => _userId ??= Call(s => s.ConnectedUserId);

    /// <summary>The SID of the process identity, which is the identity <c>Schedule.Service.Connect()</c> uses. No account-name lookup (it can fail for unusual account names).</summary>
    public string ConnectedUserSid
    {
        get
        {
            if (_userSid is not null) return _userSid;
            using WindowsIdentity me = WindowsIdentity.GetCurrent();
            _userSid = me.User?.Value
                ?? throw new SchedulerException(SchedulerErrorKind.Other, "Windows did not report the SID of the current user.", 0);
            return _userSid;
        }
    }

    public bool IsConnectedUser(string? principalUserId)
    {
        if (string.IsNullOrWhiteSpace(principalUserId)) return false;
        try
        {
            string text = principalUserId.Trim();
            SecurityIdentifier sid = text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(text)
                : (SecurityIdentifier)new NTAccount(text).Translate(typeof(SecurityIdentifier));
            return string.Equals(sid.Value, ConnectedUserSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException or SchedulerException)
        {
            return false;
        }
    }

    public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() => Call(s =>
    {
        var list = new List<RegisteredTaskInfo>();
        object? folder = s.TryGetFolder(_folderPath);
        if (folder is null) return list;
        object? tasks = null;
        try
        {
            dynamic f = folder;
            tasks = (object)f.GetTasks(FlagHidden);
            AddTasks(tasks, list, int.MaxValue, includeXmlErrors: false);
        }
        finally
        {
            ComRelease.Release(tasks);
            ComRelease.Release(folder);
        }
        return list;
    });

    public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => Call(s =>
    {
        var list = new List<RegisteredTaskInfo>();
        if (max <= 0) return list;
        object? root = s.TryGetFolder("\\");
        if (root is null) return list;
        try { Walk(root, list, max); }
        finally { ComRelease.Release(root); }
        return list;
    });

    public RegisteredTaskInfo? Get(string taskName)
    {
        ValidateSegment(taskName, "task name");
        return Call(s =>
        {
            object? folder = s.TryGetFolder(_folderPath);
            if (folder is null) return null;
            object? task = null;
            try
            {
                dynamic f = folder;
                try
                {
                    task = (object)f.GetTask(taskName);
                    return ToInfo(task);
                }
                catch (Exception ex) when (IsNotFound(ex)) { return null; }   // [CP-B] also when it vanishes while its properties are read
            }
            finally
            {
                ComRelease.Release(task);
                ComRelease.Release(folder);
            }
        });
    }

    public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ValidateSegment(taskName, "task name");
        ArgumentException.ThrowIfNullOrEmpty(xml);
        return Call(s =>
        {
            object? folder = null;
            object? task = null;
            try
            {
                folder = EnsureFolder(s);
                dynamic f = folder;
                // All seven arguments are passed explicitly: userId, password and sddl as null (the XML Principal decides who runs it).
                task = (object)f.RegisterTask(taskName, xml, (int)mode, null, null, LogonInteractiveToken, null);
                return ToInfo(task);
            }
            finally
            {
                ComRelease.Release(task);
                ComRelease.Release(folder);
            }
        });
    }

    public void Delete(string taskName)
    {
        ValidateSegment(taskName, "task name");
        Call(s =>
        {
            object? folder = s.TryGetFolder(_folderPath);
            if (folder is null) return 0;
            try
            {
                dynamic f = folder;
                try { f.DeleteTask(taskName, 0); }
                catch (Exception ex) when (IsNotFound(ex)) { }
            }
            finally { ComRelease.Release(folder); }
            return 0;
        });
    }

    public void Run(string taskName) => WithTask(taskName, task =>
    {
        object? instance = null;
        try { instance = (object)task.Run(null); }
        finally { ComRelease.Release(instance); }
    });

    public void Stop(string taskName) => WithTask(taskName, task => task.Stop(0));

    public void DeleteFolderIfEmpty() => Call(s =>
    {
        object? folder = s.TryGetFolder(_folderPath);
        if (folder is null) return 0;
        object? tasks = null;
        object? subfolders = null;
        try
        {
            dynamic f = folder;
            tasks = (object)f.GetTasks(FlagHidden);
            subfolders = (object)f.GetFolders(0);
            dynamic t = tasks;
            dynamic sf = subfolders;
            if ((int)t.Count == 0 && (int)sf.Count == 0)
            {
                object? root = s.TryGetFolder("\\");
                try
                {
                    dynamic r = root!;
                    r.DeleteFolder(_options.FolderName, 0);
                }
                catch (Exception ex) when (IsNotFound(ex)) { }
                finally { ComRelease.Release(root); }
            }
        }
        finally
        {
            ComRelease.Release(subfolders);
            ComRelease.Release(tasks);
            ComRelease.Release(folder);
        }
        return 0;
    });

    // ---- internals ----

    void WithTask(string taskName, Action<dynamic> action)
    {
        ValidateSegment(taskName, "task name");
        Call(s =>
        {
            object? folder = s.TryGetFolder(_folderPath)
                ?? throw new SchedulerException(SchedulerErrorKind.NotFound, "The folder " + _folderPath + " does not exist.", unchecked((int)0x80070003));
            object? task = null;
            try
            {
                dynamic f = folder;
                task = (object)f.GetTask(taskName);
                action(task);
            }
            finally
            {
                ComRelease.Release(task);
                ComRelease.Release(folder);
            }
            return 0;
        });
    }

    static T Call<T>(Func<TaskServiceSession, T> body)
    {
        try
        {
            using TaskServiceSession session = TaskServiceSession.Open();
            return body(session);
        }
        catch (Exception ex) when (SchedulerException.IsComFailure(ex))
        {
            throw SchedulerException.FromCom(ex);
        }
    }

    object EnsureFolder(TaskServiceSession s)
    {
        object? folder = s.TryGetFolder(_folderPath);
        if (folder is not null) return folder;
        object? root = s.TryGetFolder("\\") ?? throw new SchedulerException(SchedulerErrorKind.Unavailable, "Task Scheduler has no root folder.");
        try
        {
            dynamic r = root;
            try { return (object)r.CreateFolder(_options.FolderName, null); }
            catch (Exception ex) when (SchedulerException.IsComFailure(ex) && (uint)ex.HResult is 0x800700B7 or 0x80070050)
            {
                return s.TryGetFolder(_folderPath) ?? throw SchedulerException.FromCom(ex);
            }
        }
        finally { ComRelease.Release(root); }
    }

    void Walk(object folder, List<RegisteredTaskInfo> into, int max)
    {
        dynamic f = folder;
        string path = (string)f.Path;
        if (IsExcludedFolder(path)) return;

        object? tasks = null;
        try
        {
            tasks = (object)f.GetTasks(FlagHidden);
            AddTasks(tasks, into, max, includeXmlErrors: true);
        }
        catch (Exception ex) when (IsAccessDenied(ex)) { /* a folder we may not enumerate */ }
        finally { ComRelease.Release(tasks); }
        if (into.Count >= max) return;

        object? subs = null;
        try
        {
            subs = (object)f.GetFolders(0);
            dynamic sf = subs;
            int n = (int)sf.Count;
            for (int i = 1; i <= n && into.Count < max; i++)
            {
                object? child = null;
                try
                {
                    child = (object)sf.Item(i);
                    Walk(child, into, max);
                }
                catch (Exception ex) when (IsAccessDenied(ex)) { }
                finally { ComRelease.Release(child); }
            }
        }
        catch (Exception ex) when (IsAccessDenied(ex)) { }
        finally { ComRelease.Release(subs); }
    }

    bool IsExcludedFolder(string path) =>
        string.Equals(path, "\\Cadence", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "\\CadenceTest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, _folderPath, StringComparison.OrdinalIgnoreCase);

    static void AddTasks(object tasks, List<RegisteredTaskInfo> into, int max, bool includeXmlErrors)
    {
        dynamic c = tasks;
        int n = (int)c.Count;
        for (int i = 1; i <= n && into.Count < max; i++)
        {
            object? task = null;
            try
            {
                task = (object)c.Item(i);
                into.Add(ToInfo(task, tolerateUnreadableXml: includeXmlErrors));
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                // [CP-B] The task was deleted while the collection was being read (another Cadence call, the CLI, the MMC): it is simply gone.
                // Before this, a concurrent delete made ListManagedFolder throw NotFound (seen as a flaky GatewayTests.ThreadSafety).
            }
            catch (Exception ex) when (includeXmlErrors && SchedulerException.IsComFailure(ex))
            {
                // A foreign task whose properties cannot be read (broken registration): the read-only listing skips it.
            }
            finally { ComRelease.Release(task); }
        }
    }

    static RegisteredTaskInfo ToInfo(object? task, bool tolerateUnreadableXml = false)
    {
        dynamic t = task!;
        string xml;
        try { xml = (string)t.Xml; }
        catch (Exception ex) when (tolerateUnreadableXml && SchedulerException.IsComFailure(ex)) { xml = ""; }  // access denied, or a registration whose file is missing

        DateTime? last = Real((DateTime)t.LastRunTime);
        int result = (int)t.LastTaskResult;
        return new RegisteredTaskInfo(
            (string)t.Name, (string)t.Path, xml,
            (TaskState)(int)t.State, (bool)t.Enabled,
            last, last is null ? null : result,
            Real((DateTime)t.NextRunTime), (int)t.NumberOfMissedRuns);
    }

    /// <summary>COM DATE 0 / 1899-12-30 and the 1999-11-30 "never" value both mean "no time".</summary>
    static DateTime? Real(DateTime d) => d.Year < 2000 ? null : d;

    static bool IsNotFound(Exception ex) => SchedulerException.IsComFailure(ex) && (uint)ex.HResult is 0x80070002 or 0x80070003;

    static bool IsAccessDenied(Exception ex) => SchedulerException.IsComFailure(ex) && (uint)ex.HResult == 0x80070005;

    static void ValidateSegment(string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Trim().Length == 0 || value is "." or ".."
            || value.AsSpan().IndexOfAny('\\', '/') >= 0 || value.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("The " + what + " '" + value + "' is not allowed: it must be a plain name without path separators.", nameof(value));
    }
}
