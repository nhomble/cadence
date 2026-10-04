using Cadence.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Regression tests for the defects found by the live red-team pass (tools\redteam-agent.ps1, PLAN.md section 11 "Red-team E0").</summary>
public static class RedTeamTests
{
    const string VarName = "CADENCE_RT_UNIT_SECRET";
    const string SecretValue = "unit-s3cret-7e1d-4b2f";

    static string Save(string program) =>
        "{\"name\":\"P\",\"schedule\":{\"cron\":[\"0 18 * * *\"]},\"program\":" + program + "}";

    /// <summary>
    /// RT-1: "C:\%TOKEN%.exe" passes the shape check once expanded, so the refusal said "The program 'C:\&lt;the token&gt;.exe' does not exist": any variable of the
    /// agent host's environment could be read through the error text (and the audit log). The messages now quote the text the agent gave.
    /// </summary>
    public static void ARefusalNeverQuotesWhatAVariableExpandsTo()
    {
        Environment.SetEnvironmentVariable(VarName, SecretValue);
        try
        {
            using var rig = new WriteRig();
            string v = "%" + VarName + "%";
            (string Label, string Program, string MustMention)[] cases =
            [
                ("missing program", "{\"path\":\"C:\\\\" + v + ".exe\"}", "does not exist on this PC"),
                ("script hint", "{\"path\":\"C:\\\\" + v + ".bat\"}", "A script can't be the program itself"),
                ("script hint (.ps1)", "{\"path\":\"C:\\\\" + v + ".ps1\"}", "A script can't be the program itself"),
                ("script hint (.py)", "{\"path\":\"C:\\\\" + v + ".py\"}", "A script can't be the program itself"),
                ("extension message", "{\"path\":\"C:\\\\" + v + ".x" + v + "\"}", "The program must be an .exe or .com file"),
                ("no extension", "{\"path\":\"C:\\\\" + v + "\"}", "The program must be an .exe or .com file"),
                ("a bare name that a variable expands to", "{\"path\":\"" + v + "\"}", "Task Scheduler does not use your PATH"),
                ("missing working folder", "{\"path\":\"C:\\\\Tools\\\\job.exe\",\"workingFolder\":\"C:\\\\" + v + "\"}", "does not exist on this PC"),
            ];
            foreach ((string label, string program, string mention) in cases)
            {
                rig.Now = rig.Now.AddSeconds(5);
                ToolResult r = rig.Save(Save(program));
                string all = r.Text + "|" + WriteRig.Msg(r) + "|" + r.Structured?["error"]?["hint"];
                T.True(label + ": refused as program_rejected", r.IsError && WriteRig.Code(r) == "program_rejected", WriteRig.Code(r) + ": " + WriteRig.Msg(r));
                T.True(label + ": the message does not contain the variable's value", !all.Contains(SecretValue, StringComparison.Ordinal), WriteRig.Msg(r));
                T.True(label + ": the message still says what is wrong", WriteRig.Msg(r).Contains(mention, StringComparison.Ordinal), WriteRig.Msg(r));
            }
            T.True("the missing-program message quotes the text as given, with a hint that variables are replaced",
                WriteRig.Msg(rig.Save(Save("{\"path\":\"C:\\\\" + v + ".exe\"}"))).StartsWith("The program 'C:\\" + v + ".exe' does not exist on this PC once its %VARIABLES% are replaced.", StringComparison.Ordinal));
            T.True("no audit line holds the value", rig.Audit.All.All(a => !(a.Detail + a.JobName + a.Code).Contains(SecretValue, StringComparison.Ordinal)));
            T.Eq("nothing was written", 0, rig.Registers);

            // a variable that EXISTS and expands to a real program is still accepted (the checks use the expansion, only the words do not)
            rig.Probe.Files.Add(@"C:\Tools\" + SecretValue + ".exe");
            ToolResult ok = rig.Save(Save("{\"path\":\"C:\\\\Tools\\\\" + v + ".exe\"}"));
            T.True("an existing program reached through a variable is accepted: " + ok.Text, !ok.IsError);
            T.True("and the result shows the variable as written, not its value", !ok.Text.Contains(SecretValue, StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(VarName, null); }
    }
}
