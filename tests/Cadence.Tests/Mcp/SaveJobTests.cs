using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>
/// Phase 21 (PLAN 12.3.2): <c>cadence_save_job</c>, create side and every refusal, over an in-memory gateway: the happy path with attribution and defaults, each error
/// code with the start of its message, program rules, limits at their boundaries, the Cadence-reference rules, idempotent retries, name rules, the audit trail
/// and "no secret in any output or audit line".
/// </summary>
public static class SaveJobTests
{
    static readonly Regex JobIdRx = new("\\A[a-z0-9]{8}\\z");
    static readonly Regex RevisionRx = new("\\A[0-9a-f]{12}\\z");

    static string Id(JsonObject r) => r["job"]!["jobId"]!.GetValue<string>();

    static string Rev(JsonObject r) => r["job"]!["revision"]!.GetValue<string>();

    // ---- create: happy path ----

    public static void CreateWritesAVerifiedJobAttributedToTheAgent()
    {
        using var rig = new WriteRig();
        ToolResult res = rig.Save(WriteRig.Create("Backup notes", extraProgram: ",\"arguments\":\"--go ARGMARK-1\",\"environment\":{\"API_KEY\":\"" + WriteRig.Secret + "\",\"MODE\":\"x\"}"));
        T.True("not an error: " + res.Text, !res.IsError);
        JsonObject r = res.Structured!;
        T.Eq("outcome", "created", r["outcome"]!.GetValue<string>());
        T.Eq("message", "Created \\" + rig.Folder + "\\Backup notes.", r["message"]!.GetValue<string>());
        T.Eq("changes", "Will create \\" + rig.Folder + "\\Backup notes", r["changes"]!.GetValue<string>());
        JsonObject job = r["job"]!.AsObject();
        T.True("job id is 8 characters", JobIdRx.IsMatch(job["jobId"]!.GetValue<string>()));
        T.True("revision is 12 hex characters", RevisionRx.IsMatch(job["revision"]!.GetValue<string>()));
        T.Eq("state ok (verified InSync)", "ok", job["state"]!.GetValue<string>());
        T.Eq("name", "Backup notes", job["name"]!.GetValue<string>());
        T.Eq("enabled by default", true, job["enabled"]!.GetValue<bool>());
        T.Eq("sentence", "Every weekday at 18:00", job["schedule"]!["description"]!.GetValue<string>());
        T.Eq("cron kept verbatim", "0 18 * * 1-5", job["schedule"]!["cron"]![0]!.GetValue<string>());
        T.Eq("holidays none", "none", job["schedule"]!["skipHolidays"]!.GetValue<string>());
        JsonObject p = job["program"]!.AsObject();
        T.Eq("the agent default timeout is 60 minutes", 60, p["timeoutMinutes"]!.GetValue<int>());
        T.Eq("hidden by default", true, p["hidden"]!.GetValue<bool>());
        T.Eq("oem output by default", "oem", p["outputEncoding"]!.GetValue<string>());
        T.Eq("the working folder defaults to the program's folder", @"C:\Tools", p["workingFolder"]!.GetValue<string>());
        T.Eq("path as written", WriteRig.Exe, p["path"]!.GetValue<string>());
        T.Eq("arguments", "--go ARGMARK-1", p["arguments"]!.GetValue<string>());
        T.Eq("environment names only", "API_KEY,MODE", string.Join(",", p["environmentNames"]!.AsArray().Select(n => n!.GetValue<string>())));
        T.Eq("created by the agent", "agent", job["created"]!["byKind"]!.GetValue<string>());
        T.Eq("created by name", "Cadence Test Agent", job["created"]!["by"]!.GetValue<string>());
        T.Eq("updated by the agent", "Cadence Test Agent", job["updated"]!["by"]!.GetValue<string>());
        T.Eq("not locked", false, job["lockedForAgents"]!.GetValue<bool>());
        T.True("no environment value in the text or the structure", !res.Text.Contains(WriteRig.Secret, StringComparison.Ordinal) && !r.ToJsonString().Contains(WriteRig.Secret, StringComparison.Ordinal));

        string id = job["jobId"]!.GetValue<string>();
        T.Eq("exactly one write: a create of the new name", "register Backup notes", string.Join("|", rig.Fake.Writes));
        T.Eq("TASK_CREATE", RegisterMode.Create, rig.Fake.Modes.Single());
        CadenceMetadata meta = rig.Live(id).Metadata!;
        T.Eq("metadata createdBy", "agent:Cadence Test Agent", meta.CreatedBy);
        T.Eq("metadata updatedBy", "agent:Cadence Test Agent", meta.UpdatedBy);
        T.True("the metadata holds no lock", !meta.Job.AgentLock);
        T.Eq("the anchor is today (the injected clock)", new DateOnly(2026, 10, 1), meta.Job.Schedule.Anchor);

        T.Eq("one audit line", 1, rig.SaveAudit.Count);
        AuditEntry a = rig.SaveAudit[0];
        T.True("ok, created, with the id, the job name, the client", a.Outcome == "ok" && a.Code == "created" && a.JobId == id && a.JobName == "Backup notes" && a.Client == "Cadence Test Agent" && a.Session == "abcd1234" && a.ClientVersion == "1");
        T.True("the audit line has neither the secret nor the arguments", !AuditText(rig).Contains(WriteRig.Secret, StringComparison.Ordinal) && !AuditText(rig).Contains("ARGMARK-1", StringComparison.Ordinal));
    }

    static string AuditText(WriteRig rig) => string.Join("\n", rig.Audit.All.Select(e => string.Join("|", e.Client, e.Tool, e.JobId, e.JobName, e.Outcome, e.Code, e.Detail)));

    public static void CreateOptions()
    {
        using var rig = new WriteRig();
        JsonObject a = rig.SaveOk(WriteRig.Create("Visible", extraProgram: ",\"hidden\":false,\"timeoutMinutes\":0,\"outputEncoding\":\"utf8\",\"workingFolder\":\"C:\\\\Data\"") .Replace("\"schedule\":{\"cron\":[\"0 18 * * 1-5\"]}", "\"schedule\":{\"cron\":[\"0 18 * * 1-5\"],\"skipHolidays\":\"nyse\"},\"enabled\":false"));
        JsonObject p = a["job"]!["program"]!.AsObject();
        T.Eq("hidden false", false, p["hidden"]!.GetValue<bool>());
        T.Eq("timeout 0 means no limit", 0, p["timeoutMinutes"]!.GetValue<int>());
        T.Eq("utf8", "utf8", p["outputEncoding"]!.GetValue<string>());
        T.Eq("the working folder given", @"C:\Data", p["workingFolder"]!.GetValue<string>());
        T.Eq("disabled", false, a["job"]!["enabled"]!.GetValue<bool>());
        T.Eq("holidays", "nyse", a["job"]!["schedule"]!["skipHolidays"]!.GetValue<string>());
        T.True("a visible job warns that nothing is captured", a["warnings"]!.AsArray().Any(w => w!.GetValue<string>().Contains("not captured", StringComparison.Ordinal)));
        T.Eq("a disabled job has no next runs", 0, a["job"]!["nextRuns"]!.AsArray().Count);

        JsonObject t = rig.SaveOk("{\"name\":\"From template\",\"schedule\":{\"templateId\":\"weekdays-6pm\"},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}");
        T.Eq("a template's cron is used", "Every weekday at 18:00", t["job"]!["schedule"]!["description"]!.GetValue<string>());

        JsonObject w = rig.SaveOk(WriteRig.Create("Percent", extraProgram: ",\"arguments\":\"%TEMP%\\\\x\""));
        T.True("a %NAME% warns that it is expanded at run time", w["warnings"]!.AsArray().Any(x => x!.GetValue<string>().Contains("%NAME%", StringComparison.Ordinal)));
        T.Eq("the argument is stored as written", "%TEMP%\\x", w["job"]!["program"]!["arguments"]!.GetValue<string>());

        JsonObject logon = rig.SaveOk(WriteRig.Create("At logon", cron: "@logon"));
        T.True("@logon warns", logon["warnings"]!.AsArray().Any(x => x!.GetValue<string>().Contains("signs in", StringComparison.Ordinal)));
    }

    // ---- suspension: both checks ----

    public static void SuspendedRefusesAndWritesNothing()
    {
        using var rig = new WriteRig();
        rig.Policy.SetSuspended(true, "app", WriteRig.FixedNow.AddMinutes(-5));
        ToolResult r = rig.Save(WriteRig.Create("Nope"));
        T.True("refused", r.IsError);
        T.Eq("code", "suspended", WriteRig.Code(r));
        T.True("message", WriteRig.Msg(r).StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), WriteRig.Msg(r));
        T.True("says nothing was changed", WriteRig.Msg(r).Contains("Nothing was changed", StringComparison.Ordinal));
        T.Eq("nothing written", 0, rig.Fake.Writes.Count);
        T.True("audited once as refused/suspended", rig.SaveAudit is [{ Outcome: "refused", Code: "suspended" }]);

        rig.Policy.SetSuspended(false, "app", WriteRig.FixedNow);
        T.True("works again when resumed (no restart)", !rig.Save(WriteRig.Create("Yes")).IsError);

        rig.Policy.Fault = "the key is unreadable";
        T.Eq("an unreadable policy refuses too (fails closed)", "suspended", WriteRig.Code(rig.Save(WriteRig.Create("Unreadable"))));
    }

    public static void TheSecondSuspensionCheckRunsRightBeforeTheWrite()
    {
        FlipReader? flip = null;
        using var rig = new WriteRig(store => flip = new FlipReader(store, flipAtRead: 2));
        ToolResult r = rig.Save(WriteRig.Create("Raced"));
        T.True("refused at the second check", r.IsError && WriteRig.Code(r) == "suspended", r.Text);
        T.Eq("the policy was read twice (check 1, check 2)", 2, flip!.Reads);
        T.Eq("nothing was registered", 0, rig.Fake.Writes.Count);
        T.Eq("no job exists", 0, rig.Jobs().Load(false).Count);
        T.True("audited exactly once", rig.SaveAudit is [{ Outcome: "refused", Code: "suspended" }]);
    }

    // ---- gate 1: arguments ----

    public static void EveryArgumentErrorIsInvalidArgumentWithItsFieldNamed()
    {
        using var rig = new WriteRig();
        string ok = "\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}";
        (string Json, string Contains)[] cases =
        [
            ("{" + ok + "}", "name is required"),
            ("{\"name\":\"x\",\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule is required"),
            ("{\"name\":\"x\",\"schedule\":{\"skipHolidays\":\"nyse\"},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule is required"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]}}", "program.path is required"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"arguments\":\"a\"}}", "program.path is required"),
            ("{\"name\":\"x\"," + ok + ",\"expectedRevision\":\"0123456789ab\"}", "expectedRevision is only for changing"),
            ("{\"jobId\":\"abcd1234\",\"name\":\"x\"}", "expectedRevision is required"),
            ("{\"jobId\":\"ABCD1234\",\"expectedRevision\":\"0123456789ab\"}", "jobId"),
            ("{\"jobId\":\"abcd1234\\n\",\"expectedRevision\":\"0123456789ab\"}", "jobId"),
            ("{\"jobId\":\"abcd1234\",\"expectedRevision\":\"0123456789AB\"}", "expectedRevision"),
            ("{\"name\":\"x\"," + ok + ",\"agentLock\":false}", "Unknown property 'agentLock'"),
            ("{\"name\":\"x\"," + ok + ",\"lockedForAgents\":false}", "Unknown property"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"],\"templateId\":\"hourly\"},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "either cron or templateId"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"],\"x\":1},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule.x"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"agentLock\":true}}", "program.agentLock"),
            ("{\"name\":\"x\",\"schedule\":{\"templateId\":\"nope\"},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule.templateId must be one of"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule.cron must have 1 to 10 items"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\",\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule.cron must have 1 to 10 items"),
            ("{\"name\":\"\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "name must be"),
            ("{\"name\":\"   \"," + ok + "}", "name must contain at least one visible character"),
            ("{\"name\":\"" + new string('a', 101) + "\"," + ok + "}", "name must be"),
            ("{\"name\":7," + ok + "}", "name must be a string"),
            ("{\"name\":null," + ok + "}", "name must not be null"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"timeoutMinutes\":34561}}", "program.timeoutMinutes must be a whole number from 0 to 34560"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"timeoutMinutes\":-1}}", "program.timeoutMinutes"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"timeoutMinutes\":1.5}}", "program.timeoutMinutes"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"hidden\":\"yes\"}}", "program.hidden must be true or false"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"outputEncoding\":\"latin1\"}}", "program.outputEncoding must be one of"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":[]}}", "program.environment must be an object"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"A\":1}}}", "program.environment entry number 1 must have a text value"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"abc\"}}", "program.path must be 4 to 1024"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"arguments\":\"" + new string('a', 8001) + "\"}}", "program.arguments must be at most 8000"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"" + new string('a', 1025) + "\"}}", "program.workingFolder must be at most 1024"),
            ("{\"name\":\"x\",\"schedule\":\"0 18 * * *\",\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\"}}", "schedule must be an object"),
            ("{\"name\":\"x\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":\"cmd\"}", "program must be an object"),
            ("{\"name\":\"x\"," + ok + ",\"enabled\":\"true\"}", "enabled must be true or false"),
        ];
        foreach ((string json, string contains) in cases)
        {
            ToolResult r = rig.Save(json);
            T.True("refused as invalid_argument: " + contains, r.IsError && WriteRig.Code(r) == "invalid_argument" && WriteRig.Msg(r).Contains(contains, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }
        T.Eq("nothing written by any of them", 0, rig.Fake.Writes.Count);
        T.Eq("each refusal is exactly one audit line", cases.Length, rig.SaveAudit.Count);
        T.True("all audited as refused/invalid_argument", rig.SaveAudit.All(a => a.Outcome == "refused" && a.Code == "invalid_argument"));
        T.True("an arguments value that is not an object", rig.Host.Call("cadence_save_job", JsonDocument.Parse("[1]").RootElement, WriteRig.Context) is { IsError: true } bad && WriteRig.Code(bad) == "invalid_argument");
    }

    // ---- program rules ----

    public static void ProgramRulesRefuseWithTheirMessage()
    {
        using var rig = new WriteRig();
        (string Program, string Starts)[] cases =
        [
            ("{\"path\":\"python.exe\"}", "Task Scheduler does not use your PATH. Give the full path of the program: the program on your PATH is 'C:\\Python\\python.exe'."),
            ("{\"path\":\"node.exe\"}", "Task Scheduler does not use your PATH. Give the full path of the program, for example C:\\Windows\\System32\\cmd.exe."),
            ("{\"path\":\"tools\\\\job.exe\"}", "The program path must be a full local path"),
            ("{\"path\":\".\\\\job.exe\"}", "The program path must be a full local path"),
            ("{\"path\":\"\\\\\\\\server\\\\share\\\\job.exe\"}", "Network paths"),
            ("{\"path\":\"//server/share/job.exe\"}", "Network paths"),
            ("{\"path\":\"\\\\\\\\?\\\\C:\\\\Tools\\\\job.exe\"}", "Device paths"),
            ("{\"path\":\"\\\\\\\\.\\\\C:\\\\Tools\\\\job.exe\"}", "Device paths"),
            ("{\"path\":\"C:\\\\Tools\\\\missing.exe\"}", "The program 'C:\\Tools\\missing.exe' does not exist on this PC."),
            ("{\"path\":\"C:\\\\Tools\\\\run.bat\"}", "A script can't be the program itself. Use %SystemRoot%\\System32\\cmd.exe with arguments /d /s /c \"\"C:\\Tools\\run.bat\"\"."),
            ("{\"path\":\"C:\\\\Tools\\\\run.ps1\"}", "A script can't be the program itself. Use %SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe with arguments -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\Tools\\run.ps1\"."),
            ("{\"path\":\"C:\\\\Tools\\\\x.py\"}", "A script can't be the program itself."),
            ("{\"path\":\"C:\\\\Tools\\\\x.vbs\"}", "A script can't be the program itself."),
            ("{\"path\":\"C:\\\\Tools\\\\tool.lnk\"}", "A shortcut (.lnk) can't be used."),
            ("{\"path\":\"C:\\\\Tools\\\\data.txt\"}", "The program must be an .exe or .com file; '.txt' is not allowed."),
            ("{\"path\":\"C:\\\\Tools\\\\noext\"}", "The program must be an .exe or .com file; a file without an extension is not allowed."),
            ("{\"path\":\"C:\\\\Tools\\\\\\\"job.exe\"}", "The program path can't contain a quote character"),
            ("{\"path\":\"C:\\\\Tools\\\\job\\u0000.exe\"}", "The program path contains a control character"),
            ("{\"path\":\"C:\\\\Tools\\\\job\\n.exe\"}", "The program path contains a control character"),
            ("{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c echo a\\nb\"}", "The arguments can't contain line breaks"),
            ("{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c echo a\\rb\"}", "The arguments can't contain line breaks"),
            ("{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"a\\u0000b\"}", "The arguments contain a NUL character"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"A=B\":\"x\"}}", "Environment variable name number 1 is not valid"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"\":\"x\"}}", "Environment variable name number 1 is not valid"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"1A\":\"x\"}}", "Environment variable name number 1 is not valid"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"Path\":\"x\",\"PATH\":\"y\"}}", "The environment variable PATH is given twice"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"A\":\"" + new string('v', 2001) + "\"}}", "The value of the environment variable A is longer than 2000 characters."),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"A\":\"a\\u0000b\"}}", "The value of the environment variable A contains a NUL character."),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"C:\\\\Nowhere\"}", "The working folder 'C:\\Nowhere' does not exist on this PC."),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"tools\"}", "The working folder must be a full local folder path"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"\\\\\\\\server\\\\share\"}", "The working folder must be a full local folder path"),
            ("{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"C:\\\\Data\\\"\"}", "The working folder can't contain quotes or control characters."),
        ];
        foreach ((string program, string starts) in cases)
        {
            rig.Now = rig.Now.AddSeconds(5);   // 35 refused calls in a row would meet the rate limit: let the window slide
            ToolResult r = rig.Save("{\"name\":\"P\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":" + program + "}");
            T.True("program_rejected: " + starts.Split('.')[0], r.IsError && WriteRig.Code(r) == "program_rejected" && WriteRig.Msg(r).StartsWith(starts, StringComparison.Ordinal), WriteRig.Code(r) + ": " + WriteRig.Msg(r));
        }

        // 31 entries
        string env31 = string.Join(",", Enumerable.Range(0, 31).Select(i => "\"V" + i + "\":\"x\""));
        ToolResult many = rig.Save("{\"name\":\"P\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{" + env31 + "}}}");
        T.True("31 environment entries: program_rejected", WriteRig.Code(many) == "program_rejected" && WriteRig.Msg(many).StartsWith("At most 30 environment variables are allowed, but 31 were given.", StringComparison.Ordinal), WriteRig.Msg(many));
        string env30 = string.Join(",", Enumerable.Range(0, 30).Select(i => "\"V" + i + "\":\"x\""));
        T.True("30 entries are fine", !rig.Save("{\"name\":\"P30\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{" + env30 + "}}}").IsError);

        T.Eq("only the 30-entry job was written", 1, rig.Registers);
        T.True("a rejected program is audited as refused/program_rejected, once each", rig.SaveAudit.Count(a => a.Code == "program_rejected") == cases.Length + 1);
    }

    public static void TheEnvironmentIsNeverEchoedInAnyRefusalOrAuditLine()
    {
        using var rig = new WriteRig();
        string longValue = WriteRig.Secret + new string('v', 2100);
        ToolResult r = rig.Save("{\"name\":\"S\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"TOKEN\":\"" + longValue + "\"}}}");
        T.True("refused", WriteRig.Code(r) == "program_rejected");
        T.True("the refusal names the variable but not its value", WriteRig.Msg(r).Contains("TOKEN", StringComparison.Ordinal) && !r.Text.Contains(WriteRig.Secret, StringComparison.Ordinal) && !r.Structured!.ToJsonString().Contains(WriteRig.Secret, StringComparison.Ordinal));
        T.True("nor does the audit line", !AuditText(rig).Contains(WriteRig.Secret, StringComparison.Ordinal));

        ToolResult dup = rig.Save("{\"name\":\"S\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"A\":\"" + WriteRig.Secret + "\",\"a\":\"" + WriteRig.Secret + "\"}}}");
        T.True("a duplicate name does not echo a value", WriteRig.Code(dup) == "program_rejected" && !dup.Text.Contains(WriteRig.Secret, StringComparison.Ordinal));
        ToolResult badName = rig.Save("{\"name\":\"S\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"environment\":{\"" + WriteRig.Secret + "=\":\"x\"}}}");
        T.True("a bad name is described by its position, not echoed", WriteRig.Code(badName) == "program_rejected" && !badName.Text.Contains(WriteRig.Secret, StringComparison.Ordinal) && !AuditText(rig).Contains(WriteRig.Secret, StringComparison.Ordinal));
    }

    public static void AgentsCannotScheduleCadenceItself()
    {
        using var rig = new WriteRig();
        const string head = "{\"name\":\"C\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":";
        string msg = "Agents can't schedule Cadence itself or change its settings.";
        string[] programs =
        [
            "{\"path\":\"C:\\\\Apps\\\\Cadence\\\\Cadence.exe\"}",                                             // the running exe
            "{\"path\":\"C:\\\\x\\\\Cadence-standalone.exe\"}",
            "{\"path\":\"C:\\\\Tools\\\\Cadence2.exe\"}",                                                      // any Cadence*.exe
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c Cadence.exe --agents resume --yes\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c C:\\\\x\\\\Cad^ence.exe --ag^ents resume\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c start Cadence-standalone.exe\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c reg add HKCU\\\\Software\\\\Cadence\\\\Agents /v Suspended /d 0\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c x --MCP\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c %CADENCE_TEST_X%\"}",
            "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c echo\",\"environment\":{\"CMD\":\"cadence.exe --agents resume --yes\"}}",
        ];
        Environment.SetEnvironmentVariable("CADENCE_TEST_X", "Cadence.exe --agents resume");
        try
        {
            foreach (string p in programs)
            {
                ToolResult r = rig.Save(head + p + "}");
                T.True("program_rejected: " + p[..Math.Min(100, p.Length)], r.IsError && WriteRig.Code(r) == "program_rejected" && WriteRig.Msg(r) == msg, WriteRig.Code(r) + ": " + WriteRig.Msg(r));
            }
        }
        finally { Environment.SetEnvironmentVariable("CADENCE_TEST_X", null); }
        T.Eq("nothing written", 0, rig.Fake.Writes.Count);

        // Harmless commands that merely look similar are fine.
        T.True("echo Cadence is great is fine", !rig.Save(head + "{\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\",\"arguments\":\"/c echo Cadence is great\"}}").IsError);

        // The rule also protects an update: the merged program is checked even when only the arguments are given.
        ClassifiedTask owner = rig.OwnerSave(WriteRig.Job("Owner", "own00001"));
        ToolResult upd = rig.Save("{\"jobId\":\"own00001\",\"expectedRevision\":\"" + JobRevision.Of(owner) + "\",\"program\":{\"arguments\":\"--agents resume\"}}");
        T.True("an update that adds --agents is refused", WriteRig.Code(upd) == "program_rejected" && WriteRig.Msg(upd) == msg);
    }

    // ---- schedule rules ----

    public static void InvalidSchedulesAreRefusedWithDiagnosticsAndAlternatives()
    {
        using var rig = new WriteRig();
        ToolResult reboot = rig.Save(WriteRig.Create("R", cron: "@reboot"));
        T.True("@reboot: schedule_invalid", reboot.IsError && WriteRig.Code(reboot) == "schedule_invalid", reboot.Text);
        T.True("the message starts as the plan says and points at the preview", WriteRig.Msg(reboot).StartsWith("The schedule can't be used.", StringComparison.Ordinal) && WriteRig.Msg(reboot).Contains("cadence_preview_schedule", StringComparison.Ordinal));
        JsonObject err = reboot.Structured!["error"]!.AsObject();
        T.Eq("closestAlternative is @logon", "@logon", err["closestAlternative"]![0]!.GetValue<string>());
        T.True("diagnostics are included", err["diagnostics"]!.AsArray().Count > 0);

        ToolResult bad = rig.Save(WriteRig.Create("B", cron: "61 * * * *"));
        T.True("a bad minute", WriteRig.Code(bad) == "schedule_invalid");
        // more than 48 triggers
        ToolResult over = rig.Save(WriteRig.Create("Many", cron: "0,1,3,7,12,20,33,50 1,2,4,5,9,10,13 * * *"));
        T.True("a schedule that needs 56 triggers is refused with the compiler's text", WriteRig.Code(over) == "schedule_invalid" && WriteRig.Msg(over).Contains("needs 56 triggers; Task Scheduler allows 48", StringComparison.Ordinal), WriteRig.Msg(over));
        T.Eq("nothing written by the invalid ones", 0, rig.Fake.Writes.Count);
        T.Eq("every invalid schedule is exactly one audit line", 3, rig.SaveAudit.Count(a => a is { Outcome: "refused", Code: "schedule_invalid" }));
    }

    public static void TheMinimumIntervalIsEnforcedAtItsBoundary()
    {
        using var rig = new WriteRig();
        T.Eq("every minute is refused", "schedule_too_frequent", WriteRig.Code(rig.Save(WriteRig.Create("Every minute", cron: "* * * * *"))));
        ToolResult r = rig.Save(WriteRig.Create("Every minute", cron: "* * * * *"));
        T.True("message", WriteRig.Msg(r).StartsWith("This schedule runs every 1 minute; agents may schedule at most every 5 minutes.", StringComparison.Ordinal), WriteRig.Msg(r));
        T.True("message names the owner's way out", WriteRig.Msg(r).Contains("--agents config --min-interval N", StringComparison.Ordinal));
        T.Eq("every 4 minutes is refused", "schedule_too_frequent", WriteRig.Code(rig.Save(WriteRig.Create("Four", cron: "*/4 * * * *"))));
        T.Eq("0,1 9 * * * (a one minute gap) is refused", "schedule_too_frequent", WriteRig.Code(rig.Save(WriteRig.Create("Pair", cron: "0,1 9 * * *"))));
        T.True("every 5 minutes is allowed (exactly the limit)", !rig.Save(WriteRig.Create("Five", cron: "*/5 * * * *")).IsError);
        T.True("every 15 minutes is allowed", !rig.Save(WriteRig.Create("Fifteen", cron: "*/15 * * * *")).IsError);
        T.True("a yearly schedule has no gap and is allowed", !rig.Save(WriteRig.Create("Yearly", cron: "0 9 1 1 *")).IsError);

        rig.Policy.SetLimits(10, null);
        T.Eq("limit 10: every 5 minutes is now refused", "schedule_too_frequent", WriteRig.Code(rig.Save(WriteRig.Create("Five again", cron: "*/5 * * * *"))));
        T.True("limit 10: every 10 minutes is allowed", !rig.Save(WriteRig.Create("Ten", cron: "*/10 * * * *")).IsError);
        T.True("the limit is read live: the message names 10", WriteRig.Msg(rig.Save(WriteRig.Create("Five again", cron: "*/5 * * * *"))).Contains("at most every 10 minutes", StringComparison.Ordinal));
    }

    public static void TheMaximumNumberOfAgentJobsIsEnforcedAtItsBoundary()
    {
        using var rig = new WriteRig();
        rig.Policy.SetLimits(null, 2);
        rig.OwnerSave(WriteRig.Job("Owner job", "own00001"));   // an owner's job does not count
        T.True("first agent job", !rig.Save(WriteRig.Create("A1")).IsError);
        T.True("second agent job (19 of 20 style: one below the cap)", !rig.Save(WriteRig.Create("A2")).IsError);
        ToolResult third = rig.Save(WriteRig.Create("A3"));
        T.True("third is limit_reached", third.IsError && WriteRig.Code(third) == "limit_reached", third.Text);
        T.True("message", WriteRig.Msg(third).StartsWith("Not created: agents may create at most 2 jobs and 2 already exist.", StringComparison.Ordinal) && WriteRig.Msg(third).Contains("--agents config --max-jobs N", StringComparison.Ordinal), WriteRig.Msg(third));
        T.True("a retried create of an existing job still succeeds at the cap (unchanged)", rig.Save(WriteRig.Create("A2")).Structured!["outcome"]!.GetValue<string>() == "unchanged");
        T.True("changing an existing job is not capped", !rig.Save("{\"jobId\":\"" + Id(rig.SaveOk(WriteRig.Create("A1"))) + "\",\"expectedRevision\":\"" + Rev(rig.SaveOk(WriteRig.Create("A1"))) + "\",\"program\":{\"arguments\":\"--x\"}}").IsError);
        rig.Policy.SetLimits(null, 0);
        T.True("a cap of 0 forbids creating", rig.Save(WriteRig.Create("A4")).Structured!["error"]!["message"]!.GetValue<string>().Contains("agents may not create jobs on this PC right now", StringComparison.Ordinal));
        T.Eq("two agent jobs and the owner's job exist", 3, rig.Jobs().Load(false).Count);
    }

    // ---- names and idempotent create ----

    public static void NamesAreCheckedAndAnIdenticalRetryIsUnchanged()
    {
        using var rig = new WriteRig();
        JsonObject first = rig.SaveOk(WriteRig.Create("Backup notes", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"1\"}"));
        string id = Id(first);
        int writes = rig.Fake.Writes.Count;

        // Same name, same settings (even in another case and with blanks): a retry.
        ToolResult again = rig.Save(WriteRig.Create("  backup NOTES ", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"1\"}"));
        T.True("identical retry is a success", !again.IsError, again.Text);
        T.Eq("outcome unchanged", "unchanged", again.Structured!["outcome"]!.GetValue<string>());
        T.Eq("it is the same job", id, Id(again.Structured!));
        T.Eq("the plan's note", "A job with this name and these settings already exists; nothing was created.", again.Structured!["note"]!.GetValue<string>());
        T.Eq("no register call", writes, rig.Fake.Writes.Count);
        T.Eq("still one job", 1, rig.Jobs().Load(false).Count);

        // Same name, different settings: name_taken with the id.
        foreach (string different in new[]
        {
            WriteRig.Create("Backup notes", extraProgram: ",\"arguments\":\"--other\",\"environment\":{\"A\":\"1\"}"),
            WriteRig.Create("Backup notes", cron: "0 19 * * 1-5", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"1\"}"),
            WriteRig.Create("Backup notes", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"2\"}"),
            WriteRig.Create("Backup notes", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"1\"},\"hidden\":false"),
            WriteRig.Create("Backup notes", path: @"C:\Tools\backup.exe", extraProgram: ",\"arguments\":\"--go\",\"environment\":{\"A\":\"1\"}"),
        })
        {
            ToolResult clash = rig.Save(different);
            T.True("name_taken", clash.IsError && WriteRig.Code(clash) == "name_taken", clash.Text);
            T.True("it names the job and its id and says what to do", WriteRig.Msg(clash).Contains("(jobId " + id + ")", StringComparison.Ordinal) && WriteRig.Msg(clash).Contains("cadence_get_job", StringComparison.Ordinal));
        }
        T.Eq("nothing was written by the clashes", writes, rig.Fake.Writes.Count);

        // A name used by the owner's job counts as well.
        rig.OwnerSave(WriteRig.Job("Owner job", "own00001"));
        T.Eq("owner's name is taken", "name_taken", WriteRig.Code(rig.Save(WriteRig.Create("owner job"))));

        // Names: punctuation only, over-long and blank.
        T.True("only punctuation is refused", WriteRig.Code(rig.Save(WriteRig.Create("!!! ???"))) == "invalid_argument" && WriteRig.Msg(rig.Save(WriteRig.Create("..."))).Contains("at least one letter or digit", StringComparison.Ordinal));
        T.True("a name of 100 characters is fine", !rig.Save(WriteRig.Create(new string('n', 100))).IsError);
        // Two names that sanitize to the same task name are different jobs: the task gets a suffix.
        T.True("Backup! is fine", !rig.Save(WriteRig.Create("Backup!")).IsError);
        JsonObject q = rig.SaveOk(WriteRig.Create("Backup?"));
        T.Eq("the second one gets a suffixed task name", "\\" + rig.Folder + "\\Backup- (2)", q["job"]!["taskPath"]!.GetValue<string>());
        T.Eq("every call above was audited exactly once", 13, rig.SaveAudit.Count);
    }

    public static void ARawTaskWithTheSameNameIsNeverOverwritten()
    {
        using var rig = new WriteRig();
        string foreign = PlanSamples.Plain();
        rig.Fake.Seed("Backup notes", foreign);
        JsonObject r = rig.SaveOk(WriteRig.Create("Backup notes"));
        T.Eq("the agent's job gets a (2) task name", "\\" + rig.Folder + "\\Backup notes (2)", r["job"]!["taskPath"]!.GetValue<string>());
        T.Eq("the foreign task is byte-identical", foreign, rig.Fake.XmlOf("Backup notes"));
        T.Eq("exactly one write, to the new name", "register Backup notes (2)", string.Join("|", rig.Fake.Writes));
        T.True("the foreign task is not a Cadence job, so it is not in the name check", rig.Jobs().Load(false).Count == 1);
    }

    public static void TwoCreatesOfTheSameNameCannotBothSucceed()
    {
        WriteRig? holder = null;
        using var rig = new WriteRig(store => new FlipReader(store, 2, () => holder!.OwnerSave(WriteRig.Job("Race", "own00001"))));
        holder = rig;
        ToolResult r = rig.Save(WriteRig.Create("Race"));
        T.True("the second creator is refused inside the lock", r.IsError && WriteRig.Code(r) == "name_taken", r.Text);
        T.Eq("only the competitor's job exists", 1, rig.Jobs().Load(false).Count);
        T.Eq("the agent registered nothing (the one write is the competitor's)", 1, rig.Registers);
        T.True("audited once as refused/name_taken", rig.SaveAudit is [{ Outcome: "refused", Code: "name_taken" }]);
    }

    // ---- rate limit, busy, audit failures ----

    public static void TheThirtyFirstWriteInAMinuteIsRateLimited()
    {
        using var rig = new WriteRig();
        string create = WriteRig.Create("One");
        T.True("the first call creates", !rig.Save(create).IsError);
        for (int i = 2; i <= 30; i++)
        {
            ToolResult r = rig.Save(create);
            if (r.IsError) { T.True("call " + i + " is not limited", false, r.Text); break; }
        }
        ToolResult limited = rig.Save(create);
        T.True("the 31st is rate_limited", limited.IsError && WriteRig.Code(limited) == "rate_limited", limited.Text);
        T.True("it says how long to wait", Regex.IsMatch(WriteRig.Msg(limited), "Wait \\d+ seconds and try again\\."), WriteRig.Msg(limited));
        T.True("and that nothing was changed", WriteRig.Msg(limited).Contains("Nothing was changed", StringComparison.Ordinal));
        T.True("audited as refused/rate_limited", rig.SaveAudit.Last() is { Outcome: "refused", Code: "rate_limited" });
        rig.Now = rig.Now.AddSeconds(61);
        T.True("after the window it works again", !rig.Save(create).IsError);
        T.Eq("one job in all", 1, rig.Jobs().Load(false).Count);
    }

    public static void ABusyWriterLockReturnsARetryHint()
    {
        using var rig = new WriteRig(lockTimeout: TimeSpan.FromMilliseconds(300));
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using Cadence.Scheduler.Apply.WriterLock? l = Cadence.Scheduler.Apply.WriterLock.TryAcquire(rig.Folder, TimeSpan.FromSeconds(5));
            held.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }) { IsBackground = true };
        holder.Start();
        T.True("the other writer holds the lock", held.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            ToolResult r = rig.Save(WriteRig.Create("Waiting"));
            T.True("busy", r.IsError && WriteRig.Code(r) == "busy", r.Text);
            T.True("message and hint", WriteRig.Msg(r).StartsWith("Another Cadence window or agent is saving right now", StringComparison.Ordinal) && r.Structured!["error"]!["hint"]!.GetValue<string>().Contains("again in a few seconds", StringComparison.Ordinal));
            T.Eq("nothing written", 0, rig.Fake.Writes.Count);
            T.True("audited as refused/busy", rig.SaveAudit is [{ Outcome: "refused", Code: "busy" }]);
        }
        finally { release.Set(); holder.Join(5000); }
        T.True("works once the lock is free", !rig.Save(WriteRig.Create("Waiting")).IsError);
    }

    public static void ABrokenAuditLogNeverFailsTheCallButIsReported()
    {
        using var rig = new WriteRig();
        rig.Audit.ThrowOnAppend = new IOException("disk full");
        ToolResult ok = rig.Save(WriteRig.Create("Logged"));
        T.True("the save still succeeds", !ok.IsError, ok.Text);
        T.True("with the warning", ok.Structured!["warnings"]!.AsArray().Any(w => w!.GetValue<string>().StartsWith("The agent log could not be written: ", StringComparison.Ordinal)));
        ToolResult refused = rig.Save(WriteRig.Create("Bad", path: "python.exe"));
        T.True("a refusal still refuses, with the warning", WriteRig.Code(refused) == "program_rejected" && refused.Structured!["warnings"]!.AsArray().Count == 1);
    }

    // ---- the catalogue ----

    public static void TheSaveToolIsOfferedOnlyWithAWriter()
    {
        T.True("a read-only environment offers no write tool", !ReadRig.Bare().Has("cadence_save_job"));
        using var rig = new WriteRig();
        T.True("with a writer the host offers the four write tools, after the read tools", rig.Host.Tools.Select(t => t.Name).SequenceEqual(["cadence_get_status", "cadence_list_jobs", "cadence_get_job", "cadence_get_run_output", "cadence_preview_schedule", "cadence_list_templates", "cadence_save_job", "cadence_set_job_enabled", "cadence_run_job_now", "cadence_delete_job"]));
        using var withSwitch = new WriteRig(withSwitch: true);
        T.True("and before the two control tools", withSwitch.Host.Tools.Select(t => t.Name).Skip(6).SequenceEqual(["cadence_save_job", "cadence_set_job_enabled", "cadence_run_job_now", "cadence_delete_job", "cadence_suspend_agents", "cadence_resume_agents"]));
        ToolDefinition save = rig.Host.Tools.Single(t => t.Name == "cadence_save_job");
        T.True("annotations: not read-only, destructive, idempotent, closed world", save.Annotations is { ReadOnly: false, Destructive: true, Idempotent: true, OpenWorld: false });
        T.Eq("title", "Create or change a schedule", save.Annotations.Title);
        T.True("IsWriteTool", rig.Host.IsWriteTool("cadence_save_job") && !rig.Host.IsWriteTool("cadence_get_job"));
    }

    static string[]? PlanLines()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PLAN.md"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? null : File.ReadAllLines(Path.Combine(dir, "PLAN.md"));
    }

    public static void TheSaveSchemaAndDescriptionEqualThePlan()
    {
        string[]? plan = PlanLines();
        if (plan is null) { T.True("PLAN.md is not in this checkout (the design log is kept locally, not in the repo): comparison with the plan skipped", true); return; }
        int start = Array.FindIndex(plan, l => l.Trim() == "**`cadence_save_job`**");
        T.True("the tool is in PLAN 12.3.2", start >= 0);
        if (start < 0) return;
        string? desc = null, schema = null;
        bool inJson = false;
        var json = new List<string>();
        for (int i = start + 1; i < plan.Length && !plan[i].StartsWith("**`", StringComparison.Ordinal) && !plan[i].StartsWith("#", StringComparison.Ordinal); i++)
        {
            if (plan[i].StartsWith("- Description", StringComparison.Ordinal)) { int a = plan[i].IndexOf('`', StringComparison.Ordinal); int b = plan[i].LastIndexOf('`'); desc = plan[i][(a + 1)..b]; }
            if (plan[i].StartsWith("```json", StringComparison.Ordinal)) { inJson = true; continue; }
            if (inJson && plan[i].StartsWith("```", StringComparison.Ordinal)) { inJson = false; schema = string.Join("\n", json); break; }
            if (inJson) json.Add(plan[i]);
        }
        T.True("description found", desc is not null);
        T.Eq("description equals PLAN 12.3.2", desc!.Replace("\\\"", "\"", StringComparison.Ordinal), ToolCatalog.SaveJobDescription);
        T.True("schema found", schema is not null);
        string ids = string.Join(",", Cadence.Core.Text.Templates.All.Select(t => "\"" + t.Id + "\""));
        JsonNode expected = JsonNode.Parse(schema!.Replace("\"<generated from Templates.All>\"", ids, StringComparison.Ordinal))!;
        T.True("inputSchema equals PLAN 12.3.2 (template ids generated)", JsonNode.DeepEquals(expected, JsonNode.Parse(ToolCatalog.SaveJobSchema)));
    }

    public static void TheSaveSchemaIsValidAndEveryPropertyIsAcceptedByTheParser()
    {
        JsonObject s = JsonNode.Parse(ToolCatalog.SaveJobSchema)!.AsObject();
        T.Eq("object", "object", s["type"]!.GetValue<string>());
        T.Eq("no additional properties", false, s["additionalProperties"]!.GetValue<bool>());
        T.True("nothing is required by the schema (create/update rules are the tool's)", s["required"] is null);
        string[] top = [.. s["properties"]!.AsObject().Select(p => p.Key)];
        T.True("top-level properties", top.SequenceEqual(["jobId", "expectedRevision", "name", "schedule", "program", "enabled"]));
        T.True("schedule properties", s["properties"]!["schedule"]!["properties"]!.AsObject().Select(p => p.Key).SequenceEqual(["cron", "templateId", "skipHolidays"]));
        T.True("program properties", s["properties"]!["program"]!["properties"]!.AsObject().Select(p => p.Key).SequenceEqual(["path", "arguments", "workingFolder", "environment", "timeoutMinutes", "hidden", "outputEncoding"]));
        T.Eq("jobId pattern equals the parser's", "^[a-z0-9]{8}$", s["properties"]!["jobId"]!["pattern"]!.GetValue<string>());
        T.Eq("revision pattern equals the parser's", "^[0-9a-f]{12}$", s["properties"]!["expectedRevision"]!["pattern"]!.GetValue<string>());
        T.Eq("timeout maximum is the metadata cap in minutes", MetadataCodec.MaxTimeoutSeconds / 60, s["properties"]!["program"]!["properties"]!["timeoutMinutes"]!["maximum"]!.GetValue<int>());
        T.True("the template enum is Templates.All", s["properties"]!["schedule"]!["properties"]!["templateId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(Cadence.Core.Text.Templates.All.Select(t => t.Id)));
        // The server accepts the tool list (every schema is parsed at construction).
        using var rig = new WriteRig();
        _ = new McpServer(Cadence.Agent.McpHost.ServerInfo("1"), rig.Host, TextWriter.Null);

        // Every property of the schema is accepted by the parser: one call that sets them all.
        JsonObject all = rig.SaveOk("{\"name\":\"Everything\",\"schedule\":{\"cron\":[\"0 18 * * 1-5\"],\"skipHolidays\":\"usFederal\"},\"program\":{\"path\":\"C:\\\\Tools\\\\job.exe\",\"arguments\":\"-a\",\"workingFolder\":\"C:\\\\Data\",\"environment\":{\"K\":\"v\"},\"timeoutMinutes\":5,\"hidden\":true,\"outputEncoding\":\"utf8\"},\"enabled\":true}");
        T.Eq("created", "created", all["outcome"]!.GetValue<string>());
        T.Eq("holidays", "usFederal", all["job"]!["schedule"]!["skipHolidays"]!.GetValue<string>());
        T.Eq("timeout", 5, all["job"]!["program"]!["timeoutMinutes"]!.GetValue<int>());
        string id = Id(all), rev = Rev(all);
        JsonObject upd = rig.SaveOk("{\"jobId\":\"" + id + "\",\"expectedRevision\":\"" + rev + "\",\"name\":\"Everything 2\",\"schedule\":{\"templateId\":\"weekdays-6pm\",\"skipHolidays\":\"none\"},\"program\":{\"path\":\"C:\\\\Tools\\\\backup.exe\",\"arguments\":\"-b\",\"workingFolder\":\"C:\\\\Tools\",\"environment\":{\"L\":\"w\"},\"timeoutMinutes\":6,\"hidden\":false,\"outputEncoding\":\"oem\"},\"enabled\":false}");
        T.True("renamed and updated in one call", upd["outcome"]!.GetValue<string>() == "renamed" && Id(upd) == id);
    }
}
