namespace Cadence.Core.Model;

public sealed record RunSpec
{
    public ActionKind Kind { get; init; } = ActionKind.RunProgram;
    public required string Exe { get; init; }                 // may contain %VARS% (expanded by the wrapper)
    public string Args { get; init; } = "";                   // verbatim Windows command-line tail; %VARS% expanded by the wrapper
    public string? WorkingDirectory { get; init; }
    public ValueList<KeyValuePair<string, string>> Env { get; init; } = ValueList<KeyValuePair<string, string>>.Empty;
    public TimeSpan? Timeout { get; init; }                   // null = no timeout (TS backstop PT72H)
    public bool Hidden { get; init; } = true;                 // true: conhost --headless + capture; false: visible console, no capture
    public OutputEncoding Encoding { get; init; } = OutputEncoding.Oem;
    public string? SourcePath { get; init; }                  // dropped file or shortcut (display only)
}

public sealed record JobSettings
{
    public bool StartWhenAvailable { get; init; } = true;
    public bool RunOnBatteries { get; init; } = true;
    public bool StopOnBatteries { get; init; } = false;
    public bool WakeToRun { get; init; } = false;
}

public sealed record JobDefinition
{
    public required string Id { get; init; }                   // 8 chars [a-z0-9], immutable
    public required string Name { get; init; }                 // display name (1..100 chars)
    public required ScheduleSpec Schedule { get; init; }
    public required RunSpec Run { get; init; }
    public JobSettings Settings { get; init; } = new();
    public bool Enabled { get; init; } = true;

    /// <summary>PLAN 12.4.4: the owner forbids AI agents to change or run this job. Stored in the metadata (omitted when false), not in the task definition.</summary>
    public bool AgentLock { get; init; }
}
