namespace Cadence.Core.Cron;

public enum CronSeverity { Info, Warning, Error }

/// <summary>Message is a complete plain-English sentence that already names the line, the field and the text.</summary>
public sealed record CronDiagnostic(CronSeverity Severity, int Line, string Field, string Message);
