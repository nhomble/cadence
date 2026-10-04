using System.Reflection;
using System.Text.RegularExpressions;

namespace Cadence.Core;

/// <summary>
/// Version text for the About dialog and <c>--probe</c> (port of wsl-tray's AppVersion). The raw value is the AssemblyInformationalVersion
/// stamped by build.cmd from CADENCE_VERSION ("dev" for local builds, e.g. "0.1.1" for releases).
/// </summary>
public static partial class AppVersion
{
    /// <summary>Pure display rule: null/empty/"dev" gives "dev build"; a release-looking value gets a "v" prefix; anything else is shown as is. A "+commit" build suffix is dropped.</summary>
    public static string Format(string? raw)
    {
        if (raw is null) return "dev build";
        raw = raw.Trim();
        int plus = raw.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0 && (raw[..plus] == "dev" || ReleasePattern().IsMatch(raw[..plus]))) raw = raw[..plus];   // SDK-appended source revision
        if (raw.Length == 0 || raw == "dev") return "dev build";
        return ReleasePattern().IsMatch(raw) ? "v" + raw : raw;
    }

    /// <summary>The raw informational version of the entry assembly (null when it carries none).</summary>
    public static string? Raw()
    {
        try
        {
            Assembly a = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string Display() => Format(Raw());

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+.][0-9A-Za-z.+-]*)?$")]
    private static partial Regex ReleasePattern();
}
