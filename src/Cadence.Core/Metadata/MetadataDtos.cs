using System.Text.Json.Serialization;
using Cadence.Core.Model;

namespace Cadence.Core.Metadata;

// Wire shapes of PLAN 3.9. Property names are explicit (never derived from a naming policy) and their declaration order is the
// order in the JSON. Optional values are omitted when null. `env` and `skip.dates` are JSON OBJECTS because the wrapper
// iterates PSObject.Properties and looks dates up by name.

internal sealed class MetadataDto
{
    [JsonPropertyName("schema")] public required int Schema { get; set; }
    [JsonPropertyName("gen")] public required int Gen { get; set; }
    [JsonPropertyName("wv")] public required int Wv { get; set; }
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; set; }
    [JsonPropertyName("schedule")] public required ScheduleDto Schedule { get; set; }
    [JsonPropertyName("run")] public required RunDto Run { get; set; }
    [JsonPropertyName("settings")] public required SettingsDto Settings { get; set; }
    [JsonPropertyName("fp")] public required string Fp { get; set; }
    [JsonPropertyName("created")] public required string Created { get; set; }
    [JsonPropertyName("updated")] public required string Updated { get; set; }
    // PLAN 12.4.1 / 12.4.4: optional, omitted when null (v0.1.0 readers skip unknown properties). agentLock is written only when true.
    [JsonPropertyName("createdBy")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)][JsonConverter(typeof(LenientStringJsonConverter))] public string? CreatedBy { get; set; }
    [JsonPropertyName("updatedBy")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)][JsonConverter(typeof(LenientStringJsonConverter))] public string? UpdatedBy { get; set; }
    [JsonPropertyName("agentLock")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)][JsonConverter(typeof(AgentLockJsonConverter))] public bool? AgentLock { get; set; }
    [JsonPropertyName("prev")]
    [JsonConverter(typeof(ValueListJsonConverter<string>))]
    public required ValueList<string> Prev { get; set; }
}

internal sealed class ScheduleDto
{
    [JsonPropertyName("cron")]
    [JsonConverter(typeof(ValueListJsonConverter<string>))]
    public required ValueList<string> Cron { get; set; }
    [JsonPropertyName("skipHolidays")] public required string SkipHolidays { get; set; }   // none | usFederal | nyse
    [JsonPropertyName("anchor")] public required string Anchor { get; set; }               // yyyy-MM-dd
}

internal sealed class RunDto
{
    [JsonPropertyName("kind")] public required string Kind { get; set; }                   // runProgram
    [JsonPropertyName("exe")] public required string Exe { get; set; }
    [JsonPropertyName("args")] public required string Args { get; set; }
    [JsonPropertyName("cwd")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cwd { get; set; }
    [JsonPropertyName("env")] public required Dictionary<string, string> Env { get; set; }
    [JsonPropertyName("timeoutSec")] public required int TimeoutSec { get; set; }          // 0 = none
    [JsonPropertyName("hidden")] public required bool Hidden { get; set; }
    [JsonPropertyName("capture")] public required bool Capture { get; set; }               // v1 invariant: capture == hidden
    [JsonPropertyName("encoding")] public required string Encoding { get; set; }           // oem | utf8
    [JsonPropertyName("source")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; set; }
    [JsonPropertyName("skip")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SkipDto? Skip { get; set; }
}

internal sealed class SkipDto
{
    [JsonPropertyName("cal")] public required string Cal { get; set; }                     // usFederal | nyse
    [JsonPropertyName("until")] public required string Until { get; set; }                 // yyyy-MM-dd
    [JsonPropertyName("dates")] public required Dictionary<string, string> Dates { get; set; }   // yyyy-MM-dd -> holiday name
}

internal sealed class SettingsDto
{
    [JsonPropertyName("startWhenAvailable")] public required bool StartWhenAvailable { get; set; }
    [JsonPropertyName("runOnBatteries")] public required bool RunOnBatteries { get; set; }
    [JsonPropertyName("stopOnBatteries")] public required bool StopOnBatteries { get; set; }
    [JsonPropertyName("wakeToRun")] public required bool WakeToRun { get; set; }
}
