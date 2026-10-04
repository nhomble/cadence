using System.Globalization;
using Cadence.App.Interop;
using Cadence.App.UI.Render;

namespace Cadence.Render;

/// <summary>
/// The dev-only screenshot tool. <c>Cadence.Render &lt;state&gt; --out &lt;file.png&gt; [--width W] [--height H]</c> renders one screen; <c>Cadence.Render &lt;folder&gt; [--width W] [--height H]</c>
/// renders every screen to <c>&lt;folder&gt;\&lt;state&gt;.png</c>. Exit codes: 0 done, 1 the render failed, 2 the command line is wrong. Prints through ConsoleOut (GUI-subsystem exe, no console window of its own).
/// </summary>
public static class Program
{
    const int DefaultWidth = 1280;
    const int DefaultHeight = 800;
    const int MinSize = 320;
    const int MaxSize = 4000;

    [STAThread]
    public static int Main(string[] args)
    {
        string? target = null, outPath = null;
        int width = DefaultWidth, height = DefaultHeight;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--out" or "--width" or "--height")
            {
                string? v = i + 1 < args.Length ? args[++i] : null;
                if (v is null) return Fail(a + " needs a value.");
                if (a == "--out") outPath = v;
                else if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int px) || px < MinSize || px > MaxSize)
                    return Fail(a + " must be a whole number from " + MinSize.ToString(CultureInfo.InvariantCulture) + " to " + MaxSize.ToString(CultureInfo.InvariantCulture) + ", not '" + v + "'.");
                else if (a == "--width") width = px;
                else height = px;
            }
            else if (a.StartsWith('-') || target is not null) return Fail("Unexpected argument '" + a + "'.");
            else target = a;
        }
        if (target is null) return Fail("needs a state name or an output folder.");
        return Renderer.Run(target, outPath, width, height);
    }

    static int Fail(string error)
    {
        ConsoleOut.Write("ERROR: Cadence.Render " + error + "\r\nUsage: Cadence.Render <state> --out <file.png> [--width W] [--height H]\r\n       Cadence.Render <folder> [--width W] [--height H]\r\n");
        return 2;
    }
}