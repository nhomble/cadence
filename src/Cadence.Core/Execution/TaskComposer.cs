using Cadence.Core.Cron;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;

namespace Cadence.Core.Execution;

/// <summary>
/// Everything the composer needs from the outside world. <c>TaskName</c> is the final Task Scheduler task name (see <see cref="TaskNaming"/>);
/// the task path is <c>\FolderName\TaskName</c>. <c>Created</c> is null for a new job (then <c>Now</c> is used).
/// <c>Actor</c> is who is writing (stored as <c>updatedBy</c>, and as <c>createdBy</c> for a new job); null writes no <c>updatedBy</c>.
/// <c>CreatedBy</c> is the creator token of an existing job (kept across updates; tolerant: an unreadable token is dropped).
/// Neither is part of the fingerprint (the metadata lives in RegistrationInfo, which the projection excludes).
/// </summary>
public sealed record ComposeContext(string FolderName, string RunsRootName, string UserId, string TaskName,
    DateTimeOffset Now, DateTimeOffset? Created, ValueList<string> PreviousNames, Actor? Actor = null, string? CreatedBy = null);

/// <summary>Builds the Task Scheduler definition (and its embedded metadata) for a <see cref="JobDefinition"/> (PLAN 3.11). Pure and deterministic.</summary>
public static class TaskComposer
{
    public const int GeneratorVersion = 1;

    /// <summary>Years of holidays embedded after the current one (current year through current + 10).</summary>
    public const int HolidayYearsAhead = 10;

    /// <summary>Follows the English schedule in every composed Description (Untrack removes it again).</summary>
    public const string DescriptionSuffix = ". Managed by Cadence - edit it in the Cadence app.";

    static readonly TimeSpan Backstop = TimeSpan.FromHours(72);
    static readonly TimeSpan TimeoutSlack = TimeSpan.FromMinutes(5);

    public static string TaskPath(ComposeContext ctx) => "\\" + ctx.FolderName + "\\" + ctx.TaskName;

    public static TaskDefinitionModel Compose(JobDefinition job, ComposeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ctx);

        string taskPath = TaskPath(ctx);
        if (!WrapperScript.ValidateTaskPath(taskPath))
            throw new ComposeException("The task path '" + taskPath + "' is not allowed: it must be \\Folder\\Task name, using only letters, digits, spaces and _.,()+- (name up to 80 characters).");
        if (!WrapperScript.ValidateRunsRoot(ctx.RunsRootName))
            throw new ComposeException("The runs-root name '" + ctx.RunsRootName + "' must be letters and digits only.");
        if (!JobId.IsValid(job.Id))
            throw new ComposeException("The job id '" + job.Id + "' is not 8 characters of a-z and 0-9.");

        CompiledSchedule compiled = CronCompiler.Compile(job.Schedule, new MapContext(ctx.UserId));
        if (!compiled.Success)
        {
            var errors = compiled.Diagnostics.Where(d => d.Severity == CronSeverity.Error).Select(d => d.Message).ToArray();
            throw new ComposeException("The schedule can't be used. " + string.Join(" ", errors), compiled.Diagnostics);
        }

        DateTimeOffset now = WholeSeconds(ctx.Now);
        DateTimeOffset created = WholeSeconds(ctx.Created ?? ctx.Now);

        int timeoutSec = MetadataCodec.TimeoutSeconds(job.Run);
        TimeSpan limit = timeoutSec > 0 ? TimeSpan.FromSeconds(timeoutSec) + TimeoutSlack : Backstop;

        string description = EnglishRenderer.Describe(compiled, job.Schedule.SkipHolidays) + DescriptionSuffix;

        var model = new TaskDefinitionModel(
            new RegistrationInfoModel("Cadence", description, null, "Cadence", null, null),
            compiled.Triggers,
            new PrincipalModel(ctx.UserId, "InteractiveToken", "LeastPrivilege"),
            new SettingsModel
            {
                Enabled = job.Enabled,
                MultipleInstancesPolicy = "IgnoreNew",
                StartWhenAvailable = job.Settings.StartWhenAvailable,
                DisallowStartIfOnBatteries = !job.Settings.RunOnBatteries,
                StopIfGoingOnBatteries = job.Settings.StopOnBatteries,
                WakeToRun = job.Settings.WakeToRun,
                ExecutionTimeLimit = limit,
            },
            new ValueList<ExecActionModel>([WrapperScript.BuildAction(taskPath, ctx.RunsRootName, job.Run.Hidden)]),
            ValueList<string>.Empty);

        // The fingerprint ignores RegistrationInfo, so the metadata can carry the fingerprint of the task it lives in.
        string fp = Fingerprint.Of(model);

        SkipList? skip = null;
        if (job.Schedule.SkipHolidays != HolidayCalendar.None)
        {
            int lastYear = now.Year + HolidayYearsAhead;
            skip = new SkipList(job.Schedule.SkipHolidays, new DateOnly(lastYear, 12, 31),
                HolidayRules.Range(job.Schedule.SkipHolidays, now.Year, lastYear));
        }

        // PLAN 12.4.1: a new job is created by whoever saves it; an existing job keeps its creator (normalized: garbage reads as unknown and is dropped).
        string? updatedBy = ctx.Actor?.Token;
        string? createdBy = ctx.Created is null ? ctx.Actor?.Token : Actor.Parse(ctx.CreatedBy).Token;

        var metadata = new CadenceMetadata(MetadataCodec.CurrentSchema, GeneratorVersion, WrapperScript.Version,
            job, skip, fp, created, now, ctx.PreviousNames, createdBy, updatedBy);

        string documentation;
        try { documentation = MetadataCodec.Encode(metadata); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ComposeException(ex.Message, null, ex);
        }

        return model with { RegistrationInfo = model.RegistrationInfo with { Documentation = documentation } };
    }

    public static string ComposeXml(JobDefinition job, ComposeContext ctx) => TaskXmlWriter.Write(Compose(job, ctx));

    static DateTimeOffset WholeSeconds(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);
}
