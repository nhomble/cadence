using System.Globalization;

namespace Cadence.App.UI.Logic;

/// <summary>Where the main window sits (device-independent pixels) and whether it was maximized.</summary>
public readonly record struct PlacementData(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>A rectangle in device-independent pixels (a screen's work area).</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Remembering the window's position and size between runs (the only setting besides "Show other tasks"; PLAN 7.2 / Phase 15). Pure: the text format and the
/// rule that a saved position that no longer fits the screens (a monitor was unplugged, the resolution changed) never leaves the window off screen.
/// </summary>
public static class WindowPlacement
{
    public const double MinWidth = 560;
    public const double MinHeight = 420;
    public const double DefaultWidth = 1100;
    public const double DefaultHeight = 720;

    /// <summary>"120,80,1100,720,0" (left, top, width, height, maximized 0 or 1), whole pixels.</summary>
    public static string Format(PlacementData p) =>
        string.Join(",", new[] { p.Left, p.Top, p.Width, p.Height }.Select(v => Math.Round(v).ToString("0", CultureInfo.InvariantCulture))) + "," + (p.Maximized ? "1" : "0");

    public static bool TryParse(string? text, out PlacementData placement)
    {
        placement = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split(',');
        if (parts.Length != 5) return false;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
        if (parts[4].Trim() is not ("0" or "1")) return false;
        if (v[2] < 100 || v[3] < 100 || v[2] > 20000 || v[3] > 20000) return false;
        placement = new PlacementData(v[0], v[1], v[2], v[3], parts[4].Trim() == "1");
        return true;
    }

    /// <summary>
    /// Makes a saved placement safe: at least the minimum size, no bigger than the screen it is mostly on, and moved so that its title bar is inside a screen
    /// (so it can always be grabbed). <paramref name="screens"/> are the work areas; with none, the placement is returned unchanged.
    /// </summary>
    public static PlacementData Fit(PlacementData p, IReadOnlyList<ScreenRect> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0) return p;
        double w = Math.Max(MinWidth, p.Width);
        double h = Math.Max(MinHeight, p.Height);

        // The screen the window overlaps most (else the first).
        ScreenRect best = screens[0];
        double bestOverlap = -1;
        foreach (ScreenRect s in screens)
        {
            double ox = Math.Max(0, Math.Min(p.Left + w, s.Right) - Math.Max(p.Left, s.X));
            double oy = Math.Max(0, Math.Min(p.Top + h, s.Bottom) - Math.Max(p.Top, s.Y));
            if (ox * oy > bestOverlap) { bestOverlap = ox * oy; best = s; }
        }
        w = Math.Min(w, Math.Max(MinWidth, best.Width));
        h = Math.Min(h, Math.Max(MinHeight, best.Height));

        double left = p.Left, top = p.Top;
        if (bestOverlap <= 0)
        {
            // Entirely off every screen: centre it on the first one.
            left = best.X + (best.Width - w) / 2;
            top = best.Y + (best.Height - h) / 2;
        }
        else
        {
            // At least 120 px of the title bar must be on the screen, and the top edge below the screen's top.
            left = Math.Min(Math.Max(left, best.X - w + 120), best.Right - 120);
            top = Math.Min(Math.Max(top, best.Y), best.Bottom - 48);
        }
        return new PlacementData(left, top, w, h, p.Maximized);
    }
}
