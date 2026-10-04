using System.Diagnostics;

namespace Cadence.App.Interop;

/// <summary>
/// The names of the processes that started this one ("cmd.exe &lt; svchost.exe"), for the audit line and the registry record of a command-line
/// resume ([CP-R2]): if a job an agent made runs <c>cmd /c Cadence.exe --agents resume --yes</c>, the owner can read where it came from. Best effort
/// (a snapshot of the process table; a parent that already exited, or an id that was reused by a newer process, ends the chain). Never throws.
/// </summary>
internal static class ProcessAncestry
{
    public const int DefaultDepth = 3;

    public static string? Describe(int depth = DefaultDepth)
    {
        try
        {
            var table = new Dictionary<uint, (uint Parent, string Name)>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.Th32csSnapProcess, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
            try
            {
                var e = new Native.ProcessEntry32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.ProcessEntry32>() };
                for (bool ok = Native.Process32First(snap, ref e); ok; ok = Native.Process32Next(snap, ref e))
                    table[e.th32ProcessID] = (e.th32ParentProcessID, Name(ref e));
            }
            finally { Native.CloseHandle(snap); }

            var names = new List<string>();
            var seen = new HashSet<uint>();
            uint pid = (uint)Environment.ProcessId;
            DateTime childStart = StartOf(pid);
            while (names.Count < depth && table.TryGetValue(pid, out (uint Parent, string Name) me) && me.Parent != 0 && seen.Add(pid))
            {
                if (!table.TryGetValue(me.Parent, out (uint Parent, string Name) parent)) break;
                // A parent id that now belongs to a process started AFTER the child was reused: the real parent is gone.
                DateTime parentStart = StartOf(me.Parent);
                if (parentStart != DateTime.MinValue && childStart != DateTime.MinValue && parentStart > childStart) break;
                names.Add(parent.Name);
                pid = me.Parent;
                childStart = parentStart;
            }
            return names.Count == 0 ? null : string.Join(" < ", names);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    static unsafe string Name(ref Native.ProcessEntry32 e)
    {
        fixed (char* p = e.szExeFile) return new string(p);
    }

    static DateTime StartOf(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
