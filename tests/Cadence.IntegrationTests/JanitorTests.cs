using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>[Review R] The janitor must never kill a process this test run did not start, even when its command line matches the pattern.</summary>
public static class JanitorTests
{
    static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly DateTime After = Start.AddMinutes(1);

    public static void OnlyProvableLeftoversAreKilled()
    {
        var wrapper = new ProcInfo(100, 4, @"powershell.exe -c ""$tp='\CadenceTest\job'; ...""", After);
        var cmdUnderWrapper = new ProcInfo(101, 100, @"cmd.exe /d /c ping -n 71 127.0.0.1", After);
        var pingUnderCmd = new ProcInfo(102, 101, @"ping -n 71 127.0.0.1", After);
        var orphanPing = new ProcInfo(103, 999, @"ping  -n 72 127.0.0.1", After);
        var ownersPing = new ProcInfo(200, 50, @"ping -n 9 127.0.0.1", After);                 // parent 50 = the owner's batch file, alive
        var ownersCmdPing = new ProcInfo(201, 60, @"cmd.exe /c ping -n 9 127.0.0.1", After);  // typed in a terminal (parent 60 alive)
        var ownersPingChild = new ProcInfo(202, 201, @"ping -n 9 127.0.0.1", After);
        var oldOrphan = new ProcInfo(300, 998, @"ping -n 9 127.0.0.1", Start.AddMinutes(-5));  // older than the run
        var noTime = new ProcInfo(301, 997, @"ping -n 9 127.0.0.1");
        var all = new List<ProcInfo> { wrapper, cmdUnderWrapper, pingUnderCmd, orphanPing, ownersPing, ownersCmdPing, ownersPingChild, oldOrphan, noTime };
        bool Alive(int pid) => pid is 4 or 50 or 60 or 100 or 101 or 201;

        T.True("the wrapper itself", Janitor.IsOurs(wrapper, all, Alive, Start));
        T.True("cmd under the wrapper", Janitor.IsOurs(cmdUnderWrapper, all, Alive, Start));
        T.True("ping under cmd under the wrapper", Janitor.IsOurs(pingUnderCmd, all, Alive, Start));
        T.True("an orphaned marked ping (its parent is gone)", Janitor.IsOurs(orphanPing, all, Alive, Start));
        T.True("NOT the owner's ping with a live non-wrapper parent", !Janitor.IsOurs(ownersPing, all, Alive, Start));
        T.True("NOT the owner's cmd /c ping", !Janitor.IsOurs(ownersCmdPing, all, Alive, Start));
        T.True("NOT the ping under the owner's cmd", !Janitor.IsOurs(ownersPingChild, all, Alive, Start));
        T.True("NOT a process older than the run", !Janitor.IsOurs(oldOrphan, all, Alive, Start));
        T.True("NOT a process whose start time is unknown", !Janitor.IsOurs(noTime, all, Alive, Start));
    }
}
