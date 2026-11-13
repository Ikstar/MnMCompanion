namespace MnMCompanion.Tests;

/// <summary>
/// Per-window UI scale presets, keyboard stepping, proportional outer resizing, and monitor work-area clamping across
/// 96-DPI 4K, 200%-OS-DPI 1080p-DIP, and negative secondary-monitor coordinates.
/// </summary>
public class UiScaleTests
{
    [Fact]
    public void Presets_span_100_to_200_in_25_percent_steps_with_100_as_default()
    {
        Assert.Equal(100, UiScale.Default);
        Assert.Equal(100, UiScale.Min);
        Assert.Equal(200, UiScale.Max);
        Assert.Equal(new[] { 100, 125, 150, 175, 200 }, UiScale.Presets);

        var area = new WorkArea(-1920, 40, 1920, 1040);
        Assert.Equal(0, area.Right);
        Assert.Equal(1080, area.Bottom);
    }

    [Theory]
    [InlineData(-50, 100)]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(140, 140)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(500, 200)]
    public void Clamp_holds_any_saved_percent_within_bounds(int input, int expected) =>
        Assert.Equal(expected, UiScale.Clamp(input));

    [Theory]
    [InlineData(-25, 100)]
    [InlineData(0, 100)]
    [InlineData(80, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(112, 100)]
    [InlineData(113, 125)]
    [InlineData(125, 125)]
    [InlineData(137, 125)]
    [InlineData(138, 150)]
    [InlineData(150, 150)]
    [InlineData(162, 150)]
    [InlineData(163, 175)]
    [InlineData(175, 175)]
    [InlineData(187, 175)]
    [InlineData(188, 200)]
    [InlineData(200, 200)]
    [InlineData(205, 200)]
    [InlineData(400, 200)]
    public void Nearest_snaps_out_of_range_and_intermediate_values_to_closest_preset(int input, int expected) =>
        Assert.Equal(expected, UiScale.Nearest(input));

    [Theory]
    [InlineData(50, 1.0)]
    [InlineData(100, 1.0)]
    [InlineData(112, 1.0)]
    [InlineData(113, 1.25)]
    [InlineData(125, 1.25)]
    [InlineData(150, 1.5)]
    [InlineData(175, 1.75)]
    [InlineData(200, 2.0)]
    [InlineData(300, 2.0)]
    public void Factor_returns_multiplier_for_snapped_preset(int percent, double expected) =>
        Assert.Equal(expected, UiScale.Factor(percent), 6);

    [Theory]
    [InlineData(50, 125, 100)]
    [InlineData(100, 125, 100)]
    [InlineData(112, 125, 100)]
    [InlineData(113, 150, 100)]
    [InlineData(125, 150, 100)]
    [InlineData(138, 175, 125)]
    [InlineData(150, 175, 125)]
    [InlineData(175, 200, 150)]
    [InlineData(188, 200, 175)]
    [InlineData(200, 200, 175)]
    [InlineData(300, 200, 175)]
    public void Next_and_Previous_snap_first_and_saturate_at_100_and_200(int current, int expectedNext, int expectedPrevious)
    {
        Assert.Equal(expectedNext, UiScale.Next(current));
        Assert.Equal(expectedPrevious, UiScale.Previous(current));
    }

    [Fact]
    public void ResizeForScale_scales_outer_bounds_proportionally_and_round_trips_back_to_100()
    {
        var work = new WorkArea(0, 0, 2560, 1440);

        var enlarged = UiScale.ResizeForScale(120, 80, 480, 600, baseMinWidth: 380, baseMinHeight: 360, oldScalePercent: 100, newScalePercent: 150, work);
        Assert.Equal(new WindowBox(120, 80, 720, 900, 570, 540), enlarged);

        var restored = UiScale.ResizeForScale(enlarged.Left, enlarged.Top, enlarged.Width, enlarged.Height, baseMinWidth: 380, baseMinHeight: 360, oldScalePercent: 150, newScalePercent: 100, work);
        Assert.Equal(new WindowBox(120, 80, 480, 600, 380, 360), restored);
    }

    [Fact]
    public void Scaling_HistoryWindow_to_200_percent_enlarges_on_96_dpi_4k_and_clamps_on_200_percent_os_dpi()
    {
        // 96-DPI 4K (e.g. Linux Wine/Proton): full 3840x2160 DIPs available, shifted inward only enough to keep right/bottom inside work area.
        var work4k = new WorkArea(0, 0, 3840, 2160);
        var box4k = UiScale.ResizeForScale(left: 1200, top: 600, width: 1560, height: 900, baseMinWidth: 960, baseMinHeight: 600, oldScalePercent: 100, newScalePercent: 200, work4k);

        Assert.Equal(3120, box4k.Width, 6);
        Assert.Equal(1800, box4k.Height, 6);
        Assert.Equal(1920, box4k.MinWidth, 6);
        Assert.Equal(1200, box4k.MinHeight, 6);
        Assert.Equal(720, box4k.Left, 6);
        Assert.Equal(360, box4k.Top, 6);
        Assert.True(box4k.Left >= work4k.Left && box4k.Top >= work4k.Top);
        Assert.True(box4k.Left + box4k.Width <= work4k.Right);
        Assert.True(box4k.Top + box4k.Height <= work4k.Bottom);

        // 200% OS DPI on 4K: 1920x1080 DIP work area, where unscaled 1560x900 doubled (3120x1800) and baseMinHeight*2 (1200) exceed the monitor.
        var workHiDpi = new WorkArea(0, 0, 1920, 1080);
        var boxHiDpi = UiScale.ResizeForScale(left: 160, top: 90, width: 1560, height: 900, baseMinWidth: 960, baseMinHeight: 600, oldScalePercent: 100, newScalePercent: 200, workHiDpi);

        Assert.Equal(1920, boxHiDpi.Width, 6);
        Assert.Equal(1080, boxHiDpi.Height, 6);
        Assert.True(boxHiDpi.Width <= workHiDpi.Width);
        Assert.True(boxHiDpi.Height <= workHiDpi.Height);
        Assert.True(boxHiDpi.MinWidth <= workHiDpi.Width);
        Assert.True(boxHiDpi.MinHeight <= workHiDpi.Height);
        Assert.Equal(1080, boxHiDpi.MinHeight, 6);
        Assert.True(boxHiDpi.Left >= workHiDpi.Left);
        Assert.True(boxHiDpi.Top >= workHiDpi.Top);
        Assert.True(boxHiDpi.Left + boxHiDpi.Width <= workHiDpi.Right);
        Assert.True(boxHiDpi.Top + boxHiDpi.Height <= workHiDpi.Bottom);

        // Smaller DIP work area where baseMinWidth * 2 (1920) also exceeds work.Width.
        var compactWork = new WorkArea(0, 0, 1536, 824);
        var boxCompact = UiScale.ClampToWorkArea(100, 100, 3120, 1800, baseMinWidth: 960, baseMinHeight: 600, scalePercent: 200, compactWork);
        Assert.Equal(1536, boxCompact.MinWidth, 6);
        Assert.Equal(824, boxCompact.MinHeight, 6);
        Assert.Equal(1536, boxCompact.Width, 6);
        Assert.Equal(824, boxCompact.Height, 6);
        Assert.Equal(0, boxCompact.Left, 6);
        Assert.Equal(0, boxCompact.Top, 6);
    }

    [Fact]
    public void Negative_secondary_monitor_coordinates_preserve_left_monitor_placement_and_clamp_right_edge_overflow()
    {
        var leftMonitor = new WorkArea(-1920, 0, 1920, 1080);

        // Comfortably inside the left monitor: stays at (-1600, 100) after scaling from 100% to 150%.
        var kept = UiScale.ResizeForScale(left: -1600, top: 100, width: 480, height: 600, baseMinWidth: 380, baseMinHeight: 360, oldScalePercent: 100, newScalePercent: 150, leftMonitor);
        Assert.Equal(-1600, kept.Left, 6);
        Assert.Equal(100, kept.Top, 6);
        Assert.Equal(720, kept.Width, 6);
        Assert.Equal(900, kept.Height, 6);
        Assert.True(kept.Left >= leftMonitor.Left);
        Assert.True(kept.Left + kept.Width <= leftMonitor.Right);

        // Near the boundary x = 0: scaling 480x600 at x = -500 to 200% (960 wide) pulls Left back to -960 so title-bar close/scale buttons stay in [-1920..0].
        var pulledIn = UiScale.ResizeForScale(left: -500, top: 200, width: 480, height: 600, baseMinWidth: 380, baseMinHeight: 360, oldScalePercent: 100, newScalePercent: 200, leftMonitor);
        Assert.Equal(960, pulledIn.Width, 6);
        Assert.Equal(1080, pulledIn.Height, 6);
        Assert.Equal(-960, pulledIn.Left, 6);
        Assert.Equal(0, pulledIn.Top, 6);
        Assert.True(pulledIn.Left >= leftMonitor.Left);
        Assert.Equal(leftMonitor.Right, pulledIn.Left + pulledIn.Width, 6);
        Assert.True(pulledIn.Top >= leftMonitor.Top);
        Assert.True(pulledIn.Top + pulledIn.Height <= leftMonitor.Bottom);

        // Saved off-screen beyond the left/top edges of the secondary monitor is clamped back onto [-1920..0, 0..1080].
        var recovered = UiScale.ClampToWorkArea(left: -3200, top: -120, width: 420, height: 520, baseMinWidth: 300, baseMinHeight: 240, scalePercent: 100, leftMonitor);
        Assert.Equal(-1920, recovered.Left, 6);
        Assert.Equal(0, recovered.Top, 6);
        Assert.Equal(420, recovered.Width, 6);
        Assert.Equal(520, recovered.Height, 6);
    }
}
