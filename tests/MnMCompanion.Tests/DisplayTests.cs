using System.Text.Json;
using MnMCompanion.Display;

namespace MnMCompanion.Tests;

/// <summary>The size and typeface of the windows: whatever is saved, only a sane size and a listed, installed typeface are ever used.</summary>
public class DisplayTests
{
    [Theory]
    [InlineData(100, 100)]
    [InlineData(10, 70)]
    [InlineData(-5, 70)]
    [InlineData(1000, 250)]
    [InlineData(125, 125)]
    public void Any_saved_size_is_kept_inside_the_limits(int saved, int used) => Assert.Equal(used, UiScale.Clamp(saved));

    [Theory]
    [InlineData(100, 1, 110)]
    [InlineData(100, -1, 90)]
    [InlineData(125, 1, 130)]
    [InlineData(125, -1, 120)]
    [InlineData(130, -1, 120)]
    [InlineData(250, 1, 250)]
    [InlineData(70, -1, 70)]
    [InlineData(72, -1, 70)]
    [InlineData(244, 1, 250)]
    [InlineData(100, 0, 100)]
    public void A_step_lands_on_a_multiple_of_ten_and_stops_at_the_ends(int from, int direction, int to) => Assert.Equal(to, UiScale.Next(from, direction));

    [Fact]
    public void Every_step_up_and_down_from_anywhere_stays_in_range_and_moves_the_right_way()
    {
        for (int p = UiScale.Min; p <= UiScale.Max; p++)
        {
            var up = UiScale.Next(p, 1);
            var down = UiScale.Next(p, -1);
            Assert.InRange(up, UiScale.Min, UiScale.Max);
            Assert.InRange(down, UiScale.Min, UiScale.Max);
            Assert.True(up >= p && down <= p, $"from {p}: up {up}, down {down}");
            if (p < UiScale.Max) Assert.True(up > p, $"from {p} up should move");
            if (p > UiScale.Min) Assert.True(down < p, $"from {p} down should move");
        }
    }

    [Fact]
    public void The_buttons_are_all_inside_the_limits_and_include_the_normal_size()
    {
        Assert.All(UiScale.Presets, p => Assert.InRange(p, UiScale.Min, UiScale.Max));
        Assert.Contains(UiScale.Default, UiScale.Presets);
        Assert.Equal(UiScale.Presets.OrderBy(p => p), UiScale.Presets);
    }

    [Theory]
    [InlineData(480, 100, 150, 720)]
    [InlineData(600, 100, 150, 900)]
    [InlineData(720, 150, 100, 480)]
    [InlineData(333, 100, 110, 366)]
    public void A_window_grows_in_proportion_so_its_contents_keep_their_room(double length, int from, int to, double grown) => Assert.Equal(grown, UiScale.Resized(length, from, to));

    [Fact]
    public void A_length_that_is_not_a_number_is_left_alone()
    {
        Assert.True(double.IsNaN(UiScale.Resized(double.NaN, 100, 150)));
        Assert.True(double.IsInfinity(UiScale.Resized(double.PositiveInfinity, 100, 150)));
    }

    [Fact]
    public void Factor_is_the_percent_over_a_hundred()
    {
        Assert.Equal(1.0, UiScale.Factor(100));
        Assert.Equal(1.5, UiScale.Factor(150));
        Assert.Equal(0.7, UiScale.Factor(5));
        Assert.Equal(2.5, UiScale.Factor(900));
    }

    private static readonly Func<string, bool> Everything = _ => true;
    private static readonly Func<string, bool> Nothing = _ => false;

    [Theory]
    [InlineData("Verdana", "Verdana")]
    [InlineData("verdana", "Verdana")]
    [InlineData("  Trebuchet MS ", "Trebuchet MS")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Comic Sans MS", null)]
    [InlineData("Arial, Impact", null)]
    [InlineData("Segoe UI Variable", null)]
    public void A_saved_typeface_means_a_listed_one_or_the_programs_own(string? saved, string? used) => Assert.Equal(used, UiFonts.Normalise(saved, Everything));

    [Fact]
    public void A_listed_typeface_that_is_not_installed_here_means_the_programs_own()
    {
        Assert.Null(UiFonts.Normalise("Verdana", Nothing));
        Assert.Null(UiFonts.Normalise("Verdana", n => n == "Arial"));
        Assert.Equal("Arial", UiFonts.Normalise("Arial", n => n == "Arial"));
    }

    [Fact]
    public void Only_the_installed_ones_are_offered_and_in_list_order()
    {
        Assert.Equal(UiFonts.Offered, UiFonts.Available(Everything));
        Assert.Empty(UiFonts.Available(Nothing));
        Assert.Equal(["Arial", "Consolas"], UiFonts.Available(n => n is "Consolas" or "Arial"));
    }

    [Fact]
    public void The_list_has_no_commas_so_a_name_cannot_turn_into_several_families()
    {
        Assert.All(UiFonts.Offered, n => Assert.DoesNotContain(',', n));
        Assert.Equal(UiFonts.Offered.Count, UiFonts.Offered.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_family_lists_fall_back_to_segoe_ui_and_default_to_the_programs_own()
    {
        Assert.Equal(UiFonts.DefaultText, UiFonts.Text(null));
        Assert.Equal(UiFonts.DefaultDisplay, UiFonts.Display(null));
        Assert.Equal("Verdana, Segoe UI", UiFonts.Text("Verdana"));
        Assert.Equal("Verdana, Segoe UI", UiFonts.Display("Verdana"));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MnMCompanion.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MnMCompanion.sln not found above " + AppContext.BaseDirectory);
    }

    private sealed class CompactSettingsContract
    {
        public List<string> PageWindowsCompact { get; set; } = [];
        public Dictionary<string, double> PageWindowsCompactHeight { get; set; } = [];
    }

    [Fact]
    public void Compact_window_heights_scale_in_proportion_separately_from_full_window_height()
    {
        double compactMin = 96 * UiScale.Factor(150);
        double compactMax = 260 * UiScale.Factor(150);
        Assert.Equal(144.0, compactMin, 6);
        Assert.Equal(390.0, compactMax, 6);

        double manualCompactAt150 = UiScale.Resized(172.0, 100, 150);
        double expandedAt150 = UiScale.Resized(580.0, 100, 150);
        Assert.Equal(258.0, manualCompactAt150);
        Assert.Equal(870.0, expandedAt150);
        Assert.Equal(172.0, UiScale.Resized(manualCompactAt150, 150, 100));
        Assert.Equal(580.0, UiScale.Resized(expandedAt150, 150, 100));
    }

    [Fact]
    public void Compact_window_settings_round_trip_through_json()
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var original = new CompactSettingsContract
        {
            PageWindowsCompact = ["Loot", "Harvest"],
            PageWindowsCompactHeight = new Dictionary<string, double> { ["Loot"] = 172.0 },
        };

        var json = JsonSerializer.Serialize(original, jsonOptions);
        var loaded = JsonSerializer.Deserialize<CompactSettingsContract>(json, jsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(["Loot", "Harvest"], loaded.PageWindowsCompact);
        Assert.Single(loaded.PageWindowsCompactHeight);
        Assert.Equal(172.0, loaded.PageWindowsCompactHeight["Loot"]);

        var empty = JsonSerializer.Deserialize<CompactSettingsContract>("{}", jsonOptions);
        Assert.NotNull(empty);
        Assert.Empty(empty.PageWindowsCompact);
        Assert.Empty(empty.PageWindowsCompactHeight);

        var settingsSource = File.ReadAllText(Path.Combine(Root(), "src", "MnMCompanion", "Settings.cs"));
        Assert.Contains("public List<string> PageWindowsCompact { get; set; } = [];", settingsSource);
        Assert.Contains("public Dictionary<string, double> PageWindowsCompactHeight { get; set; } = [];", settingsSource);
    }

    [Fact]
    public void Snapshot_compact_and_size_arguments_parse_popout_compact_and_explicit_size()
    {
        static (bool PopOut, bool Compact, bool HasSize, int Width, int Height) ParseSnapshotFlags(string[] args)
        {
            string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            int w = 390, h = 600;
            var size = Value("--size");
            if (size is not null && size.Split('x') is [var sw, var sh] && int.TryParse(sw, out var pw) && int.TryParse(sh, out var ph)) (w, h) = (pw, ph);
            return (args.Contains("--popout"), args.Contains("--compact"), size is not null, w, h);
        }

        var autoSized = ParseSnapshotFlags(["--snapshot", "out.png", "--window", "--mini", "Loot", "--popout", "--compact"]);
        Assert.True(autoSized.PopOut);
        Assert.True(autoSized.Compact);
        Assert.False(autoSized.HasSize);
        Assert.Equal(390, autoSized.Width);
        Assert.Equal(600, autoSized.Height);

        var explicitSized = ParseSnapshotFlags(["--snapshot", "out.png", "--window", "--mini", "Loot", "--popout", "--compact", "--size", "420x180"]);
        Assert.True(explicitSized.PopOut);
        Assert.True(explicitSized.Compact);
        Assert.True(explicitSized.HasSize);
        Assert.Equal(420, explicitSized.Width);
        Assert.Equal(180, explicitSized.Height);

        var appSource = File.ReadAllText(Path.Combine(Root(), "src", "MnMCompanion", "App.xaml.cs"));
        Assert.Contains("bool Compact = false, bool HasSize = false", appSource);
        Assert.Contains("args.Contains(\"--compact\")", appSource);
        Assert.Contains("var size = Value(\"--size\");", appSource);
        Assert.Contains("size is not null", appSource);
    }
}
