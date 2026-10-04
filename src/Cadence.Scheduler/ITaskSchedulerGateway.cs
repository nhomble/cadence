namespace Cadence.Scheduler;

/// <summary>Which writes Task Scheduler may perform for <see cref="ITaskSchedulerGateway.Register"/> (the TASK_CREATE flags).</summary>
public enum RegisterMode
{
    /// <summary>TASK_CREATE (2): fails when the task exists.</summary>
    Create = 2,

    /// <summary>TASK_UPDATE (4): fails when the task does not exist.</summary>
    Update = 4,

    /// <summary>TASK_CREATE_OR_UPDATE (6).</summary>
    CreateOrUpdate = 6,
}

/// <summary>
/// The thin, safe boundary to Windows Task Scheduler (PLAN Phase 7). Task definitions travel as XML strings. Every call opens its own
/// COM session, so the gateway may be called from thread-pool threads. Task names are plain names inside the managed folder;
/// a name that contains a path separator is refused (ArgumentException) so that no call can reach another folder.
/// </summary>
public interface ITaskSchedulerGateway
{
    /// <summary>The managed folder and runs-root names this gateway was created with.</summary>
    SchedulerOptions Options { get; }

    /// <summary>"DOMAIN\user" of the connected account.</summary>
    string ConnectedUserId { get; }

    /// <summary>The SID string of <see cref="ConnectedUserId"/> (for example S-1-5-21-...).</summary>
    string ConnectedUserSid { get; }

    /// <summary>True when <paramref name="principalUserId"/> (a SID string or DOMAIN\user) resolves to the connected user's SID. False on null, blank or translation failure.</summary>
    bool IsConnectedUser(string? principalUserId);

    /// <summary>Tasks in the managed folder, hidden ones included. Empty if the folder does not exist.</summary>
    IReadOnlyList<RegisteredTaskInfo> ListManagedFolder();

    /// <summary>Read-only recursive listing from the root, excluding \Cadence\, \CadenceTest\ and the managed folder. Folders that deny access are skipped.</summary>
    IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000);

    /// <summary>The task in the managed folder, or null if it does not exist.</summary>
    RegisteredTaskInfo? Get(string taskName);

    /// <summary>
    /// Writes the task with an interactive-token logon. Creates the managed folder if missing. Throws <see cref="SchedulerException"/>.
    /// <see cref="RegisterMode.Create"/> fails with <see cref="SchedulerErrorKind.AlreadyExists"/> when the name is taken, so a task that
    /// appeared after the caller's live check is never overwritten.
    /// </summary>
    RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate);

    /// <summary>Deletes the task; does nothing if it (or the folder) does not exist.</summary>
    void Delete(string taskName);

    void Run(string taskName);

    void Stop(string taskName);

    /// <summary>Deletes the managed folder if it holds no tasks and no subfolders.</summary>
    void DeleteFolderIfEmpty();
}
