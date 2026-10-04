using Cadence.TestHarness;

namespace Cadence.Tests;

public static class ScaffoldTests
{
    public static void HarnessWorks() => T.Eq("1+1", 2, 1 + 1);
}
