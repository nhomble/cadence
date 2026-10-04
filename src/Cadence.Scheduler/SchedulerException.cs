using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cadence.Scheduler;

public enum SchedulerErrorKind { AccessDenied, NotFound, InvalidXml, AlreadyExists, Unavailable, Other }

/// <summary>
/// The only exception the gateway throws for Task Scheduler problems. For <see cref="SchedulerErrorKind.InvalidXml"/> the position
/// Task Scheduler reported (<c>(line,col):Element:Value</c>) is parsed into <see cref="Line"/>, <see cref="Column"/>, <see cref="Element"/>, <see cref="Value"/>.
/// </summary>
public sealed class SchedulerException : Exception
{
    static readonly Regex XmlPosition = new(@"^\((\d+),(\d+)\):(\w*):(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public SchedulerException(SchedulerErrorKind kind, string message, int hresult = 0, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HResultCode = hresult;
    }

    public SchedulerErrorKind Kind { get; }
    public int HResultCode { get; }
    public int? Line { get; private init; }
    public int? Column { get; private init; }
    public string? Element { get; private init; }
    public string? Value { get; private init; }

    /// <summary>
    /// Late-bound COM calls surface a failed HRESULT as COMException OR as the specific .NET type for it (0x80070002 becomes FileNotFoundException,
    /// 0x80070003 DirectoryNotFoundException, 0x80070005 UnauthorizedAccessException, 0x80070057 ArgumentException ...). All of them carry the HRESULT.
    /// </summary>
    public static bool IsComFailure(Exception ex) => ex is COMException or IOException or UnauthorizedAccessException or ArgumentException;

    public static SchedulerException FromCom(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex as SchedulerException ?? FromHResult(ex.HResult, ex.Message, ex);
    }

    /// <summary>The mapping of PLAN Phase 7 step 3 (public so that tests can pin it without a COM object).</summary>
    public static SchedulerException FromHResult(int hresult, string message, Exception? inner = null)
    {
        string text = (message ?? "").Trim();
        Match m = XmlPosition.Match(text);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int line)
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int col))
        {
            string element = m.Groups[3].Value;
            string value = m.Groups[4].Value.Trim();
            string where = "line " + line.ToString(CultureInfo.InvariantCulture) + ", column " + col.ToString(CultureInfo.InvariantCulture);
            string explanation = element.Length > 0 ? "invalid " + element + " value '" + value + "'" : value;
            return new SchedulerException(SchedulerErrorKind.InvalidXml,
                "Task Scheduler rejected the task definition at " + where + ": " + explanation + ".", hresult, inner)
            { Line = line, Column = col, Element = element, Value = value };
        }

        SchedulerErrorKind kind = (uint)hresult switch
        {
            0x80070005 => SchedulerErrorKind.AccessDenied,
            0x80041318 or 0x80041319 or 0x8004131A => SchedulerErrorKind.InvalidXml,   // INVALIDVALUE, MISSINGNODE, MALFORMEDXML
            0x80070002 or 0x80070003 => SchedulerErrorKind.NotFound,
            0x800700B7 or 0x80070050 => SchedulerErrorKind.AlreadyExists,
            0x80041315 or 0x800706BA or 0x800706BE or 0x80080005 => SchedulerErrorKind.Unavailable,
            _ => SchedulerErrorKind.Other,
        };
        return new SchedulerException(kind, text.Length > 0 ? text : "Task Scheduler error 0x" + ((uint)hresult).ToString("X8", CultureInfo.InvariantCulture), hresult, inner);
    }
}
