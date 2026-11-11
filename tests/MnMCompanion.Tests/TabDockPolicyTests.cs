namespace MnMCompanion.Tests;

/// <summary>
/// Tab docking rules: canonical tab order, compact-mode eligibility, the at-least-one-docked-tab invariant,
/// next-tab selection when detaching, and startup normalization of persisted tab state.
/// </summary>
public class TabDockPolicyTests
{
    [Fact]
    public void PageNames_and_CompactPages_have_canonical_order_and_lookup_is_case_insensitive()
    {
        Assert.Equal(new[] { "Map", "Loot", "Vendor", "Trades", "Items", "Harvest", "Recipes", "Market" }, TabDockPolicy.PageNames);
        Assert.Equal(new[] { "Loot", "Vendor", "Items", "Harvest" }, TabDockPolicy.CompactPages);

        Assert.True(TabDockPolicy.IsKnownPage("loot", out var loot));
        Assert.Equal("Loot", loot);
        Assert.True(TabDockPolicy.IsKnownPage("RECIPES", out var recipes));
        Assert.Equal("Recipes", recipes);
        Assert.False(TabDockPolicy.IsKnownPage(null, out var none));
        Assert.Equal("", none);
        Assert.False(TabDockPolicy.IsKnownPage("UnknownTab", out _));
    }

    [Theory]
    [InlineData("Loot", true)]
    [InlineData("loot", true)]
    [InlineData("Vendor", true)]
    [InlineData("Items", true)]
    [InlineData("Harvest", true)]
    [InlineData("HARVEST", true)]
    [InlineData("Map", false)]
    [InlineData("Trades", false)]
    [InlineData("Recipes", false)]
    [InlineData("Market", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("FutureTab", false)]
    public void SupportsCompact_allows_only_the_four_personal_summary_tabs(string? page, bool expected) =>
        Assert.Equal(expected, TabDockPolicy.SupportsCompact(page));

    [Fact]
    public void DockedPages_treats_missing_and_closed_entries_as_docked_in_PageNames_order()
    {
        Assert.Equal(TabDockPolicy.PageNames, TabDockPolicy.DockedPages(new Dictionary<string, TabSlot>()));

        var sparse = new Dictionary<string, TabSlot>
        {
            ["Harvest"] = new("Harvest", Open: true, Scale: 150, OnTop: true, Compact: true),
            ["Loot"] = new("Loot", Open: true, Scale: 125, OnTop: true, Compact: false),
            ["Vendor"] = new("Vendor", Open: false, Scale: 175, OnTop: false, Compact: true),
        };

        Assert.Equal(new[] { "Map", "Vendor", "Trades", "Items", "Recipes", "Market" }, TabDockPolicy.DockedPages(sparse));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(8, true)]
    public void CanDetach_forbids_detaching_the_last_remaining_docked_tab(int dockedCount, bool expected) =>
        Assert.Equal(expected, TabDockPolicy.CanDetach(dockedCount));

    [Fact]
    public void NextDockedPage_picks_following_tab_for_first_or_middle_and_preceding_tab_for_last()
    {
        var docked = new[] { "Map", "Loot", "Vendor", "Market" };

        Assert.Equal("Loot", TabDockPolicy.NextDockedPage(docked, "Map"));
        Assert.Equal("Vendor", TabDockPolicy.NextDockedPage(docked, "Loot"));
        Assert.Equal("Vendor", TabDockPolicy.NextDockedPage(docked, "loot"));
        Assert.Equal("Vendor", TabDockPolicy.NextDockedPage(docked, "Market"));
        Assert.Equal("Map", TabDockPolicy.NextDockedPage(new[] { "Map", "Market" }, "Market"));
    }

    [Fact]
    public void Normalize_repairs_all_eight_open_by_force_docking_miniPage_while_preserving_its_slot_settings()
    {
        var allOpen = TabDockPolicy.PageNames.ToDictionary(
            p => p,
            p => new TabSlot(p, Open: true, Scale: p == "Harvest" ? 175 : 125, OnTop: p != "Harvest", Compact: TabDockPolicy.SupportsCompact(p)));

        var normalized = TabDockPolicy.Normalize("harvest", allOpen);

        Assert.Equal("Harvest", normalized.ActiveDockedPage);
        Assert.Equal(new[] { "Harvest" }, TabDockPolicy.DockedPages(normalized.Tabs));

        var harvest = normalized.Tabs["Harvest"];
        Assert.False(harvest.Open);
        Assert.Equal(175, harvest.Scale);
        Assert.False(harvest.OnTop);
        Assert.True(harvest.Compact);
        Assert.Equal(7, normalized.Tabs.Values.Count(s => s.Open));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RemovedPage")]
    public void Normalize_repairs_all_eight_open_with_Loot_when_miniPage_is_null_or_unknown(string? miniPage)
    {
        var allOpen = TabDockPolicy.PageNames.ToDictionary(
            p => p,
            p => new TabSlot(p, Open: true, Scale: p == "Loot" ? 150 : 100, OnTop: p != "Loot", Compact: p == "Loot"));

        var normalized = TabDockPolicy.Normalize(miniPage, allOpen);

        Assert.Equal("Loot", normalized.ActiveDockedPage);
        Assert.Equal(new[] { "Loot" }, TabDockPolicy.DockedPages(normalized.Tabs));

        var loot = normalized.Tabs["Loot"];
        Assert.False(loot.Open);
        Assert.Equal(150, loot.Scale);
        Assert.False(loot.OnTop);
        Assert.True(loot.Compact);
    }

    [Fact]
    public void Normalize_reassigns_ActiveDockedPage_to_first_docked_tab_when_miniPage_is_detached()
    {
        var raw = new Dictionary<string, TabSlot>
        {
            ["Map"] = new("Map", Open: true, Scale: 100, OnTop: true, Compact: false),
            ["Loot"] = new("Loot", Open: true, Scale: 150, OnTop: true, Compact: true),
        };

        var fromDetached = TabDockPolicy.Normalize("Loot", raw);
        Assert.Equal("Vendor", fromDetached.ActiveDockedPage);

        var mapStillDocked = TabDockPolicy.Normalize("Loot", new Dictionary<string, TabSlot>
        {
            ["Loot"] = new("Loot", Open: true, Scale: 150, OnTop: true, Compact: true),
        });
        Assert.Equal("Map", mapStillDocked.ActiveDockedPage);

        var alreadyDocked = TabDockPolicy.Normalize("Recipes", raw);
        Assert.Equal("Recipes", alreadyDocked.ActiveDockedPage);
    }

    [Fact]
    public void Normalize_strips_unknown_keys_resolves_case_duplicates_snaps_scale_and_clears_invalid_compact_flags()
    {
        var raw = new Dictionary<string, TabSlot>(StringComparer.Ordinal)
        {
            ["FutureOverlay"] = new("FutureOverlay", Open: true, Scale: 150, OnTop: true, Compact: true),
            [""] = new("", Open: true, Scale: 125, OnTop: true, Compact: false),
            // Duplicate casing where closed precedes open in Ordinal order: Open == true wins.
            ["LOOT"] = new("LOOT", Open: false, Scale: 100, OnTop: true, Compact: false),
            ["loot"] = new("loot", Open: true, Scale: 188, OnTop: false, Compact: true),
            // Duplicate casing where open precedes closed in Ordinal order: Open == true still wins.
            ["Vendor"] = new("Vendor", Open: true, Scale: 113, OnTop: false, Compact: true),
            ["vendor"] = new("vendor", Open: false, Scale: 100, OnTop: true, Compact: false),
            // Duplicate casing where both are closed: first in StringComparer.Ordinal wins.
            ["ITEMS"] = new("ITEMS", Open: false, Scale: 175, OnTop: false, Compact: true),
            ["items"] = new("items", Open: false, Scale: 125, OnTop: true, Compact: false),
            // Non-compact pages with Compact = true and non-preset scales.
            ["map"] = new("map", Open: true, Scale: 40, OnTop: true, Compact: true),
            ["Trades"] = new("Trades", Open: false, Scale: 137, OnTop: true, Compact: true),
            ["RECIPES"] = new("RECIPES", Open: false, Scale: 163, OnTop: false, Compact: true),
            ["Market"] = new("Market", Open: false, Scale: 350, OnTop: true, Compact: true),
        };

        var normalized = TabDockPolicy.Normalize("Trades", raw);

        Assert.False(normalized.Tabs.ContainsKey("FutureOverlay"));
        Assert.False(normalized.Tabs.ContainsKey(""));
        Assert.Equal(7, normalized.Tabs.Count);

        var loot = normalized.Tabs["Loot"];
        Assert.Equal("Loot", loot.Name);
        Assert.True(loot.Open);
        Assert.Equal(200, loot.Scale);
        Assert.False(loot.OnTop);
        Assert.True(loot.Compact);

        var vendor = normalized.Tabs["Vendor"];
        Assert.Equal("Vendor", vendor.Name);
        Assert.True(vendor.Open);
        Assert.Equal(125, vendor.Scale);
        Assert.False(vendor.OnTop);
        Assert.True(vendor.Compact);

        var items = normalized.Tabs["Items"];
        Assert.Equal("Items", items.Name);
        Assert.False(items.Open);
        Assert.Equal(175, items.Scale);
        Assert.False(items.OnTop);
        Assert.True(items.Compact);

        Assert.False(normalized.Tabs["Map"].Compact);
        Assert.Equal(100, normalized.Tabs["Map"].Scale);
        Assert.False(normalized.Tabs["Trades"].Compact);
        Assert.Equal(125, normalized.Tabs["Trades"].Scale);
        Assert.False(normalized.Tabs["Recipes"].Compact);
        Assert.Equal(175, normalized.Tabs["Recipes"].Scale);
        Assert.False(normalized.Tabs["Market"].Compact);
        Assert.Equal(200, normalized.Tabs["Market"].Scale);

        Assert.Equal("Trades", normalized.ActiveDockedPage);

        var empty = TabDockPolicy.Normalize(null, null);
        Assert.Empty(empty.Tabs);
        Assert.Equal("Loot", empty.ActiveDockedPage);
    }
}
