using System.Runtime.InteropServices;

namespace Cadence.Scheduler.Com;

/// <summary>Deterministic release of COM objects obtained through late binding.</summary>
public static class ComRelease
{
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            _ = Marshal.FinalReleaseComObject(comObject);
    }
}
