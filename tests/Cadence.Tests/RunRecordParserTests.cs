using Cadence.Core.History;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class RunRecordParserTests
{
    public static void FailFixture()
    {
        RunRecord? r = RunRecordParser.ParseLine(HistorySamples.Fail, "job00001");
        T.True("parsed", r is not null);
        if (r is null) return;
        T.Eq("job id comes from the folder", "job00001", r.JobId);
        T.Eq("task path", "\\CadenceSpike\\v1Bat", r.TaskPath);
        T.Eq("run id", "20260930-225550-456", r.RunId);
        T.Eq("status", RunStatus.Failed, r.Status);
        T.Eq("exit", (int?)3, r.ExitCode);
        T.Eq("pid", (int?)64500, r.Pid);
        T.Eq("out chars", 86L, r.OutBytes);
        T.Eq("err chars", 12L, r.ErrBytes);
        T.True("no message", r.Message is null);
        T.Eq("wrapper start offset", TimeSpan.FromHours(-4), r.WrapperStart.Offset);
        T.Eq("duration is end - start (42.2182 ms)", TimeSpan.FromTicks(422182), r.Duration ?? TimeSpan.Zero);
        T.True("wrapper overhead is not in the duration", r.Start!.Value - r.WrapperStart > TimeSpan.FromMilliseconds(500));
    }

    public static void LaunchErrorFixture()
    {
        RunRecord r = RunRecordParser.ParseLine(HistorySamples.LaunchError, "job00001")!;
        T.Eq("status", RunStatus.LaunchError, r.Status);
        T.True("exit and pid are null", r.ExitCode is null && r.Pid is null);
        T.True("duration is null although start and end exist", r.Start is not null && r.End is not null && r.Duration is null);
        T.True("message keeps its quotes", r.Message is not null && r.Message.Contains("\"Start\"", StringComparison.Ordinal) && r.Message.EndsWith("specified\"", StringComparison.Ordinal));
        T.True("a launch error message is not informational", !r.MessageIsInformational);
    }

    public static void TimeoutFixture()
    {
        RunRecord r = RunRecordParser.ParseLine(HistorySamples.Timeout, "job00001")!;
        T.Eq("status", RunStatus.TimedOut, r.Status);
        T.Eq("exit", (int?)1, r.ExitCode);
        T.Eq("duration 3.3384261 s", TimeSpan.FromTicks(33384261), r.Duration ?? TimeSpan.Zero);
        T.Eq("out chars", 240L, r.OutBytes);
    }

    public static void SkippedFixture()
    {
        RunRecord r = RunRecordParser.ParseLine(HistorySamples.Skipped, "job00001")!;
        T.Eq("status", RunStatus.Skipped, r.Status);
        T.True("start is null", r.Start is null);
        T.True("end is set", r.End is not null);
        T.True("duration is null", r.Duration is null);
        T.Eq("message", "holiday: Test Holiday", r.Message);
        T.True("a holiday note is informational", r.MessageIsInformational);
    }

    public static void StatusTokens()
    {
        (string Token, RunStatus? Expected)[] table =
        [
            ("ok", RunStatus.Ok), ("fail", RunStatus.Failed), ("timeout", RunStatus.TimedOut), ("launchError", RunStatus.LaunchError),
            ("skipped", RunStatus.Skipped), ("OK", RunStatus.Ok), ("launcherror", RunStatus.LaunchError),
            ("running", null), ("failed", null), ("", null), ("success", null),
        ];
        foreach (var (token, expected) in table)
        {
            string line = $$"""{"runId":"20261001-120000-000","wrapperStart":"2026-10-01T12:00:00.0000000-04:00","status":"{{token}}"}""";
            RunRecord? r = RunRecordParser.ParseLine(line, "job00001");
            T.True("status '" + token + "' -> " + (expected?.ToString() ?? "rejected"), expected is null ? r is null : r is not null && r.Status == expected);
        }
    }

    public static void GarbledLinesGiveNullNotExceptions()
    {
        string full = HistorySamples.Fail;
        for (int len = 0; len < full.Length; len++)
        {
            string prefix = full[..len];
            RunRecord? r = null;
            bool threw = false;
            try { r = RunRecordParser.ParseLine(prefix, "job00001"); }
            catch (Exception) { threw = true; }
            T.True("truncated at " + len + " does not throw and gives null", !threw && r is null);
        }

        string[] garbage =
        [
            "", "   ", "\r", "not json", "{", "}", "[]", "[1,2]", "null", "42", "\"text\"", "{}", "{\"runId\":\"x\"}",
            """{"runId":"20261001-120000-000","status":"ok"}""",                                                      // no wrapperStart
            """{"runId":"20261001-120000-000","wrapperStart":"yesterday","status":"ok"}""",                           // bad date
            """{"runId":"","wrapperStart":"2026-10-01T12:00:00-04:00","status":"ok"}""",                              // empty run id
            """{"wrapperStart":"2026-10-01T12:00:00-04:00","status":"ok"}""",                                         // no run id
            """{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00"}""",                                           // no status
            """{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00","status":["ok"]}""",                           // status of the wrong type
            "\0\0\0\0\0", "{\"runId\":\"a\",\"wrapperStart\":\"2026-10-01T12:00:00-04:00\",\"status\":\"ok\"} trailing",
        ];
        foreach (string g in garbage)
        {
            bool threw = false;
            RunRecord? r = null;
            try { r = RunRecordParser.ParseLine(g, "job00001"); }
            catch (Exception) { threw = true; }
            T.True("garbage '" + (g.Length > 30 ? g[..30] : g).Replace("\0", "NUL", StringComparison.Ordinal) + "' -> null", !threw && r is null);
        }
    }

    public static void TolerantFieldHandling()
    {
        // unknown properties, a newer v, numbers as strings, an NTSTATUS exit code that arrived unsigned, a UTF-8 BOM before the line
        string line = "﻿{\"v\":7,\"wv\":3,\"future\":{\"a\":[1,2]},\"task\":\"\\\\Cadence\\\\x\",\"runId\":\"20261001-120000-000\",\"wrapperStart\":\"2026-10-01T12:00:00.0000000-04:00\",\"start\":\"2026-10-01T12:00:01.0000000-04:00\",\"end\":\"2026-10-01T12:00:02.5000000-04:00\",\"exit\":3221225786,\"status\":\"fail\",\"pid\":\"77\",\"outBytes\":\"12\",\"errBytes\":1.0,\"msg\":\"\"}";
        RunRecord? r = RunRecordParser.ParseLine(line, "job00001");
        T.True("parsed", r is not null);
        if (r is null) return;
        T.Eq("unsigned NTSTATUS wraps to signed", (int?)-1073741510, r.ExitCode);
        T.Eq("pid from a string", (int?)77, r.Pid);
        T.Eq("out chars from a string", 12L, r.OutBytes);
        T.Eq("err chars from 1.0", 1L, r.ErrBytes);
        T.True("empty message is null", r.Message is null);
        T.Eq("duration 1.5 s", TimeSpan.FromSeconds(1.5), r.Duration ?? TimeSpan.Zero);

        RunRecord minimal = RunRecordParser.ParseLine("""{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00","status":"ok"}""", "job00001")!;
        T.True("missing optional fields become null/0", minimal.Start is null && minimal.End is null && minimal.ExitCode is null && minimal.Pid is null && minimal.OutBytes == 0 && minimal.TaskPath == "" && minimal.Duration is null);

        RunRecord backwards = RunRecordParser.ParseLine("""{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00","start":"2026-10-01T12:00:05-04:00","end":"2026-10-01T12:00:01-04:00","status":"ok"}""", "job00001")!;
        T.True("end before start (clock change) gives no duration", backwards.Duration is null);
    }

    public static void InformationalMessages()
    {
        RunRecord partial = RunRecordParser.ParseLine(
            """{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00","start":"2026-10-01T12:00:01-04:00","end":"2026-10-01T12:00:02-04:00","exit":0,"status":"ok","outBytes":5,"msg":"output not fully captured: a program it started still holds the output open"}""", "job00001")!;
        T.Eq("status stays ok", RunStatus.Ok, partial.Status);
        T.True("output incomplete flag", partial.OutputIncomplete);
        T.True("message is informational, not an error", partial.MessageIsInformational);

        (RunStatus Status, string? Msg, bool Expected)[] table =
        [
            (RunStatus.Failed, "output not fully captured: a program it started still holds the output open", true),
            (RunStatus.TimedOut, "output not fully captured: x", true),
            (RunStatus.Ok, "holiday list expired; ran anyway", true),
            (RunStatus.Skipped, "holiday: Thanksgiving Day", true),
            (RunStatus.Ok, "something odd", false),
            (RunStatus.LaunchError, "output not fully captured", false),
            (RunStatus.Ok, null, false),
            (RunStatus.Ok, "", false),
        ];
        foreach (var (status, msg, expected) in table)
            T.Eq(status + " / " + (msg ?? "null"), expected, RunMessages.IsInformational(status, msg));
    }

    public static void MixedLineEndingsPartialAndBlankLines()
    {
        // LF appends, then a rotation rewrote part of the file with CRLF, then more LF appends, a blank line, garbage and a cut last line.
        string a = HistorySamples.Fail, b = HistorySamples.Timeout, c = HistorySamples.Skipped, d = HistorySamples.LaunchError;
        string text = a + "\r\n" + b + "\r\n" + "\r\n" + c + "\n" + "garbage{\n" + "\n" + d + "\r\n" + HistorySamples.Fail[..60];
        List<RunRecord> list = RunRecordParser.ParseText(text, "job00001");
        T.Eq("four complete records survive", 4, list.Count);
        T.Eq("file order kept", "20260930-225550-456", list[0].RunId);
        T.Eq("second", RunStatus.TimedOut, list[1].Status);
        T.Eq("third", RunStatus.Skipped, list[2].Status);
        T.Eq("fourth (CRLF)", RunStatus.LaunchError, list[3].Status);
        T.True("a CR does not leak into the last field", list[3].Message is { } m && !m.Contains('\r', StringComparison.Ordinal));

        T.Eq("empty text", 0, RunRecordParser.ParseText("", "job00001").Count);
        T.Eq("only newlines", 0, RunRecordParser.ParseText("\n\r\n\n", "job00001").Count);
        T.Eq("last line without any newline still counts", 1, RunRecordParser.ParseText(HistorySamples.Fail, "job00001").Count);
        T.Eq("CR-only is not a separator", 0, RunRecordParser.ParseText(HistorySamples.Fail + "\r" + HistorySamples.Timeout, "job00001").Count);
    }

    public static void UnicodeSurvives()
    {
        string line = """{"runId":"20261001-120000-000","wrapperStart":"2026-10-01T12:00:00.0000000-04:00","status":"launchError","msg":"café 日本語 😀 and raw é日本語😀"}""";
        RunRecord r = RunRecordParser.ParseLine(line, "job00001")!;
        T.Eq("escaped and raw unicode both decode", "café 日本語 \U0001F600 and raw é日本語😀", r.Message);
    }

    public static void UnknownFolderKeepsThePathForAttribution()
    {
        RunRecord r = RunRecordParser.ParseLine(HistorySamples.Fail, "_unknown")!;
        T.Eq("folder is the job id until the store re-attributes", "_unknown", r.JobId);
        T.Eq("task path is kept", "\\CadenceSpike\\v1Bat", r.TaskPath);
    }
}
