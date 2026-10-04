using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cadence.App.Interop;

/// <summary>Console output for a GUI (WinExe) process: attaches to the parent console or falls back to CONOUT$. Ported from wsl-tray Program.WriteOut.</summary>
internal static class ConsoleOut
{
    public static void Write(string text)
    {
        Native.AttachConsole(-1);
        IntPtr h = Native.GetStdHandle(-11);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
            h = Native.CreateFile("CONOUT$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
        try
        {
            using var fs = new FileStream(new SafeFileHandle(h, false), FileAccess.Write);
            byte[] b = new UTF8Encoding(false).GetBytes(text);
            fs.Write(b, 0, b.Length);
            fs.Flush();
        }
        catch (IOException)
        {
            // The reader closed the pipe or the console went away (for example `| Select -First 1`): nobody is listening, nothing to report to.
        }
    }
}
