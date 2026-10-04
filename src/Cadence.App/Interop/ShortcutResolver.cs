using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cadence.Core.Execution;

namespace Cadence.App.Interop;

/// <summary>The shell's <c>IShellLinkW</c> (Unicode), declared by hand: no references and no NuGet; COM stays in the App.</summary>
[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
    void GetIDList(out IntPtr pidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

/// <summary><c>IPersistFile</c>, the part of a shell link that loads and saves its .lnk file.</summary>
[ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

/// <summary>
/// Reads a .lnk file with the shell's <c>IShellLinkW</c> (it only reads: the shortcut is never launched and its target is never searched for). The
/// plan's <c>WScript.Shell.CreateShortcut</c> turned out to be ANSI-only (a shortcut or folder with Japanese characters in its name comes back
/// empty), so the Unicode interface is used instead. The read runs on its own short-lived STA thread with a time limit, so a slow disk or share
/// can't hang the caller (and it is never the UI thread). Any failure gives null.
/// </summary>
internal static class ShortcutResolver
{
    public static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    /// <summary>The target, arguments and "Start in" folder of a shortcut. Null when the file is missing, can't be read, or has no target path (advertised and Store shortcuts).</summary>
    public static string? Resolve(string lnkPath, out string? arguments, out string? workingDirectory)
    {
        arguments = null;
        workingDirectory = null;
        if (string.IsNullOrWhiteSpace(lnkPath)) return null;
        string? target = null, args = null, cwd = null;
        var thread = new Thread(() => (target, args, cwd) = ReadOnThisThread(lnkPath)) { IsBackground = true, Name = "Cadence shortcut reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeLimit)) return null;
        if (string.IsNullOrWhiteSpace(target)) return null;
        arguments = NullIfBlank(args);
        workingDirectory = NullIfBlank(cwd);
        return target.Trim();
    }

    static (string? Target, string? Args, string? Cwd) ReadOnThisThread(string lnkPath)
    {
        object? link = null;
        try
        {
            if (!File.Exists(lnkPath)) return (null, null, null);
            Type? type = Type.GetTypeFromCLSID(ShellLinkClass);
            if (type is null) return (null, null, null);
            link = Activator.CreateInstance(type);
            if (link is not IShellLinkW shell || link is not IPersistFile file) return (null, null, null);
            file.Load(lnkPath, 0);   // STGM_READ
            return (Read(shell.GetPath), Read(shell.GetArguments), Read(shell.GetWorkingDirectory));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException or NotSupportedException or ArgumentException)
        {
            return (null, null, null);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
        }
    }

    static string? Read(Action<StringBuilder, int> getter)
    {
        var sb = new StringBuilder(2048);
        getter(sb, sb.Capacity);
        return sb.ToString();
    }

    static string? Read(Action<StringBuilder, int, IntPtr, uint> getter) => Read((sb, n) => getter(sb, n, IntPtr.Zero, 0));

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The real machine for <see cref="DropInterpreter"/>: files, PATH and shortcuts.</summary>
internal sealed class SystemFileProbe : IFileProbe
{
    public bool FileExists(string path)
    {
        try { return File.Exists(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
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
                string candidate = System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(raw.Trim('"')), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { /* a bad PATH entry: skip it */ }
        }
        return null;
    }

    public string? ResolveShortcut(string lnkPath, out string? args, out string? workingDir) =>
        ShortcutResolver.Resolve(lnkPath, out args, out workingDir);
}
