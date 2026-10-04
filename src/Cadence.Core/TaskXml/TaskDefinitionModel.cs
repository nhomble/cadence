using Cadence.Core.Model;

namespace Cadence.Core.TaskXml;

public sealed record RegistrationInfoModel(string? Author, string? Description, string? Documentation, string? Source, string? Uri, string? Date);

public sealed record PrincipalModel(string? UserId, string LogonType /*InteractiveToken|S4U|Password|...*/, string RunLevel /*LeastPrivilege|HighestAvailable*/);

/// <summary>Defaults are the Task Scheduler schema defaults, applied when an element is absent.</summary>
public sealed record SettingsModel
{
    public bool Enabled { get; init; } = true;
    public bool Hidden { get; init; } = false;
    public string MultipleInstancesPolicy { get; init; } = "IgnoreNew";
    public bool StartWhenAvailable { get; init; } = false;
    public bool DisallowStartIfOnBatteries { get; init; } = true;
    public bool StopIfGoingOnBatteries { get; init; } = true;
    public TimeSpan? ExecutionTimeLimit { get; init; } = TimeSpan.FromHours(72);   // PT0S -> null (unlimited)
    public bool WakeToRun { get; init; } = false;
    public bool AllowStartOnDemand { get; init; } = true;
    public bool AllowHardTerminate { get; init; } = true;
    public bool RunOnlyIfNetworkAvailable { get; init; } = false;
    public bool RunOnlyIfIdle { get; init; } = false;
    public int Priority { get; init; } = 7;
    public TimeSpan? DeleteExpiredTaskAfter { get; init; }
    public int? RestartCount { get; init; }
    public TimeSpan? RestartInterval { get; init; }
    public ValueList<string> OtherElements { get; init; } = ValueList<string>.Empty;   // canonical outer XML of unmodeled children (sorted)
}

public sealed record ExecActionModel(string Command, string? Arguments, string? WorkingDirectory);

public sealed record TaskDefinitionModel(
    RegistrationInfoModel RegistrationInfo, ValueList<TaskTrigger> Triggers, PrincipalModel Principal,
    SettingsModel Settings, ValueList<ExecActionModel> ExecActions, ValueList<string> OtherActionsXml);
