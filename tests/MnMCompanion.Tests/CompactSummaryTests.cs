using MnMCompanion.History;
using MnMCompanion.Ledger;
using MnMCompanion.Loot;
using static MnMCompanion.Tests.Fixture;

namespace MnMCompanion.Tests;

/// <summary>
/// Compact ("mini") 3-row overlay projections for <c>Loot</c>, <c>Vendor</c>, <c>Items</c>, and <c>Harvest</c>
/// directly over <c>MnMCompanion.Core</c> domain summaries.
/// </summary>
public class CompactSummaryTests
{
    private static readonly DateTime T = new(2026, 10, 26, 20, 0, 0, DateTimeKind.Local);
    private static readonly DateOnly Day = DateOnly.FromDateTime(T);

    // ------------------------------------------------------------------ Mini Loot

    [Fact]
    public void Mini_Loot_shows_personal_coin_split_with_estimated_flag_and_top_three_items_across_the_whole_party()
    {
        var prices = new ItemValues(
        [
            ("Silver Mace", 500),
            ("Ruby Ring", 800),
            ("Bronze Shield", 300),
            ("Wolf Pelt", 120),
            ("Rat Ear", 25),
        ]);

        var social = new[]
        {
            // Brom loots divided purses where owner Aldric receives 1/5 (20 of 100, 40 of 200).
            Purse(T, "Brom", "Aldric", "a goblin", "0,0,1,0", split: "0,0,0,20"),
            Purse(T.AddSeconds(30), "Brom", "Aldric", "a goblin", "0,0,2,0", split: "0,0,0,40"),
            // Party members loot higher-worth items than Aldric across 4 distinct drops.
            Item(T.AddSeconds(31), "Brom", "Aldric", "a goblin", "Ruby Ring"),
            Item(T.AddSeconds(45), "Tamsin", "Aldric", "a goblin", "Silver Mace"),
            Item(T.AddSeconds(46), "Tamsin", "Aldric", "a goblin", "Silver Mace"),
            Item(T.AddSeconds(60), "Brom", "Aldric", "a goblin", "Bronze Shield"),
            Item(T.AddSeconds(75), "Pip", "Aldric", "a goblin", "Wolf Pelt"),
        };

        var character = new[]
        {
            // Aldric loots a 150cp purse while grouped (estimated 1/5 share = 30cp) and a low-worth personal item.
            Purse(T.AddSeconds(90), "Aldric", "Aldric", "a goblin", "0,0,1,50"),
            Item(T.AddSeconds(91), "Aldric", "Aldric", "a goblin", "Rat Ear"),
        };

        var events = LootExtractor.Extract(
            Parse(File(social), LedgerFileKind.Social)
                .Concat(Parse(File(character), LedgerFileKind.Character)));

        var day = LootDay.Of(Day, events, prices);

        // Personal coin split reflects 20 + 40 received + 30 estimated (not the 450cp group total) and flags EstimatedShares > 0.
        Assert.Equal(450, day.Totals.Copper);
        Assert.Equal(90, day.Ours.Copper);
        Assert.True(day.Ours.EstimatedShares > 0);
        Assert.Equal(1, day.Ours.EstimatedShares);
        Assert.Equal(25, day.Ours.ItemValue);
        Assert.Contains("My split: ~90cp", LootSummary.CoinLine(day.Totals.Copper, day.Ours, mine: true));

        // Compact top-3 ranks the whole party's drops by vendor worth, not personal-only drops.
        var topThree = day.ItemsWorth.Take(3).ToList();
        Assert.Equal(new[] { "Silver Mace", "Ruby Ring", "Bronze Shield" }, topThree.Select(i => i.Item));
        Assert.Equal(new[] { 1000L, 800L, 300L }, topThree.Select(i => i.Total));
    }

    [Fact]
    public void Mini_Loot_handles_exact_shares_fewer_than_three_items_and_empty_sessions()
    {
        var prices = new ItemValues([("Ruby Ring", 800), ("Rat Ear", 25)]);

        var empty = LootDay.Of(Day, [], prices);
        Assert.Equal(LootOurs.None, empty.Ours);
        Assert.Equal(0, empty.Ours.EstimatedShares);
        Assert.Empty(empty.ItemsWorth.Take(3));

        var twoItems = LootDay.Of(Day, LootExtractor.Extract(Parse(File(
            Purse(T, "Brom", "Aldric", "a goblin", "0,0,1,0", split: "0,0,0,25"),
            Item(T.AddSeconds(5), "Brom", "Aldric", "a goblin", "Ruby Ring"),
            Item(T.AddSeconds(10), "Aldric", "Aldric", "a goblin", "Rat Ear")), LedgerFileKind.Social)), prices);

        Assert.Equal(25, twoItems.Ours.Copper);
        Assert.Equal(0, twoItems.Ours.EstimatedShares);
        Assert.Equal(new[] { "Ruby Ring", "Rat Ear" }, twoItems.ItemsWorth.Take(3).Select(i => i.Item));
    }

    // ------------------------------------------------------------------ Mini Vendor

    [Fact]
    public void Mini_Vendor_computes_net_coin_and_slices_top_three_sold_and_bought_items()
    {
        var rows = new[]
        {
            new VendorRow(T, "Aldric", VendorDirection.Sold, "Fine Steel Longsword", 1, 900),
            new VendorRow(T.AddMinutes(1), "Aldric", VendorDirection.Sold, "Goblin Spear", 2, 220),
            new VendorRow(T.AddMinutes(2), "Aldric", VendorDirection.Sold, "goblin spear", 1, 110),
            new VendorRow(T.AddMinutes(3), "Aldric", VendorDirection.Sold, "Bronze Shield", 1, 250),
            new VendorRow(T.AddMinutes(4), "Aldric", VendorDirection.Sold, "Wolf Pelt", 4, 120),
            new VendorRow(T.AddMinutes(5), "Aldric", VendorDirection.Sold, "Rat Ear", 5, 40),
            new VendorRow(T.AddMinutes(6), "Aldric", VendorDirection.Bought, "Heavy Bandage", 20, 400),
            new VendorRow(T.AddMinutes(7), "Aldric", VendorDirection.Bought, "Iron Ration", 20, 260),
            new VendorRow(T.AddMinutes(8), "Aldric", VendorDirection.Bought, "Water Flask", 20, 100),
            new VendorRow(T.AddMinutes(9), "Aldric", VendorDirection.Bought, "Torch", 4, 24),
        };

        var totals = VendorSummary.Totals(rows);
        Assert.Equal(1640, totals.SoldCopper);
        Assert.Equal(784, totals.BoughtCopper);
        Assert.Equal(856, totals.Net);

        var topSold = VendorSummary.ByItem(rows, VendorDirection.Sold).Take(3).ToList();
        Assert.Equal(new[] { "Fine Steel Longsword", "Goblin Spear", "Bronze Shield" }, topSold.Select(i => i.Item));
        Assert.Equal(new[] { 900L, 330L, 250L }, topSold.Select(i => i.Total));
        Assert.Equal(2, topSold[1].Stacks);
        Assert.Equal(3, topSold[1].Units);

        var topBought = VendorSummary.ByItem(rows, VendorDirection.Bought).Take(3).ToList();
        Assert.Equal(new[] { "Heavy Bandage", "Iron Ration", "Water Flask" }, topBought.Select(i => i.Item));
        Assert.Equal(new[] { 400L, 260L, 100L }, topBought.Select(i => i.Total));
    }

    [Fact]
    public void Mini_Vendor_handles_negative_net_and_empty_rows()
    {
        var emptyTotals = VendorSummary.Totals([]);
        Assert.Equal(0, emptyTotals.Net);
        Assert.Empty(VendorSummary.ByItem([], VendorDirection.Sold).Take(3));
        Assert.Empty(VendorSummary.ByItem([], VendorDirection.Bought).Take(3));

        var deficit = new[]
        {
            new VendorRow(T, "Aldric", VendorDirection.Sold, "Rat Ear", 2, 16),
            new VendorRow(T.AddMinutes(1), "Aldric", VendorDirection.Bought, "Spell Scroll", 1, 500),
        };
        Assert.Equal(-484, VendorSummary.Totals(deficit).Net);
        Assert.Single(VendorSummary.ByItem(deficit, VendorDirection.Sold).Take(3));
        Assert.Single(VendorSummary.ByItem(deficit, VendorDirection.Bought).Take(3));
    }

    // ------------------------------------------------------------------ Mini Items

    [Fact]
    public void Mini_Items_totals_all_change_kinds_and_orders_latest_three_rows_newest_first()
    {
        // Deliberately unsorted input covering Dropped, PickedUp (including a within-window take-back), and Destroyed.
        var rows = new[]
        {
            new ItemMoveRow(T.AddMinutes(2), "Aldric", ItemMoveKind.Dropped, "Boar Meat", 4, "nightharborw"),
            new ItemMoveRow(T, "Aldric", ItemMoveKind.Dropped, "Rusty Dagger", 1, "nightharborw"),
            new ItemMoveRow(T.AddMinutes(14), "Aldric", ItemMoveKind.PickedUp, "Foraged Root", 1, "fallenpass"),
            new ItemMoveRow(T.AddMinutes(5), "Aldric", ItemMoveKind.PickedUp, "Boar Meat", 2, "nightharborw"),
            new ItemMoveRow(T.AddMinutes(9), "Aldric", ItemMoveKind.Destroyed, "Tattered Cloth", 3, "fallenpass"),
            new ItemMoveRow(T.AddMinutes(11), "Aldric", ItemMoveKind.Dropped, "Cracked Staff", 1, "fallenpass"),
        };

        var totals = ItemChangeSummary.Totals(rows);
        Assert.Equal(6, totals.Dropped);
        Assert.Equal(3, totals.PickedUp);
        Assert.Equal(3, totals.Destroyed);
        Assert.Equal(2, totals.TakenBack);
        Assert.Equal(12, totals.Dropped + totals.PickedUp + totals.Destroyed);

        var latestThree = rows.OrderByDescending(r => r.Time).Take(3).ToList();
        Assert.Equal(new[] { "Foraged Root", "Cracked Staff", "Tattered Cloth" }, latestThree.Select(r => r.Item));
        Assert.Equal(new[] { ItemMoveKind.PickedUp, ItemMoveKind.Dropped, ItemMoveKind.Destroyed }, latestThree.Select(r => r.Kind));

        var emptyTotals = ItemChangeSummary.Totals([]);
        Assert.Equal(new ItemChangeTotals(0, 0, 0, 0), emptyTotals);
    }

    // ------------------------------------------------------------------ Mini Harvest

    [Fact]
    public void Mini_Harvest_reports_hourly_rate_after_one_active_minute_or_unit_count_for_short_windows_and_top_three_items()
    {
        var rows = new[]
        {
            new HarvestRow(T, "Aldric", "Copper Ore", 4, "nightharborw"),
            new HarvestRow(T.AddMinutes(3), "Aldric", "Pine Log", 6, "nightharborw"),
            new HarvestRow(T.AddMinutes(6), "Aldric", "Flax Stalk", 5, "nightharborw"),
            // 30-minute pause exceeds HarvestSummary.MaxGap (10m) and is excluded from active time.
            new HarvestRow(T.AddMinutes(36), "Aldric", "River Clay", 3, "fallenpass"),
            new HarvestRow(T.AddMinutes(42), "Aldric", "copper ore", 5, "fallenpass"),
            new HarvestRow(T.AddMinutes(45), "Aldric", "Wild Sage", 7, "fallenpass"),
        };

        var totals = HarvestSummary.Totals(rows);
        Assert.Equal(6, totals.Harvests);
        Assert.Equal(30, totals.Units);
        Assert.Equal(5, totals.Kinds);
        Assert.Equal(TimeSpan.FromMinutes(15), totals.Active);
        Assert.NotNull(totals.PerHour);
        Assert.Equal(120.0, totals.PerHour!.Value, 6);

        var topThree = HarvestSummary.ByItem(rows).Take(3).ToList();
        Assert.Equal(new[] { "Copper Ore", "Wild Sage", "Pine Log" }, topThree.Select(i => i.Item));
        Assert.Equal(new[] { 9, 7, 6 }, topThree.Select(i => i.Units));

        // Under 1 minute of active gathering: PerHour is null and compact headline falls back to Units.
        var shortWindow = new[]
        {
            new HarvestRow(T, "Aldric", "Pine Log", 3, "nightharborw"),
            new HarvestRow(T.AddSeconds(45), "Aldric", "Copper Ore", 2, "nightharborw"),
        };
        var shortTotals = HarvestSummary.Totals(shortWindow);
        Assert.Null(shortTotals.PerHour);
        Assert.Equal(5, shortTotals.Units);
        Assert.Equal(new[] { "Pine Log", "Copper Ore" }, HarvestSummary.ByItem(shortWindow).Take(3).Select(i => i.Item));

        var emptyTotals = HarvestSummary.Totals([]);
        Assert.Null(emptyTotals.PerHour);
        Assert.Equal(0, emptyTotals.Units);
        Assert.Empty(HarvestSummary.ByItem([]).Take(3));
    }
}
