namespace MnMCompanion;

/// <summary>The part of a monitor a window may use (the screen less the taskbar), in device-independent units.</summary>
public readonly record struct WorkArea(double Left, double Top, double Width, double Height)
{
    /// <summary>The right edge of the area.</summary>
    public double Right => Left + Width;

    /// <summary>The bottom edge of the area.</summary>
    public double Bottom => Top + Height;
}

/// <summary>Where a window goes and how big it is, with the least size it may be dragged down to at its scale.</summary>
public readonly record struct WindowBox(double Left, double Top, double Width, double Height, double MinWidth, double MinHeight);

/// <summary>
/// How big a window draws its contents, as a percent of normal, and how a window is kept on its monitor when that changes.
/// Only the offered <see cref="Presets"/> are used, so a saved file holding anything else lands on the nearest one.
/// </summary>
public static class UiScale
{
    /// <summary>The scales offered, as percents of normal size.</summary>
    public static readonly IReadOnlyList<int> Presets = [100, 125, 150, 175, 200];

    /// <summary>The scale a window starts at.</summary>
    public const int Default = 100;

    /// <summary>The smallest scale.</summary>
    public const int Min = 100;

    /// <summary>The largest scale.</summary>
    public const int Max = 200;

    /// <summary>A percent held to what can be used (a saved file can hold anything).</summary>
    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>The offered scale nearest to <paramref name="percent"/>; halfway between two, the smaller one.</summary>
    public static int Nearest(int percent)
    {
        var clamped = Clamp(percent);
        var best = Presets[0];
        var bestDist = Math.Abs(best - clamped);
        foreach (var p in Presets)
        {
            if (Math.Abs(p - clamped) < bestDist)
            {
                best = p;
                bestDist = Math.Abs(p - clamped);
            }
        }
        return best;
    }

    /// <summary>One offered scale up from <paramref name="current"/>, staying at <see cref="Max"/> at the top.</summary>
    public static int Next(int current)
    {
        var idx = IndexOf(Nearest(current));
        return Presets[Math.Min(idx + 1, Presets.Count - 1)];
    }

    /// <summary>One offered scale down from <paramref name="current"/>, staying at <see cref="Min"/> at the bottom.</summary>
    public static int Previous(int current)
    {
        var idx = IndexOf(Nearest(current));
        return Presets[Math.Max(idx - 1, 0)];
    }

    /// <summary>The multiplier for a percent (1.0 at normal size).</summary>
    public static double Factor(int percent) => Nearest(percent) / 100.0;

    /// <summary>
    /// A window's place and size kept inside <paramref name="work"/>: never smaller than its least size at the scale (unless the monitor itself is smaller),
    /// never bigger than the monitor, and moved so the whole window (its title bar and the buttons at the right end of it) can be seen.
    /// </summary>
    public static WindowBox ClampToWorkArea(double left, double top, double width, double height, double baseMinWidth, double baseMinHeight, int scalePercent, WorkArea work)
    {
        var s = Factor(scalePercent);
        var minW = Math.Min(baseMinWidth * s, work.Width);
        var minH = Math.Min(baseMinHeight * s, work.Height);
        var w = Math.Clamp(width, minW, work.Width);
        var h = Math.Clamp(height, minH, work.Height);
        var l = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
        var t = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new WindowBox(l, t, w, h, minW, minH);
    }

    /// <summary>
    /// A window grown or shrunk by the change from <paramref name="oldScalePercent"/> to <paramref name="newScalePercent"/>, keeping its top-left corner,
    /// then kept inside <paramref name="work"/> as <see cref="ClampToWorkArea"/> does.
    /// </summary>
    public static WindowBox ResizeForScale(double left, double top, double width, double height, double baseMinWidth, double baseMinHeight, int oldScalePercent, int newScalePercent, WorkArea work)
    {
        var ratio = Factor(newScalePercent) / Factor(oldScalePercent);
        return ClampToWorkArea(left, top, width * ratio, height * ratio, baseMinWidth, baseMinHeight, newScalePercent, work);
    }

    private static int IndexOf(int preset)
    {
        for (var i = 0; i < Presets.Count; i++)
            if (Presets[i] == preset) return i;
        return 0;
    }
}
