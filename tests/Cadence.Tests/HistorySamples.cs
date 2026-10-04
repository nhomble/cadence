using System.Globalization;
using Cadence.Core.History;

namespace Cadence.Tests;

/// <summary>Real run records captured in the spikes (PLAN Phase 9 fixtures) and a builder for synthetic ones. Not a test class.</summary>
static class HistorySamples
{
    public const string Fail = """{"v":1,"wv":1,"task":"\\CadenceSpike\\v1Bat","runId":"20260930-225550-456","wrapperStart":"2026-09-30T22:55:50.4563003-04:00","start":"2026-09-30T22:55:51.0329322-04:00","end":"2026-09-30T22:55:51.0751504-04:00","exit":3,"status":"fail","pid":64500,"outBytes":86,"errBytes":12,"msg":null}""";
    public const string LaunchError = """{"v":1,"wv":1,"task":"\\CadenceSpike\\wrapMissing","runId":"20260930-223943-752","wrapperStart":"2026-09-30T22:39:43.7524356-04:00","start":"2026-09-30T22:39:44.1618290-04:00","end":"2026-09-30T22:39:44.1762887-04:00","exit":null,"status":"launchError","pid":null,"outBytes":0,"errBytes":0,"msg":"Exception calling \"Start\" with \"1\" argument(s): \"The system cannot find the file specified\""}""";
    public const string Timeout = """{"v":1,"wv":1,"task":"\\CadenceSpike\\wrapTimeout","runId":"20260930-223943-722","wrapperStart":"2026-09-30T22:39:43.7229467-04:00","start":"2026-09-30T22:39:44.1347380-04:00","end":"2026-09-30T22:39:47.4731641-04:00","exit":1,"status":"timeout","pid":89232,"outBytes":240,"errBytes":0,"msg":null}""";
    public const string Skipped = """{"v":1,"wv":1,"task":"\\CadenceSpike\\skipToday","runId":"20261001-002054-310","wrapperStart":"2026-10-01T00:20:54.3107947-04:00","start":null,"end":"2026-10-01T00:20:56.7302360-04:00","exit":null,"status":"skipped","pid":null,"outBytes":0,"errBytes":0,"msg":"holiday: Test Holiday"}""";

    public static readonly string[] AllLines = [Fail, LaunchError, Timeout, Skipped];

    public static List<RunRecord> Fixtures() =>
        AllLines.Select(l => RunRecordParser.ParseLine(l, "spike0001")!).ToList();

    /// <summary>A fixed "now" for tests that need one; nothing here reads the clock.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(-4));

    /// <summary>A record that started <paramref name="at"/> and ran <paramref name="seconds"/> (null = no program ran).</summary>
    public static RunRecord Rec(DateTimeOffset at, RunStatus status, double? seconds = null, string? msg = null, int? exit = null, long outChars = 0, string? runId = null)
    {
        DateTimeOffset? start = seconds is null ? null : at.AddMilliseconds(500);
        DateTimeOffset? end = start?.AddSeconds(seconds!.Value);
        runId ??= at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return new RunRecord("job00001", "\\Cadence\\test", runId, at, start, end, exit ?? (status == RunStatus.Ok ? 0 : status == RunStatus.Failed ? 1 : null),
            status, seconds is null ? null : 100, outChars, 0, msg);
    }

    public static DateTime Utc(DateTimeOffset t) => t.UtcDateTime;
}
