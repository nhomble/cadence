using System.Security.Cryptography;
using System.Text;
using Cadence.Scheduler;
using Cadence.Scheduler.Com;
using Microsoft.Win32;

namespace Cadence.IntegrationTests;

/// <summary>
/// What a test run may NOT change: the product's own locations (the \Cadence task folder, HKCU\Software\Cadence, %LOCALAPPDATA%\Cadence). The user may legitimately have schedules there,
/// so "absent" is not the invariant; "the run did not create or change anything" is. <see cref="Take"/> reads (never writes) a cheap fingerprint of each: whether it exists, the number of tasks in the
/// folder, a hash of the key's value names, kinds and data (and of its subkeys), and a hash of the data folder's file list with sizes and timestamps. Contents of files and values are hashed, never
/// read out or reported. Program.cs takes the baseline before the first test; <c>McpSafetyTests.ZzProcessHygiene</c> takes another at the end and compares with <see cref="Differences"/>.
/// </summary>
public sealed record ProductLocations(bool FolderExists, int TaskCount, bool KeyExists, string KeyFingerprint, bool DataExists, string DataFingerprint)
{
    /// <summary>The baseline taken at the start of the run (set by Program.cs; null when the tests are driven some other way).</summary>
    public static ProductLocations? Baseline { get; set; }

    /// <summary>Reads the three product locations. Read-only.</summary>
    public static ProductLocations Take()
    {
        bool folderExists;
        using (TaskServiceSession s = TaskServiceSession.Open())
        {
            object? f = s.TryGetFolder("\\Cadence");
            ComRelease.Release(f);
            folderExists = f is not null;
        }
        int count = folderExists ? new TaskSchedulerGateway(new SchedulerOptions("Cadence", "Cadence")).ListManagedFolder().Count : 0;

        bool keyExists;
        string keyFp;
        using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Cadence"))
        {
            keyExists = k is not null;
            var sb = new StringBuilder();
            if (k is not null) AppendKey(sb, k, "");
            keyFp = Hash(sb.ToString());
        }

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cadence");
        bool dataExists = Directory.Exists(dir);
        return new ProductLocations(folderExists, count, keyExists, keyFp, dataExists, dataExists ? Hash(FileList(dir)) : Hash(""));
    }

    /// <summary>What differs between two snapshots, per area ("tasks", "key", "data"), in words (no values, no names of anything inside). Empty when nothing changed.</summary>
    public static List<(string Area, string Text)> Differences(ProductLocations before, ProductLocations after)
    {
        var d = new List<(string, string)>();
        if (before.FolderExists != after.FolderExists) d.Add(("tasks", "the \\Cadence task folder " + (after.FolderExists ? "was created" : "was removed")));
        if (before.TaskCount != after.TaskCount) d.Add(("tasks", "the number of tasks in \\Cadence changed (" + before.TaskCount + " -> " + after.TaskCount + ")"));
        if (before.KeyExists != after.KeyExists) d.Add(("key", "HKCU\\Software\\Cadence " + (after.KeyExists ? "was created" : "was removed")));
        else if (before.KeyFingerprint != after.KeyFingerprint) d.Add(("key", "the values or subkeys of HKCU\\Software\\Cadence changed"));
        if (before.DataExists != after.DataExists) d.Add(("data", "the %LOCALAPPDATA%\\Cadence folder " + (after.DataExists ? "was created" : "was removed")));
        else if (before.DataFingerprint != after.DataFingerprint) d.Add(("data", "the files in %LOCALAPPDATA%\\Cadence changed (names, sizes or timestamps)"));
        return d;
    }

    static void AppendKey(StringBuilder sb, RegistryKey key, string prefix)
    {
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string data = v switch
            {
                byte[] b => Convert.ToHexString(b),
                string[] m => string.Join("\u0001", m),
                null => "",
                _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            sb.Append(prefix).Append('|').Append(name).Append('|').Append(key.GetValueKind(name)).Append('|').Append(data).Append('\n');
        }
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.Append(prefix).Append('/').Append(sub).Append('\n');
            using RegistryKey? child = key.OpenSubKey(sub);
            if (child is not null) AppendKey(sb, child, prefix + "/" + sub);
        }
    }

    static string FileList(string dir)
    {
        var sb = new StringBuilder();
        foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            sb.Append(Path.GetRelativePath(dir, f.FullName)).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
        return sb.ToString();
    }

    /// <summary>The hash of a canonical text (exposed for the test of the comparison itself).</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The data-folder fingerprint of a directory (exposed so the comparison can be tested on a scratch folder).</summary>
    public static string FingerprintOfFolder(string dir) => Hash(FileList(dir));
}
