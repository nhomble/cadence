using System.Globalization;

namespace Cadence.Core.TaskXml;

/// <summary>
/// Plain-English text for a Task Scheduler <c>LastTaskResult</c> (PLAN 7). Pure: no Windows API.
/// Owner rule: "we shouldn't guess, but for stable error codes we can be friendly". Only codes Microsoft documents get friendly text, and
/// the text says what the code means, never why it happened (no inference from a code to antivirus, permissions, and so on). Every
/// friendly text carries its hex code. Anything else reads "Error 0x…" (or "The program exited with code N" for a small exit code).
/// </summary>
public static class TaskResultCodes
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED). A program's own exit code 5 is NOT this value.</summary>
    public const uint AccessDenied = 0x80070005;

    public static string Describe(int code)
    {
        uint u = (uint)code;
        switch (u)
        {
            case 0: return "Completed successfully (0)";
            // Plain exit codes of the program itself: stated as the number only. Whatever the program means by them is not the code's to say.
            case 1: return "The program exited with code 1 (a general error)";
            case 2: return "The program exited with code 2";
        }
        string? reason = TaskSchedulerReason(u) ?? Win32Reason(u) ?? NtStatusReason(u);
        return reason is null ? Generic(code) : reason + " (" + Hex(u) + ")";
    }

    /// <summary>
    /// Task Scheduler constants: Microsoft Learn, "Task Scheduler Error and Success Constants" (taskschd.h / winerror.h, SCHED_S_* and SCHED_E_*).
    /// Meanings follow the documented descriptions.
    /// </summary>
    static string? TaskSchedulerReason(uint code) => code switch
    {
        0x00041300 => "Ready: waiting for the next scheduled run",                                  // SCHED_S_TASK_READY
        0x00041301 => "Running now",                                                                 // SCHED_S_TASK_RUNNING
        0x00041302 => "The task is disabled",                                                        // SCHED_S_TASK_DISABLED
        0x00041303 => "The task has not run yet",                                                    // SCHED_S_TASK_HAS_NOT_RUN
        0x00041304 => "No more runs are scheduled",                                                  // SCHED_S_TASK_NO_MORE_RUNS
        0x00041305 => "The task has no enabled trigger, so nothing is scheduled",                    // SCHED_S_TASK_NO_VALID_TRIGGERS
        0x00041306 => "The last run of the task was terminated",                                     // SCHED_S_TASK_TERMINATED
        0x8004130A => "A property the task needs in order to run has not been set",                  // SCHED_E_TASK_NOT_READY
        0x8004130B => "There is no running instance of the task",                                    // SCHED_E_TASK_NOT_RUNNING
        0x8004130F => "No account information is stored for this task",                              // SCHED_E_ACCOUNT_INFORMATION_NOT_SET
        0x80041315 => "The Task Scheduler service is not running",                                   // SCHED_E_SERVICE_NOT_RUNNING
        0x8004131F => "An instance of the task is already running",                                  // SCHED_E_ALREADY_RUNNING
        0x80041326 => "The task is disabled",                                                        // SCHED_E_TASK_DISABLED
        _ => null,
    };

    /// <summary>
    /// Win32 error codes as HRESULTs (HRESULT_FROM_WIN32 = 0x8007 + the Win32 error): Microsoft Learn, "System Error Codes" (winerror.h, ERROR_*).
    /// </summary>
    static string? Win32Reason(uint code) => code switch
    {
        0x80070002 => "File not found",                                                              // ERROR_FILE_NOT_FOUND (2)
        0x80070003 => "Path not found",                                                              // ERROR_PATH_NOT_FOUND (3)
        0x80070005 => "Access denied",                                                               // ERROR_ACCESS_DENIED (5)
        0x800700C1 => "The program is not a valid Windows program",                                  // ERROR_BAD_EXE_FORMAT (193)
        0x8007010B => "The folder name is not valid",                                                // ERROR_DIRECTORY (267)
        0x800702E4 => "The operation requires elevation (administrator rights)",                     // ERROR_ELEVATION_REQUIRED (740)
        0x8007052E => "Logon failure: the user name or password is incorrect",                       // ERROR_LOGON_FAILURE (1326)
        0x80070569 => "Logon failure: the account was not granted the requested logon type",         // ERROR_LOGON_TYPE_NOT_GRANTED (1385)
        0x80070422 => "The service cannot be started because it is disabled",                        // ERROR_SERVICE_DISABLED (1058)
        0x800710E0 => "The request was refused",                                                     // ERROR_REQUEST_REFUSED (4320)
        _ => null,
    };

    /// <summary>
    /// NTSTATUS values a program can exit with: Microsoft Learn, "NTSTATUS values" (ntstatus.h, STATUS_*). Stated as what the status
    /// is called, not why it happened.
    /// </summary>
    static string? NtStatusReason(uint code) => code switch
    {
        0xC0000005 => "The program stopped with an access violation",                                // STATUS_ACCESS_VIOLATION
        0xC00000FD => "The program stopped with a stack overflow",                                   // STATUS_STACK_OVERFLOW
        0xC0000135 => "A required DLL was not found",                                                // STATUS_DLL_NOT_FOUND
        0xC0000142 => "A DLL failed to initialize",                                                  // STATUS_DLL_INIT_FAILED
        0xC000013A => "The program was ended with Ctrl+C or its console was closed",                 // STATUS_CONTROL_C_EXIT
        _ => null,
    };

    /// <summary>The sentence for Task Scheduler reporting access denied (0x80070005) when it tried to start the job: what Windows said, not why.</summary>
    public const string AccessDeniedLaunch = "Windows refused to start this job: access denied (0x80070005).";

    /// <summary>
    /// A clearly labelled list of POSSIBILITIES, shown beside <see cref="AccessDeniedLaunch"/>. The code does not say which one applies,
    /// so this is never worded as a diagnosis. The only place the text names antivirus.
    /// </summary>
    public const string AccessDeniedPossibilities =
        "Things that can cause this: antivirus or other security software, permissions on a file or folder the job uses, or a program that needs administrator rights.";

    /// <summary>
    /// True for the <c>LastTaskResult</c> values Task Scheduler reports when it could NOT start the task's action at all (so no program
    /// and no wrapper ran). These are HRESULTs, never plain exit codes: a program's exit code 5 is not 0x80070005.
    /// </summary>
    public static bool IsLaunchFailure(int code) => DescribeLaunchFailure(code) is not null;

    /// <summary>
    /// A plain sentence for a launch failure (see <see cref="IsLaunchFailure"/>), or null when <paramref name="code"/> is not one.
    /// Used for the synthesized "Didn't report" row, where Task Scheduler's code is the only evidence of what happened (Checkpoint B).
    /// </summary>
    public static string? DescribeLaunchFailure(int code)
    {
        uint u = (uint)code;
        if (u == AccessDenied) return AccessDeniedLaunch;
        if (Array.IndexOf(LaunchCodes, u) < 0) return null;
        string? reason = TaskSchedulerReason(u) ?? Win32Reason(u);
        return reason is null ? null : "Windows could not start this job: " + char.ToLowerInvariant(reason[0]) + reason[1..] + " (" + Hex(u) + ").";
    }

    /// <summary>
    /// A few words for the list's Last result column: "Couldn't start (access denied)", or null when <paramref name="code"/> is not a launch
    /// failure. Short enough for the column (at most <see cref="MaxListLabelLength"/> characters).
    /// </summary>
    public static string? ListLabel(int code)
    {
        string? shortReason = (uint)code switch
        {
            0x80070005 => "access denied",
            0x80070002 => "file not found",
            0x80070003 => "path not found",
            0x800700C1 => "invalid program",
            0x8007010B => "invalid folder",
            0x800702E4 => "needs elevation",
            0x800710E0 => "request refused",
            0x8004130F => "no account info",
            _ => null,
        };
        return shortReason is null ? null : "Couldn't start (" + shortReason + ")";
    }

    /// <summary>The longest <see cref="ListLabel"/> the Last result column is meant to hold.</summary>
    public const int MaxListLabelLength = 34;

    /// <summary>The launch-failure code a stored message sentence stands for (so a row built from the message alone still reads right), or null.</summary>
    public static int? LaunchFailureCodeOf(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        foreach (uint c in LaunchCodes)
            if (string.Equals(DescribeLaunchFailure(unchecked((int)c)), message, StringComparison.Ordinal)) return unchecked((int)c);
        return null;
    }

    /// <summary>Every code <see cref="DescribeLaunchFailure"/> answers for.</summary>
    public static readonly uint[] LaunchCodes = [0x80070005, 0x80070002, 0x80070003, 0x800700C1, 0x8007010B, 0x800702E4, 0x800710E0, 0x8004130F];

    static string Hex(uint code) => "0x" + code.ToString("X", CultureInfo.InvariantCulture);

    static string Generic(int code)
    {
        // Small non-negative values are the exit code of the program; anything with the high bit set is an HRESULT or NTSTATUS.
        if (code >= 0 && code < 0x10000)
            return "The program exited with code " + code.ToString(CultureInfo.InvariantCulture);
        if (code >= 0)
            return "The program exited with code " + code.ToString(CultureInfo.InvariantCulture) + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
        return "Error 0x" + ((uint)code).ToString("X8", CultureInfo.InvariantCulture);
    }
}
