using Cadence.Core.Execution;

namespace Cadence.Agent.Tools;

/// <summary>
/// The real machine for the agent tools: files and PATH (shortcuts are never resolved for agents). The read tools do not use it yet; the write
/// tools of Phase 21 check programs and folders with it. It only reads.
/// </summary>
public sealed class AgentFileProbe : IFileProbe
{
    public bool FileExists(string path)
    {
        try { return File.Exists(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    public string? FindOnPath(string exeName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(raw.Trim('"')), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { /* a bad PATH entry: skip it */ }
        }
        return null;
    }

    public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir)
    {
        args = null;
        workingDir = null;
        return null;
    }
}
