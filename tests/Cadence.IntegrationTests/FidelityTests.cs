using System.Globalization;
using System.Xml.Linq;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// PLAN 6.3 #2 and #5 plus the CP-A fidelity cases: whatever the composer writes, Task Scheduler must accept and store so that
/// Fingerprint(Read(stored)) equals the composed fingerprint (and the metadata's fp), with Documentation byte for byte.
/// Every task here is composed disabled and anchored in 2099 (see Fx), so none can fire.
/// </summary>
public static class FidelityTests
{
    /// <summary>Every successful row of the PLAN 3.6 table (a row may be several cron lines).</summary>
    internal static readonly string[][] Rows =
    [
        ["0 8 * * 1-5"], ["30 17 * * MON-FRI"], ["0 * * * *"], ["*/15 * * * *"], ["* * * * *"], ["0 9-17 * * 1-5"],
        ["1-59/2 * * * *"], ["*/7 * * * *"], ["5,20,40 */2 * * *"], ["0-58 * * * 1"], ["0 0 L * *"], ["0 17 * * 5L"],
        ["0 9 13 * 5"], ["0 0 * * 0,7"], ["0 0 29 2 *"], ["0 9 * 1,7 *"], ["0 7 * * *", "30 19 * * *"], ["@logon"], ["0 0 0 * * *"],
    ];

    public static void EveryTemplate()
    {
        int n = 0;
        foreach (Template t in Templates.All)
        {
            n++;
            string label = "template " + t.Id;
            Fx.Case(label, () => Fx.ComposeRegisterCheck(label, Fx.Job(t.SuggestedName, [t.Cron], skip: t.SuggestedSkip), "fid template " + n.ToString(CultureInfo.InvariantCulture)));
        }
        T.Eq("all nine templates were exercised", 9, n);
    }

    public static void EveryCronRowOfTable36()
    {
        int n = 0;
        foreach (string[] row in Rows)
        {
            n++;
            string label = "3.6 row " + string.Join(" + ", row);
            Fx.Case(label, () => Fx.ComposeRegisterCheck(label, Fx.Job("Row " + n.ToString(CultureInfo.InvariantCulture), row), "fid row " + n.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public static void HolidaySkipJobKeepsItsList()
    {
        Fx.Case("nyse job", () =>
        {
            JobDefinition job = Fx.Job("Stock ingest", ["0 18 * * 1-5"], skip: HolidayCalendar.Nyse);
            var info = Fx.ComposeRegisterCheck("nyse job", job, "fid nyse", deleteAfter: false);
            MetadataCodec.TryDecode(TaskXmlReader.Read(info!.Xml).RegistrationInfo.Documentation, out CadenceMetadata? meta);
            T.Eq("the 10-year holiday list survives", 2026 + 10, meta?.Skip?.Until.Year);
            T.True("it holds Thanksgiving 2026", meta?.Skip?.Dates.Any(h => h.Date == new DateOnly(2026, 11, 26)) == true);
            Fx.Gateway().Delete("fid nyse");
        });
    }

    /// <summary>CP-A: 25 triggers whose repetition Duration equals its Interval. Task Scheduler must accept them (the oracle check is in ParityTests).</summary>
    public static void DurationEqualsIntervalIsAccepted()
    {
        Fx.Case("0-23,30 9,17", () =>
        {
            JobDefinition job = Fx.Job("Duration equals interval", ["0-23,30 9,17 * * *"]);
            CompiledSchedule compiled = CronCompiler.Compile(job.Schedule, new MapContext("x\\y"));
            T.Eq("compiles", true, compiled.Success);
            T.Eq("25 triggers", 25, compiled.Triggers.Count);
            T.True("every repetition has Duration == Interval", compiled.Triggers.All(t => t.Repetition is { Duration: { } d } r && d == r.Interval),
                string.Join("; ", compiled.Triggers.Take(3).Select(t => t.Repetition)));
            Fx.ComposeRegisterCheck("0-23,30 9,17", job, "fid dur-interval");
        });
    }

    /// <summary>CP-A: does the stored LogonTrigger gain a StartBoundary or a SID UserId? Recorded as INFO lines; the fingerprint must match either way.</summary>
    public static void LogonTriggerStorage()
    {
        foreach (string[] lines in new[] { new[] { "@logon" }, new[] { "@logon", "0 9 * * *" } })
        {
            string label = "logon " + string.Join(" + ", lines);
            Fx.Case(label, () =>
            {
                var info = Fx.ComposeRegisterCheck(label, Fx.Job("Logon", lines), "fid logon", deleteAfter: false);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                XElement? logon = XDocument.Parse(info!.Xml).Descendants(ns + "LogonTrigger").FirstOrDefault();
                T.True("a LogonTrigger is stored", logon is not null);
                Fx.Info(label + ": stored LogonTrigger = " + string.Concat((logon?.Elements() ?? []).Select(e => "<" + e.Name.LocalName + ">" + e.Value + " ")));
                TaskDefinitionModel stored = TaskXmlReader.Read(info.Xml);
                var lt = stored.Triggers.OfType<LogonTrigger>().FirstOrDefault();
                Fx.Info(label + ": parsed StartBoundary=" + (lt?.StartBoundary.ToString("o", CultureInfo.InvariantCulture) ?? "-") + " UserId=" + (lt?.UserId ?? "-"));
                Fx.Gateway().Delete("fid logon");
            });
        }
    }

    /// <summary>CP-A: the 48-trigger schedule from CronCompilerTests.FortyEightTriggerLimit (the limit itself).</summary>
    public static void FortyEightTriggers()
    {
        string[] lines = [.. Enumerable.Repeat("*/7 * * * *", 5), "5,20,40 */2 * * *"];
        Fx.Case("48 triggers", () =>
        {
            T.Eq("compiles to 48", 48, CronCompiler.Compile(Fx.Job("x", lines).Schedule, new MapContext("x\\y")).Triggers.Count);
            Fx.ComposeRegisterCheck("48 triggers", Fx.Job("Forty-eight", lines), "fid 48");
        });
    }

    public static void LastFridayAndPunctuationInNames()
    {
        Fx.Case("5L", () => Fx.ComposeRegisterCheck("0 17 * * 5L", Fx.Job("Last Friday", ["0 17 * * 5L"]), "fid 5L"));
        // Every punctuation character a task name may contain (PLAN 3.8): letters, digits, space and _ . , ( ) + -
        Fx.Case("punctuation", () => Fx.ComposeRegisterCheck("task name with all punctuation", Fx.Job("Punctuation", ["0 9 * * *"]), "a_b.c,d (e)+f-g"));
        Fx.Case("longest name", () =>
        {
            string name = new('n', 80);
            T.Eq("80 characters is a valid task path", true, WrapperScript.ValidateTaskPath("\\CadenceTest\\" + name));
            Fx.ComposeRegisterCheck("80-character task name", Fx.Job("Long", ["0 9 * * *"]), name);
        });
    }

    /// <summary>CP-A: name, args, cwd and env hold non-ASCII text, quotes, &amp;, &lt;, %, $(, CR/LF and a tab. Documentation must come back byte for byte.</summary>
    public static void AwkwardMetadataComesBackByteForByte()
    {
        Fx.Case("awkward", () =>
        {
            JobDefinition job = Fx.Job("Za\u017C\u00F3\u0142\u0107 \"g\u0119\u015Bl\u0105\" & <ja\u017A\u0144> 100% $(x)", ["0 9 * * *"]) with
            {
                Run = new RunSpec
                {
                    Exe = "C:\\Program Files\\caf\u00E9 \u65E5\u672C\u8A9E\\run.bat",
                    Args = "\"q & <r> %PATH% $(Arg0)\" 'single' \u00E9\u65E5\u672C\u8A9E \U0001F600\r\n\ttab",
                    WorkingDirectory = "C:\\Users\\Za\u017C\u00F3\u0142\u0107 & Co",
                    Env = ValueList.Of(
                        new KeyValuePair<string, string>("VAR_1", "a\"b&c<d>%e%$(f)\r\n\tg"),
                        new KeyValuePair<string, string>("\u00C9T\u00C9", "\u2603")),
                    Timeout = TimeSpan.FromSeconds(90),
                    Encoding = OutputEncoding.Utf8,
                },
            };
            var info = Fx.ComposeRegisterCheck("awkward metadata", job, "fid awkward");
            T.True("registered", info is not null);
        });
    }

    /// <summary>PLAN 6.3 #5: the largest metadata Encode allows (16 KB) survives a registration.</summary>
    public static void SixteenKilobytesOfMetadata()
    {
        Fx.Case("16 KB", () =>
        {
            int filler = 15000;
            JobDefinition Make(int n) => Fx.Job("Big", ["0 9 * * *"]) with
            {
                Run = new RunSpec { Exe = "cmd.exe", Args = "/c exit 0 " + new string('x', n) },
            };
            // Tune the filler until the encoded Documentation is within 40 characters of the 16,384 limit.
            for (int i = 0; i < 6; i++)
            {
                int len = TaskComposer.Compose(Make(filler), Fx.Ctx(Fx.Gateway(), "fid big")).RegistrationInfo.Documentation!.Length;
                int delta = MetadataCodec.MaxEncodedLength - 30 - len;
                if (Math.Abs(delta) < 30) break;
                filler += delta;
            }
            JobDefinition job = Make(filler);
            int docLength = TaskComposer.Compose(job, Fx.Ctx(Fx.Gateway(), "fid big")).RegistrationInfo.Documentation!.Length;
            T.True("Documentation is close to 16 KB (" + docLength + ")", docLength is > 16000 and <= 16384);
            Fx.ComposeRegisterCheck("16 KB metadata", job, "fid big");
        });
    }
}
