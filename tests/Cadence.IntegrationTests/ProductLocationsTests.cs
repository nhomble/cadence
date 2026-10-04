using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// The comparison behind <c>McpSafetyTests.ZzProcessHygiene</c> ("the run did not create or change anything in the product's locations") must really notice a change. These tests use
/// synthetic snapshots and a scratch folder under the temp directory only; nothing here touches \Cadence, HKCU\Software\Cadence or %LOCALAPPDATA%\Cadence for writing.
/// </summary>
public static class ProductLocationsTests
{
    static readonly string Fp = ProductLocations.Hash("x");

    static ProductLocations Snap(bool folder = true, int tasks = 8, bool key = true, string? keyFp = null, bool data = true, string? dataFp = null) =>
        new(folder, tasks, key, keyFp ?? Fp, data, dataFp ?? Fp);

    public static void TheSameSnapshotsDoNotDiffer()
    {
        T.Eq("identical snapshots: no differences", 0, ProductLocations.Differences(Snap(), Snap()).Count);
        T.Eq("existing product locations are fine as such (nothing is required to be absent)", 0, ProductLocations.Differences(Snap(tasks: 8), Snap(tasks: 8)).Count);
    }

    public static void EachKindOfChangeIsNoticedInItsOwnArea()
    {
        T.Eq("a task added to the product folder", "tasks", ProductLocations.Differences(Snap(tasks: 8), Snap(tasks: 9)).Single().Area);
        T.Eq("a task removed from the product folder", "tasks", ProductLocations.Differences(Snap(tasks: 8), Snap(tasks: 7)).Single().Area);
        T.Eq("the product task folder created", "tasks", ProductLocations.Differences(Snap(folder: false, tasks: 0), Snap(folder: true, tasks: 0)).Single().Area);
        T.Eq("the key created", "key", ProductLocations.Differences(Snap(key: false), Snap(key: true)).Single().Area);
        T.Eq("a value of the key changed", "key", ProductLocations.Differences(Snap(), Snap(keyFp: ProductLocations.Hash("y"))).Single().Area);
        T.Eq("the data folder created", "data", ProductLocations.Differences(Snap(data: false), Snap(data: true)).Single().Area);
        T.Eq("a file of the data folder changed", "data", ProductLocations.Differences(Snap(), Snap(dataFp: ProductLocations.Hash("y"))).Single().Area);
    }

    public static void TheFolderFingerprintSeesNewFilesSizesAndTimestampsButNotOtherFolders()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadence-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = System.IO.Path.Combine(root, "a.txt");
            File.WriteAllText(a, "one");
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            string f0 = ProductLocations.FingerprintOfFolder(root);
            T.Eq("unchanged folder: same fingerprint", f0, ProductLocations.FingerprintOfFolder(root));

            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            T.True("a newer timestamp changes it", ProductLocations.FingerprintOfFolder(root) != f0);
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            T.Eq("restoring the timestamp restores it", f0, ProductLocations.FingerprintOfFolder(root));

            File.WriteAllText(a, "three");
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            T.True("a different size changes it", ProductLocations.FingerprintOfFolder(root) != f0);
            File.WriteAllText(a, "one");
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            string sub = System.IO.Path.Combine(root, "runs");
            Directory.CreateDirectory(sub);
            T.Eq("an empty subfolder is not a file", f0, ProductLocations.FingerprintOfFolder(root));
            File.WriteAllText(System.IO.Path.Combine(sub, "b.json"), "{}");
            T.True("a new file in a subfolder changes it", ProductLocations.FingerprintOfFolder(root) != f0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
