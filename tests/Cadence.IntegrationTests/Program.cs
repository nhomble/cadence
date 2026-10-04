using Cadence.IntegrationTests;
using Cadence.TestHarness;

// Integration tests against the real Task Scheduler. They only ever use \CadenceTest\ (see TestFolder).
// Order: clean stale leftovers -> run -> clean again (also from process-exit and Ctrl+C hooks).
TestFolder.InstallExitHooks();

SortedSet<string> before;
try
{
    before = TestFolder.SnapshotOtherTasks();
    // The product's own locations are only read, here and at the end (McpSafetyTests.ZzProcessHygiene compares): the run must not create or change anything there.
    ProductLocations.Baseline = ProductLocations.Take();
    TestFolder.Cleanup();
}
catch (Exception ex)
{
    Console.Error.WriteLine("Cannot prepare \\CadenceTest: " + ex);
    return 1;
}

int rc;
try { rc = T.RunAll(args); }
finally { TestFolder.TryCleanup(); }

if (TestFolder.Exists())
{
    Console.WriteLine("FAIL \\CadenceTest still exists after the run");
    rc = 1;
}

// Information only: other tasks come and go on their own (updaters), so a difference is reported, not failed.
SortedSet<string> after = TestFolder.SnapshotOtherTasks();
var gone = before.Except(after, StringComparer.OrdinalIgnoreCase).ToList();
var added = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
Console.WriteLine(gone.Count == 0 && added.Count == 0
    ? "OK   other tasks unchanged (" + after.Count + " outside \\Cadence and \\CadenceTest)"
    : "WARN other tasks changed during the run: removed [" + string.Join("; ", gone) + "] added [" + string.Join("; ", added) + "]");
return rc;
