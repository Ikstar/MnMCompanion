using MnMCompanion.Loot;
using static MnMCompanion.Tests.Fixture;

namespace MnMCompanion.Tests;

public class CustomPricesTests
{
    private static readonly DateTime T = new(2026, 10, 24, 9, 0, 0);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("45", 45)]
    [InlineData("1,500", 1_500)]
    [InlineData("0cp", 0)]
    [InlineData("3sp 41cp", 341)]
    [InlineData("1pp 2gp 3sp 4cp", 1_020_304)]
    [InlineData("1gp25sp", 12_500)]
    [InlineData("5 sp", 500)]
    [InlineData("2 gold 10 silver", 21_000)]
    [InlineData("2p 5g 3s 1c", 2_050_301)]
    [InlineData("1.5gp", 15_000)]
    [InlineData(".5sp", 50)]
    [InlineData("2.25pp", 2_250_000)]
    [InlineData("1000000pp", CustomPrices.MaxCopper)]
    public void Coin_input_parses_copper_and_denominated_amounts(string text, long expected)
    {
        Assert.True(CustomPrices.TryParseCoin(text, out var copper, out var error));
        Assert.Null(error);
        Assert.Equal(expected, copper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-5cp")]
    [InlineData("~1sp")]
    [InlineData("+50cp")]
    [InlineData("1.5cp")]
    [InlineData("0.123sp")]
    [InlineData("5dp")]
    [InlineData("hello")]
    [InlineData("1gp 2gp")]
    [InlineData("5sp 10s")]
    [InlineData("5sp 20")]
    [InlineData("10 20sp")]
    [InlineData("1000001pp")]
    [InlineData("1.gp")]
    public void Invalid_coin_input_is_rejected_with_an_explanation(string text)
    {
        Assert.False(CustomPrices.TryParseCoin(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Prices_are_saved_on_disk_reloaded_and_removed_case_insensitively()
    {
        using var s = new Scratch();
        var book = new CustomPrices(s.Path);
        int events = 0;
        book.Changed += () => events++;

        Assert.True(book.Set("  Black Pearl  ", 25_000));
        Assert.True(book.Set("No-Drop\r\n Pebble ", 0));
        Assert.True(System.IO.File.Exists(Path.Combine(s.Path, CustomPrices.FileName)));
        Assert.Equal(2, events);

        // Overwrite case-insensitively with a different copper amount
        Assert.True(book.Set("black pearl", 30_000));
        Assert.Equal(2, book.Count);
        Assert.Equal(3, events);

        var loaded = new CustomPrices(s.Path);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet("BLACK PEARL", out var pearl));
        Assert.Equal(30_000, pearl);
        Assert.True(loaded.TryGet("No-Drop Pebble", out var pebble));
        Assert.Equal(0, pebble);

        Assert.True(loaded.Remove("BLACK PEARL"));
        Assert.False(loaded.TryGet("Black Pearl", out _));

        // External write on disk is picked up by Reload()
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(s.Path, CustomPrices.FileName), DateTime.UtcNow.AddSeconds(5));
        Assert.True(book.Reload());
        Assert.Single(book.All);
        Assert.False(book.TryGet("Black Pearl", out _));
        Assert.False(book.Reload());

        Assert.Equal(1, book.ClearAll());
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Corrupt_file_and_control_only_names_are_ignored_safely()
    {
        using var s = new Scratch();
        s.Write(CustomPrices.FileName, "{ not valid json");
        var book = new CustomPrices(s.Path);
        Assert.Equal(0, book.Count);
        Assert.False(book.Set("\r\n\t ", 10));
        Assert.False(book.Set("Rat Ear", -1));
    }

    [Fact]
    public void Custom_prices_override_vendor_public_charged_and_estimated_without_polluting_families()
    {
        var rows = Parse(File(
            Sale(T, "Aldric", "Rat Ear", 1, "0 platinum 0 gold 0 silver 5 copper"),
            Sale(T, "Aldric", "Corroded Bronze Chain Gloves", 1, "0 platinum 0 gold 0 silver 135 copper")));
        var list = new PublicPrices([
            ("Wolf Pelt", new PublicPrice(20, 74)),
            ("Iron Mace", new PublicPrice(0, 370)),
        ], generated: null);

        var custom = new CustomPrices([
            ("Rat Ear", 500),
            ("Wolf Pelt", 0),
            ("Iron Mace", 1_000),
            ("Corroded Bronze Chain Mask", 9_999),
            ("Rusty Iron Longsword", 777),
        ]);

        var values = ItemValues.FromLedger(rows, list, custom);

        // Custom wins over Sold, Public, Charged, Estimated
        Assert.Equal(new ItemValue(500, ValueSource.Custom), values.Of("rat ear"));
        Assert.Equal(new ItemValue(5, ValueSource.Sold), values.NaturalOf("rat ear"));

        var pelt = values.Of("Wolf Pelt");
        Assert.Equal(new ItemValue(0, ValueSource.Custom), pelt);
        Assert.True(pelt.Known);
        Assert.True(pelt.Custom);
        Assert.False(pelt.Estimated);
        Assert.Equal(new ItemValue(20, ValueSource.Public), values.NaturalOf("Wolf Pelt"));

        Assert.Equal(new ItemValue(1_000, ValueSource.Custom), values.Of("Iron Mace"));
        Assert.Equal(ValueSource.Charged, values.NaturalOf("Iron Mace").Source);

        // Custom prices never enter _family
        Assert.False(values.Of("Rusty Iron Broadsword").Known);
        // Real vendor sale of Gloves still provides the family price for Mask (NaturalOf) and Boots (Of), unaffected by Mask's 9999cp custom price
        Assert.Equal(new ItemValue(9_999, ValueSource.Custom), values.Of("Corroded Bronze Chain Mask"));
        Assert.Equal(new ItemValue(135, ValueSource.Estimated), values.NaturalOf("Corroded Bronze Chain Mask"));
        Assert.Equal(new ItemValue(135, ValueSource.Estimated), values.Of("Corroded Bronze Chain Boots"));

        // Clearing custom prices via WithCustom restores natural prices immediately
        var reverted = values.WithCustom(CustomPrices.Empty);
        Assert.Equal(new ItemValue(5, ValueSource.Sold), reverted.Of("Rat Ear"));
        Assert.Equal(new ItemValue(20, ValueSource.Public), reverted.Of("Wolf Pelt"));
    }

    [Fact]
    public void Zero_copper_custom_price_clears_unpriced_flags_and_formats_clearly()
    {
        var events = new[]
        {
            new LootEvent(T, LootKind.Item, "Brom", false, "a rat", "Quest Pebble", 2, 0, 0),
            new LootEvent(T.AddSeconds(1), LootKind.Item, "Brom", false, "a rat", "Mystery Fang", 3, 0, 0),
        };
        var values = new ItemValues([], custom: new CustomPrices([("Quest Pebble", 0)]));
        var day = LootDay.Of(new DateOnly(2026, 10, 24), events, values);

        Assert.Equal(0, day.ItemValue);
        Assert.Equal(2, day.Priced);
        Assert.Equal(2, day.Custom);
        Assert.Equal(3, day.Unpriced);
        // 0cp known item sorts ahead of truly unpriced item even though both have Total == 0
        Assert.Equal(["Quest Pebble", "Mystery Fang"], day.ItemsWorth.Select(i => i.Item));
        Assert.Contains("(custom)", LootSummary.ItemLine(day.ItemsWorth[0]));
        Assert.DoesNotContain("~", LootSummary.ItemLine(day.ItemsWorth[0]));

        // When every item is 0cp custom-priced, day has no unpriced '+' flag and Breakdown still prints items worth
        var allZero = LootValuesAllZero(events, new ItemValues([], custom: new CustomPrices([("Quest Pebble", 0), ("Mystery Fang", 0)])));
        Assert.Equal(0, allZero.Unpriced);
        var text = LootSummary.Breakdown(new DateOnly(2026, 10, 24), events,
            values: new ItemValues([], custom: new CustomPrices([("Quest Pebble", 0), ("Mystery Fang", 0)])));
        Assert.Contains("items 0cp", text);
        Assert.Contains(" 0cp", text);
    }

    private static LootDay LootValuesAllZero(LootEvent[] events, ItemValues values) => LootDay.Of(new DateOnly(2026, 10, 24), events, values);

    [Fact]
    public void LootStore_applies_persists_and_syncs_custom_prices_across_stores()
    {
        using var s = new Scratch();
        var today = DateTime.Now.AddMinutes(-5);
        s.Write(Path.Combine("ledger", "srv", "Aldric", "Ledger", "Aldric_Character_x.json"), File(
            Item(today, "Aldric", "Aldric", "a rat", "Rare Gem"),
            Sale(today.AddSeconds(-10), "Aldric", "Rat Ear", 1, "0 platinum 0 gold 0 silver 5 copper")));

        var ledgerDir = Path.Combine(s.Path, "ledger");
        var stateDir = Path.Combine(s.Path, "state");

        var primary = new LootStore(stateDir, ledgerDir);
        primary.Select(primary.Sources.Single());
        Assert.True(primary.Refresh());

        var initial = primary.Values;
        Assert.False(primary.Refresh());
        Assert.Same(initial, primary.Values);
        Assert.False(primary.Values.Of("Rare Gem").Known);

        // Secondary store (e.g., HistoryWindow) shares CustomPrices
        var secondary = new LootStore(stateDir, ledgerDir, primary.CustomPrices);
        secondary.Select(secondary.Sources.Single());
        secondary.Refresh();

        Assert.True(primary.SetCustomPrice("Rare Gem", 50_000));
        Assert.NotSame(initial, primary.Values);
        Assert.Equal(new ItemValue(50_000, ValueSource.Custom), primary.Values.Of("Rare Gem"));

        Assert.True(secondary.SyncCustom());
        Assert.Equal(new ItemValue(50_000, ValueSource.Custom), secondary.Values.Of("Rare Gem"));
        Assert.False(secondary.SyncCustom());

        // Persisted to a brand-new store pointed at the same stateRoot
        var restarted = new LootStore(stateDir, ledgerDir);
        restarted.Select(restarted.Sources.Single());
        restarted.Refresh();
        Assert.Equal(new ItemValue(50_000, ValueSource.Custom), restarted.Values.Of("Rare Gem"));

        Assert.True(primary.ClearCustomPrice("Rare Gem"));
        Assert.False(primary.Values.Of("Rare Gem").Known);
        Assert.True(secondary.SyncCustom());
        Assert.False(secondary.Values.Of("Rare Gem").Known);
    }
}
