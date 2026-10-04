using Cadence.Core.Model;
using Cadence.Cron;

namespace Cadence.Core.Cron;

/// <summary>
/// The one place where Cadence.Core meets the cron module: parses a cron line with <see cref="CronParser"/> and translates the result into Core's own
/// <see cref="CronExpr"/> and <see cref="CronDiagnostic"/> (the flag enums and lists have the same bits and items, so the mapping is a copy). No other file in
/// <c>src</c> may mention Cadence.Cron (checked by tools\check-layering.ps1), so a different cron engine means rewriting this file only.
/// </summary>
public static class CronSyntax
{
    /// <summary>Parses one cron line (PLAN.md 3.2 dialect). Returns null when the line has an Error diagnostic; every problem found is added to <paramref name="diags"/>.</summary>
    public static CronExpr? Parse(string line, int lineNo, List<CronDiagnostic> diags)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(diags);
        var notes = new List<CronNote>();
        CronExpression? e = CronParser.Parse(line, lineNo, notes);
        foreach (CronNote n in notes) diags.Add(new CronDiagnostic(Severity(n.Level), n.Line, n.Field, n.Message));
        return e is null ? null : Map(e);
    }

    static CronSeverity Severity(CronNoteLevel level) => level switch
    {
        CronNoteLevel.Info => CronSeverity.Info,
        CronNoteLevel.Warning => CronSeverity.Warning,
        CronNoteLevel.Error => CronSeverity.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    static CronExpr Map(CronExpression e) => new(
        new ValueList<int>(e.Minutes),
        new ValueList<int>(e.Hours),
        new DomSpec(e.Dom.Star, new ValueList<int>(e.Dom.Days), e.Dom.Last),
        (MonthSet)(int)e.Months,
        new DowSpec(e.Dow.Star, (WeekDays)(int)e.Dow.Days, new ValueList<NthWeekday>(e.Dow.Nth.Select(n => new NthWeekday((WeekOfMonth)n.Week, n.Day)))),
        e.AtLogon);
}