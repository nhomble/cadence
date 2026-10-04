namespace Cadence.Cron;

public enum CronNoteLevel { Info, Warning, Error }

/// <summary>What the parser has to say about a line. Message is a complete plain-English sentence that already names the line, the field and the text.</summary>
public sealed record CronNote(CronNoteLevel Level, int Line, string Field, string Message);