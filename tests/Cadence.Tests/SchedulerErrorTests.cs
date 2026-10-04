using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The HRESULT / message to SchedulerException mapping (PLAN Phase 7 step 3). No Task Scheduler involved.</summary>
public static class SchedulerErrorTests
{
    static SchedulerErrorKind Kind(uint hr, string msg = "x") => SchedulerException.FromHResult(unchecked((int)hr), msg).Kind;

    public static void HResultMapping()
    {
        T.Eq("access denied", SchedulerErrorKind.AccessDenied, Kind(0x80070005));
        T.Eq("file not found", SchedulerErrorKind.NotFound, Kind(0x80070002));
        T.Eq("path not found", SchedulerErrorKind.NotFound, Kind(0x80070003));
        T.Eq("already exists", SchedulerErrorKind.AlreadyExists, Kind(0x800700B7));
        T.Eq("service not running", SchedulerErrorKind.Unavailable, Kind(0x80041315));
        T.Eq("rpc unavailable", SchedulerErrorKind.Unavailable, Kind(0x800706BA));
        T.Eq("malformed xml", SchedulerErrorKind.InvalidXml, Kind(0x8004131A));
        T.Eq("anything else", SchedulerErrorKind.Other, Kind(0x80004005));
        T.Eq("success-like code", SchedulerErrorKind.Other, Kind(0));
    }

    public static void InvalidXmlPosition()
    {
        var e = SchedulerException.FromHResult(unchecked((int)0x80041318), "(4,106):Interval:PT30S");
        T.Eq("kind", SchedulerErrorKind.InvalidXml, e.Kind);
        T.Eq("line", 4, e.Line);
        T.Eq("column", 106, e.Column);
        T.Eq("element", "Interval", e.Element);
        T.Eq("value", "PT30S", e.Value);
        T.True("message mentions position and value", e.Message.Contains("line 4", StringComparison.Ordinal) && e.Message.Contains("PT30S", StringComparison.Ordinal), e.Message);

        var crlf = SchedulerException.FromHResult(unchecked((int)0x80041318), "(4,130):Duration:PT1H\r\n");
        T.Eq("trailing newline trimmed", "PT1H", crlf.Value);

        var empty = SchedulerException.FromHResult(unchecked((int)0x8004131A), "(1,2)::ERROR: incorrect document syntax");
        T.Eq("empty element", "", empty.Element);
        T.Eq("empty element: line", 1, empty.Line);
        T.True("empty element: explanation is the text", empty.Message.Contains("incorrect document syntax", StringComparison.Ordinal), empty.Message);

        var plain = SchedulerException.FromHResult(unchecked((int)0x80070005), "Access is denied.");
        T.Eq("no position for other errors", (int?)null, plain.Line);
        T.Eq("message kept", "Access is denied.", plain.Message);
        T.Eq("hresult kept", unchecked((int)0x80070005), plain.HResultCode);
    }

    public static void NamesCannotEscape()
    {
        var gw = new TaskSchedulerGateway(new SchedulerOptions("CadenceTest", "CadenceTest"));
        T.Throws<ArgumentException>("separator in a name is refused before any COM call", () => gw.Delete("..\\x"));
        T.Throws<ArgumentException>("folder name with separator", () => new TaskSchedulerGateway(new SchedulerOptions("a\\b")));
    }
}
