using Cadence.Core.TaskXml;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class TaskResultCodesTests
{
    static readonly uint[] Mapped =
    [
        0x41300, 0x41301, 0x41302, 0x41303, 0x41304, 0x41305, 0x41306,
        0x8004130A, 0x8004130B, 0x8004130F, 0x80041315, 0x8004131F, 0x80041326,
        0x80070002, 0x80070003, 0x80070005, 0x800700C1, 0x8007010B, 0x800702E4, 0x8007052E, 0x80070569, 0x80070422, 0x800710E0,
        0xC0000005, 0xC00000FD, 0xC0000135, 0xC0000142, 0xC000013A,
    ];

    static string HexOf(uint code) => "0x" + code.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    public static void KnownCodes()
    {
        (int Code, string Contains)[] table =
        [
            (0, "Completed successfully"),
            (1, "exited with code 1"),
            (2, "exited with code 2"),
            (0x41300, "Ready"),
            (0x41301, "Running"),
            (0x41302, "disabled"),
            (0x41303, "not run yet"),
            (0x41304, "No more runs"),
            (0x41305, "no enabled trigger"),
            (0x41306, "terminated"),
            (unchecked((int)0x8004130A), "has not been set"),
            (unchecked((int)0x8004130B), "no running instance"),
            (unchecked((int)0x8004130F), "No account information"),
            (unchecked((int)0x80041315), "service is not running"),
            (unchecked((int)0x8004131F), "already running"),
            (unchecked((int)0x80041326), "disabled"),
            (unchecked((int)0x80070002), "File not found"),
            (unchecked((int)0x80070003), "Path not found"),
            (unchecked((int)0x80070005), "Access denied"),
            (unchecked((int)0x800700C1), "not a valid Windows program"),
            (unchecked((int)0x8007010B), "folder name is not valid"),
            (unchecked((int)0x800702E4), "elevation"),
            (unchecked((int)0x8007052E), "Logon failure"),
            (unchecked((int)0x80070569), "logon type"),
            (unchecked((int)0x80070422), "disabled"),
            (unchecked((int)0x800710E0), "refused"),
            (unchecked((int)0xC0000005), "access violation"),
            (unchecked((int)0xC00000FD), "stack overflow"),
            (unchecked((int)0xC0000135), "DLL was not found"),
            (unchecked((int)0xC0000142), "failed to initialize"),
            (unchecked((int)0xC000013A), "Ctrl+C"),
        ];
        foreach (var (code, contains) in table)
        {
            string text = TaskResultCodes.Describe(code);
            T.True(HexOf((uint)code) + " mentions '" + contains + "'", text.Contains(contains, StringComparison.OrdinalIgnoreCase), text);
        }
    }

    /// <summary>Owner rule: friendly text for documented codes only, always with the hex code beside it, and never a guessed cause.</summary>
    public static void EveryMappedCodeShowsItsHexAndNoGuessedCause()
    {
        foreach (uint code in Mapped)
        {
            string text = TaskResultCodes.Describe(unchecked((int)code));
            T.True(HexOf(code) + " has its hex in the text", text.Contains("(" + HexOf(code) + ")", StringComparison.Ordinal), text);
            T.True(HexOf(code) + " is not the generic wording", !text.StartsWith("Error 0x", StringComparison.Ordinal) && !text.StartsWith("The program exited", StringComparison.Ordinal), text);
            T.True(HexOf(code) + " never names antivirus or Defender", !text.Contains("antivirus", StringComparison.OrdinalIgnoreCase) && !text.Contains("Defender", StringComparison.OrdinalIgnoreCase), text);
        }
        foreach (uint code in TaskResultCodes.LaunchCodes)
        {
            string text = TaskResultCodes.DescribeLaunchFailure(unchecked((int)code))!;
            T.True(HexOf(code) + " launch sentence has no cause words", !text.Contains("antivirus", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Defender", StringComparison.OrdinalIgnoreCase) && !text.Contains("blocked", StringComparison.OrdinalIgnoreCase), text);
        }
        T.True("the possibilities line is the only text that names antivirus",
            TaskResultCodes.AccessDeniedPossibilities.Contains("antivirus", StringComparison.Ordinal)
            && !TaskResultCodes.AccessDeniedLaunch.Contains("antivirus", StringComparison.OrdinalIgnoreCase)
            && !TaskResultCodes.AccessDeniedLaunch.Contains("Defender", StringComparison.OrdinalIgnoreCase));
        T.True("the possibilities are labelled as possibilities", TaskResultCodes.AccessDeniedPossibilities.StartsWith("Things that can cause this:", StringComparison.Ordinal));
    }

    /// <summary>A program's own exit code is not an HRESULT: 5 is just "exited with code 5", and 0x80070005 is access denied.</summary>
    public static void ExitCodeFiveIsNotAccessDenied()
    {
        T.Eq("exit code 5", "The program exited with code 5", TaskResultCodes.Describe(5));
        T.True("0x80070005 reads differently", TaskResultCodes.Describe(5) != TaskResultCodes.Describe(unchecked((int)0x80070005)));
        T.True("exit code 5 is not a launch failure", !TaskResultCodes.IsLaunchFailure(5) && TaskResultCodes.ListLabel(5) is null);
        T.Eq("exit code 2 is stated as a number only", "The program exited with code 2", TaskResultCodes.Describe(2));
        T.True("no small exit code is explained", !TaskResultCodes.Describe(2).Contains("often", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Checkpoint B: the codes that mean "Task Scheduler could not start the action", with their plain sentences.</summary>
    public static void LaunchFailures()
    {
        T.Eq("0x80070005: states the code, not a cause", TaskResultCodes.AccessDeniedLaunch, TaskResultCodes.DescribeLaunchFailure(unchecked((int)0x80070005)));
        T.Eq("0x80070005 sentence", "Windows refused to start this job: access denied (0x80070005).", TaskResultCodes.AccessDeniedLaunch);
        foreach (uint code in TaskResultCodes.LaunchCodes)
        {
            string hex = HexOf(code);
            string? text = TaskResultCodes.DescribeLaunchFailure(unchecked((int)code));
            T.True(hex + " is a launch failure", TaskResultCodes.IsLaunchFailure(unchecked((int)code)));
            T.True(hex + " sentence names the code and ends with a period", text is not null && text.Contains("(" + hex + ")", StringComparison.Ordinal) && text.EndsWith('.'), text);
            T.Eq(hex + " is found again from its sentence", (int?)unchecked((int)code), TaskResultCodes.LaunchFailureCodeOf(text));
        }
        foreach (int code in new[] { 0, 1, 2, 5, 3, 0x41301, 0x41303, 0x41306, unchecked((int)0x8004131F), unchecked((int)0xC000013A), -1 })
            T.True(HexOf((uint)code) + " is not a launch failure (exit codes and states are not)",
                !TaskResultCodes.IsLaunchFailure(code) && TaskResultCodes.DescribeLaunchFailure(code) is null && TaskResultCodes.ListLabel(code) is null);
    }

    /// <summary>The Last result column is about 205 px: every list label stays short and neutral.</summary>
    public static void ListLabelsFitTheColumn()
    {
        T.Eq("access denied label", "Couldn't start (access denied)", TaskResultCodes.ListLabel(unchecked((int)0x80070005)));
        T.Eq("file not found label", "Couldn't start (file not found)", TaskResultCodes.ListLabel(unchecked((int)0x80070002)));
        foreach (uint code in TaskResultCodes.LaunchCodes)
        {
            string label = TaskResultCodes.ListLabel(unchecked((int)code)) ?? "";
            T.True(HexOf(code) + " label '" + label + "' fits (<= " + TaskResultCodes.MaxListLabelLength + " chars)", label.Length > 0 && label.Length <= TaskResultCodes.MaxListLabelLength, label);
            T.True(HexOf(code) + " label is neutral", label.StartsWith("Couldn't start (", StringComparison.Ordinal)
                && !label.Contains("block", StringComparison.OrdinalIgnoreCase) && !label.Contains("antivirus", StringComparison.OrdinalIgnoreCase), label);
        }
    }

    public static void GenericCodes()
    {
        T.Eq("exit code 3", "The program exited with code 3", TaskResultCodes.Describe(3));
        T.Eq("exit code 259", "The program exited with code 259", TaskResultCodes.Describe(259));
        T.Eq("large positive", "The program exited with code 1000000 (0x000F4240)", TaskResultCodes.Describe(1_000_000));
        T.Eq("unknown HRESULT", "Error 0x80041234", TaskResultCodes.Describe(unchecked((int)0x80041234)));
        T.Eq("unknown NTSTATUS", "Error 0xC0000374", TaskResultCodes.Describe(unchecked((int)0xC0000374)));
        T.Eq("-1", "Error 0xFFFFFFFF", TaskResultCodes.Describe(-1));
        T.True("unknown codes get no friendly cause", !TaskResultCodes.Describe(unchecked((int)0x80041234)).Contains("antivirus", StringComparison.OrdinalIgnoreCase));
    }
}
