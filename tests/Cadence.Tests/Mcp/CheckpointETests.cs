using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Metadata;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>
/// Checkpoint E: regressions for what the final review found by attacking the real server by hand (PLAN 11, Checkpoint E row): names that only look like an
/// owner's job name, cron lines that differ only in spaces, and agent names that only look like the app.
/// </summary>
public static class CheckpointETests
{
    static string Cyr(char c) => c.ToString();

    public static void AJobNameThatOnlyLooksLikeTheOwnersIsTaken()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(WriteRig.Job("Owner Backup", "ownerbk1"));
        rig.OwnerSave(WriteRig.Job("Caf" + (char)0xE9 + " notes", "ownercf1"));

        string[] lookAlikes =
        [
            "Owner" + (char)0xA0 + "Backup",                    // no-break space
            "Owner" + (char)0x200B + " Backup",                 // zero-width space
            "  owner    BACKUP ",                               // case and spaces
            "Owner" + (char)0x202F + "Backup",                  // narrow no-break space
            (char)0xFF2F + "wner Backup",                       // fullwidth O
            "Cafe" + (char)0x301 + " notes",                    // e + combining acute (NFD) against the owner's NFC é
        ];
        foreach (string name in lookAlikes)
        {
            ToolResult r = rig.Save(WriteRig.Create(name, "0 3 1 1 *"));
            T.True("create '" + JsonSerializer.Serialize(name) + "' is name_taken", r.IsError && WriteRig.Code(r) == "name_taken", r.Text);
        }

        // Renaming an agent's own job into a look-alike is refused the same way.
        JsonObject made = rig.SaveOk(WriteRig.Create("Agent notes", "0 3 1 1 *"));
        string id = made["job"]!["jobId"]!.GetValue<string>();
        string rev = made["job"]!["revision"]!.GetValue<string>();
        ToolResult rename = rig.Save("{\"jobId\":\"" + id + "\",\"expectedRevision\":\"" + rev + "\",\"name\":" + JsonSerializer.Serialize("Owner" + (char)0xA0 + "Backup") + "}");
        T.True("rename into a look-alike of the owner's name is name_taken", rename.IsError && WriteRig.Code(rename) == "name_taken", rename.Text);

        // A genuinely different name is still fine.
        T.True("a different name is created", !rig.Save(WriteRig.Create("Owner Backup 2", "0 3 1 1 *")).IsError);
    }

    public static void ACronLineThatDiffersOnlyInSpacesIsStoredPlainAndIsNoChange()
    {
        using var rig = new WriteRig();
        JsonObject made = rig.SaveOk(WriteRig.Create("Spaced", "  0   3 1 1 *  "));
        JsonObject job = made["job"]!.AsObject();
        T.Eq("the line is stored in its plain form", "0 3 1 1 *", job["schedule"]!["cron"]![0]!.GetValue<string>());
        string id = job["jobId"]!.GetValue<string>();
        string rev = job["revision"]!.GetValue<string>();
        int writes = rig.Registers;
        JsonObject again = rig.SaveOk("{\"jobId\":\"" + id + "\",\"expectedRevision\":\"" + rev + "\",\"schedule\":{\"cron\":[\"0  3  1  1  *\"]}}");
        T.Eq("the same schedule with other spaces is unchanged", "unchanged", again["outcome"]!.GetValue<string>());
        T.Eq("and nothing was written", writes, rig.Registers);
        JsonObject tabbed = rig.SaveOk(WriteRig.Create("Tabbed", "0\t3 * *  1-5 "));
        T.Eq("a tab separates fields too", "0 3 * * 1-5", tabbed["job"]!["schedule"]!["cron"]![0]!.GetValue<string>());
    }

    public static void AnAgentNameThatOnlyLooksLikeTheAppIsMarked()
    {
        string[] lookAlikes =
        [
            "Cadence (the " + Cyr((char)0x0430) + "pp)",                         // Cyrillic a
            "Cadence " + (char)0xFF08 + "the app" + (char)0xFF09,                // fullwidth brackets
            "C" + Cyr((char)0x0430) + "dence (the " + Cyr((char)0x0430) + "pp)", // two Cyrillic a
            "CADENCE  (THE APP)",                                                // case and spaces
            "Cadence (the app)" + (char)0x200D,                                  // zero-width joiner (dropped by SanitizeName already)
            "the c" + Cyr((char)0x043E) + "mmand line",                          // Cyrillic o
            Cyr((char)0x0441) + "li",                                            // Cyrillic c
            "Y" + Cyr((char)0x043E) + "u",                                       // "You" with a Cyrillic o
            "in the app",
        ];
        foreach (string name in lookAlikes)
        {
            string shown = Actor.Agent(name).Describe();
            T.True("'" + JsonSerializer.Serialize(name) + "' is marked as an agent", shown.EndsWith(" (AI agent)", StringComparison.Ordinal), shown);
        }
        foreach (string fine in new[] { "Claude Code", "Cadence Test Agent", "Cadence helper", "Apple", "Clippy" })
            T.Eq("'" + fine + "' is shown as it is", fine, Actor.Agent(fine).Describe());
        T.Eq("the app itself is still the app", "Cadence (the app)", Actor.App.Describe());
    }
}
