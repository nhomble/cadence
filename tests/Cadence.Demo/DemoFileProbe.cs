using Cadence.Core.Execution;

namespace Cadence.App.Demo;

/// <summary>
/// A pretend machine for demo mode and renders: no disk, no PATH, no COM. Python's launcher and Node.js "are installed", PowerShell 7 is not,
/// and a few named shortcuts resolve to the demo programs, so a drop gives the same answer on every PC.
/// </summary>
public sealed class DemoFileProbe : IFileProbe
{
    public bool FileExists(string path) => string.Equals(path, DropInterpreter.PyLauncher, StringComparison.OrdinalIgnoreCase);

    public bool DirectoryExists(string path) => path.EndsWith(@"\Documents", StringComparison.OrdinalIgnoreCase);

    public string? FindOnPath(string exeName) =>
        string.Equals(exeName, "node.exe", StringComparison.OrdinalIgnoreCase) ? @"C:\Program Files\nodejs\node.exe" : null;

    public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir)
    {
        args = null;
        workingDir = null;
        int slash = lnkPath.LastIndexOfAny(['\\', '/']);
        string file = slash >= 0 ? lnkPath[(slash + 1)..] : lnkPath;
        switch (file.ToLowerInvariant())
        {
            case "stock ingestion.lnk":
                return @"C:\Users\Dad\stocks\ingest.py";
            case "youtube feed.lnk":
                args = "--flat-playlist -J :ythome";
                workingDir = @"C:\Users\tester\Videos";
                return @"C:\Tools\yt-dlp.exe";
            case "notepad.lnk":
                return @"C:\Windows\System32\notepad.exe";
            case "tidy downloads.lnk":
                args = "\"C:\\Users\\tester\\My Downloads\" --older-than=30 a&b";
                return @"C:\Users\tester\scripts\tidy.bat";
            default:
                return null;
        }
    }
}
