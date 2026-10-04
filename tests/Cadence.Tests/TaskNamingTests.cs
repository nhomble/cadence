using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class TaskNamingTests
{
    static readonly IReadOnlySet<string> None = new HashSet<string>();

    public static void PlanTable()
    {
        (string Input, string Expected)[] table =
        [
            ("Backup notes!", "Backup notes-"),
            ("a/b\\c", "a-b-c"),
            ("   ", "job"),
            ("", "job"),
            ("100% done", "100- done"),
            ("it's", "it-s"),
            ("Stock ingest (Dad)", "Stock ingest (Dad)"),
            ("Backup", "Backup"),
            ("  padded  ", "padded"),
            ("a    b", "a b"),
            ("a---b", "a-b"),
            ("a - b", "a - b"),
            ("a//b", "a-b"),
            ("a: b? c*", "a- b- c-"),
            ("café 日本", "caf- -"),
            ("tab\tand\nnewline", "tab-and-newline"),
            ("keep _.,()+- all", "keep _.,()+- all"),
            ("!!!", "-"),
            (".", "job"),
            ("..", "job"),
            ("...", "job"),
            ("a.b", "a.b"),
            ("a..b", "a.b"),   // [AG-P22] Task Scheduler refuses a task name that contains ".."
            ("Loading...done", "Loading.done"),
            ("..\\..\\Windows\\System32\\x", ".-.-Windows-System32-x"),
            ("\"quoted\"", "-quoted-"),
            ("$(evil)&`x`", "-(evil)-x-"),
            ("100%", "100-"),
        ];
        foreach (var (input, expected) in table)
            T.Eq("'" + input.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'", expected, TaskNaming.ToTaskName(input, None));
    }

    // [CP-A] Names Windows can't use as the task's file name under System32\Tasks.
    public static void FileNameRules()
    {
        (string Input, string Expected)[] table =
        [
            ("Backup.", "Backup"),
            ("Backup ...", "Backup"),
            (". .", "job"),
            ("v1.2", "v1.2"),
            ("CON", "CON-job"),
            ("nul", "nul-job"),
            ("Nul.backup", "Nul-job.backup"),
            ("com1", "com1-job"),
            ("LPT9.", "LPT9-job"),
            ("COM10", "COM10"),
            ("console", "console"),
            ("AUX (old)", "AUX (old)"),
        ];
        foreach (var (input, expected) in table)
        {
            string name = TaskNaming.ToTaskName(input, None);
            T.Eq("'" + input + "'", expected, name);
            T.True("'" + input + "' gives a valid task path", WrapperScript.ValidateTaskPath("\\Cadence\\" + name));
        }
        string longDots = TaskNaming.ToTaskName(new string('a', 79) + ".b", None);
        T.Eq("a cut never ends with a dot", new string('a', 79), longDots);
    }

    public static void LengthCap()
    {
        string name = TaskNaming.ToTaskName(new string('a', 200), None);
        T.Eq("cut at 80", 80, name.Length);
        string spaced = TaskNaming.ToTaskName(new string('a', 79) + " bbbbbb", None);
        T.Eq("cut does not leave a trailing space", new string('a', 79), spaced);
        var taken = new HashSet<string> { name };
        string second = TaskNaming.ToTaskName(new string('a', 200), taken);
        T.Eq("suffix keeps the name within 80", 80, second.Length);
        T.True("suffix present", second.EndsWith(" (2)", StringComparison.Ordinal));
        T.True("suffixed name differs", second != name);
    }

    public static void Duplicates()
    {
        T.Eq("second", "Backup (2)", TaskNaming.ToTaskName("Backup", new HashSet<string> { "Backup" }));
        T.Eq("third", "Backup (3)", TaskNaming.ToTaskName("Backup", new HashSet<string> { "Backup", "Backup (2)" }));
        T.Eq("case-insensitive", "Backup (2)", TaskNaming.ToTaskName("Backup", new HashSet<string> { "BACKUP" }));
        T.Eq("case-insensitive for the suffix (the new name keeps its own case)", "backup (3)", TaskNaming.ToTaskName("backup", new HashSet<string> { "Backup", "BACKUP (2)" }));
        T.Eq("a gap is reused", "Backup (2)", TaskNaming.ToTaskName("Backup", new HashSet<string> { "Backup", "Backup (3)" }));
        T.Eq("collision after sanitizing", "a-b (2)", TaskNaming.ToTaskName("a/b", new HashSet<string> { "a-b" }));
        T.Eq("unrelated names do not matter", "Backup", TaskNaming.ToTaskName("Backup", new HashSet<string> { "Backups", "Other" }));
    }

    public static void EveryResultIsAValidTaskPathSegment()
    {
        var rng = new Random(20261001);
        const string chars = "abcXYZ019 _.,()+-/\\:*?\"<>|!@#$%^&'`~;{}[]=\t\né日\U0001F600";
        for (int i = 0; i < 400; i++)
        {
            int len = rng.Next(0, 120);
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < len; k++) sb.Append(chars[rng.Next(chars.Length)]);   // lone surrogates are fine: they map to '-'
            string name = TaskNaming.ToTaskName(sb.ToString(), None);
            if (!WrapperScript.ValidateTaskPath("\\Cadence\\" + name) || name.Contains("..", StringComparison.Ordinal)) { T.True("valid path for input #" + i, false, name); return; }
        }
        T.True("400 random inputs all give valid task paths", true);
    }

    public static void JobIds()
    {
        for (int i = 0; i < 200; i++)
            if (!JobId.IsValid(JobId.New())) { T.True("generated id valid", false); return; }
        T.True("200 generated ids are valid", true);
        var seen = new HashSet<string>(Enumerable.Range(0, 500).Select(_ => JobId.New()));
        T.True("500 ids are distinct", seen.Count == 500);
        foreach (var (id, ok) in new (string?, bool)[]
        {
            ("d4dst0ck", true), ("abcdefgh", true), ("01234567", true), (null, false), ("", false), ("abc", false), ("abcdefghi", false),
            ("ABCDEFGH", false), ("abcdefg-", false), ("abcdefg ", false), ("abcdeféh", false),
        })
            T.Eq("IsValid(" + (id ?? "null") + ")", ok, JobId.IsValid(id));
    }
}
