using MnMCompanion.Loot;
using MnMCompanion.Market;
using MnMCompanion.Recipes;
using static MnMCompanion.Tests.Fixture;

namespace MnMCompanion.Tests;

public class ScrapItemsTests
{
    private static readonly DateTime T = new(2026, 10, 24, 9, 0, 0);

    // ------------------------------------------------------------------ 1. Parsing yields

    [Theory]
    [InlineData("4 Bronze Scraps", new[] { "Bronze Scraps:4" })]
    [InlineData("4x Bronze Scraps", new[] { "Bronze Scraps:4" })]
    [InlineData("4× Bronze Scraps", new[] { "Bronze Scraps:4" })]
    [InlineData("Bronze Scraps x4", new[] { "Bronze Scraps:4" })]
    [InlineData("Bronze Scraps ×4", new[] { "Bronze Scraps:4" })]
    [InlineData("Bronze Scraps (4)", new[] { "Bronze Scraps:4" })]
    [InlineData("Cloth Scraps", new[] { "Cloth Scraps:1" })]
    [InlineData("2 Bronze Scraps, 2 Rawhide Scraps", new[] { "Bronze Scraps:2", "Rawhide Scraps:2" })]
    [InlineData("2x Bronze Scraps + 1x Rawhide Scraps", new[] { "Bronze Scraps:2", "Rawhide Scraps:1" })]
    [InlineData("2 Bronze Scraps, 3 bronze scraps", new[] { "Bronze Scraps:5" })]
    public void Scrap_yield_input_parses_single_multi_and_duplicate_materials(string input, string[] expected)
    {
        Assert.True(ScrapItems.TryParseYields(input, "Corroded Bronze Axe", out var yields, out var error));
        Assert.Null(error);
        Assert.Equal(expected, yields.Select(y => $"{y.Item}:{y.Qty}"));
    }

    [Theory]
    [InlineData("", "Corroded Bronze Axe")]
    [InlineData("   ", "Corroded Bronze Axe")]
    [InlineData("0 Bronze Scraps", "Corroded Bronze Axe")]
    [InlineData("-2 Bronze Scraps", "Corroded Bronze Axe")]
    [InlineData("1.5 Bronze Scraps", "Corroded Bronze Axe")]
    [InlineData("Bronze Scraps x1.5", "Corroded Bronze Axe")]
    [InlineData("4x", "Corroded Bronze Axe")]
    [InlineData("4", "Corroded Bronze Axe")]
    [InlineData("1001 Bronze Scraps", "Corroded Bronze Axe")]
    [InlineData("2 Corroded Bronze Axe", "Corroded Bronze Axe")]
    [InlineData("1 a, 1 b, 1 c, 1 d, 1 e, 1 f, 1 g, 1 h, 1 i", "Target")]
    public void Invalid_scrap_yield_input_is_rejected_with_an_explanation(string input, string target)
    {
        Assert.False(ScrapItems.TryParseYields(input, target, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ------------------------------------------------------------------ 2. Disk persistence

    [Fact]
    public void Scrap_rules_are_saved_on_disk_reloaded_and_removed_case_insensitively()
    {
        using var s = new Scratch();
        var book = new ScrapItems(s.Path);
        int events = 0;
        book.Changed += () => events++;

        Assert.True(book.Set("  Corroded Bronze Plate Breastplate  ", [new ScrapYield("Bronze Scraps", 4)]));
        Assert.True(book.Set("Corroded Bronze Axe", [new ScrapYield("Bronze Scraps", 2), new ScrapYield("Rawhide Scraps", 2)]));
        Assert.True(System.IO.File.Exists(Path.Combine(s.Path, ScrapItems.FileName)));
        Assert.Equal(2, events);

        // Same yields in same order is a no-op
        Assert.True(book.Set("Corroded Bronze Plate Breastplate", [new ScrapYield("Bronze Scraps", 4)]));
        Assert.Equal(2, events);

        // Overwrite case-insensitively with updated yields
        Assert.True(book.Set("corroded bronze plate breastplate", [new ScrapYield("Bronze Scraps", 5)]));
        Assert.Equal(2, book.Count);
        Assert.Equal(3, events);

        var loaded = new ScrapItems(s.Path);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet("CORRODED BRONZE PLATE BREASTPLATE", out var rule));
        Assert.Equal([new ScrapYield("Bronze Scraps", 5)], rule.Yields);

        Assert.True(loaded.Remove("CORRODED BRONZE PLATE BREASTPLATE"));
        Assert.False(loaded.TryGet("Corroded Bronze Plate Breastplate", out _));

        // External write on disk is picked up by Reload()
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(s.Path, ScrapItems.FileName), DateTime.UtcNow.AddSeconds(5));
        Assert.True(book.Reload());
        Assert.Single(book.All);
        Assert.False(book.TryGet("Corroded Bronze Plate Breastplate", out _));
        Assert.False(book.Reload());

        Assert.Equal(1, book.ClearAll());
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Corrupt_scrap_file_and_self_reference_are_ignored_safely()
    {
        using var s = new Scratch();
        s.Write(ScrapItems.FileName, "{ not valid json");
        var book = new ScrapItems(s.Path);
        Assert.Equal(0, book.Count);
        Assert.False(book.Set("Bronze Scraps", [new ScrapYield("bronze scraps", 2)]));
        Assert.False(book.Set("\r\n\t ", [new ScrapYield("Cloth Scraps", 1)]));
    }

    // ------------------------------------------------------------------ 3. Wiki suggestions

    [Fact]
    public void Wiki_suggestions_match_breakdown_recipes_secondary_yields_and_disenchant_notes_but_reject_forward_crafts()
    {
        const string json = """
        {
          "tradeskills": [
            {
              "name": "Blacksmithing",
              "recipeGroups": [
                {
                  "name": "Salvage & Breakdown",
                  "recipes": [
                    {
                      "trivial": "10",
                      "result": "Bronze Scraps",
                      "result_qty": 4,
                      "ingredients": [
                        { "name": "Corroded Bronze Plate Breastplate", "qty": 1 },
                        { "name": "Hammer and Chisel", "qty": 1 }
                      ]
                    },
                    {
                      "trivial": "15",
                      "result": "Bronze Scraps",
                      "result_qty": 2,
                      "ingredients": [
                        { "name": "Corroded Bronze Axe", "qty": 1 },
                        { "name": "Rivet Cutters", "qty": 1 }
                      ],
                      "extra": {
                        "Secondary Yield": "Rawhide Scraps x2"
                      }
                    },
                    {
                      "trivial": "40",
                      "result": "Restored Bronze Breastplate",
                      "result_qty": 1,
                      "ingredients": [
                        { "name": "Corroded Bronze Plate Breastplate", "qty": 1 },
                        { "name": "Bronze Scraps", "qty": 2 },
                        { "name": "Smithing Hammer", "qty": 1 }
                      ]
                    }
                  ]
                },
                {
                  "name": "Weapons",
                  "recipes": [
                    {
                      "trivial": "50",
                      "result": "Bronze Longsword",
                      "result_qty": 1,
                      "ingredients": [
                        { "name": "Bronze Bar", "qty": 1 },
                        { "name": "Smithing Hammer", "qty": 1 }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "name": "Tailoring",
              "recipeGroups": [
                {
                  "name": "Salvage",
                  "recipes": [
                    {
                      "trivial": "5",
                      "result": "Cloth Scraps",
                      "result_qty": 2,
                      "ingredients": [
                        { "name": "Tattered Cloth Robe", "qty": 1 },
                        { "name": "Seam Ripper", "qty": 1 }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "name": "Smelting",
              "recipeGroups": [
                {
                  "name": "Recycling",
                  "recipes": [
                    {
                      "trivial": "5",
                      "result": "Bronze Bar",
                      "result_qty": 1,
                      "ingredients": [
                        { "name": "Bronze Scraps", "qty": 4 },
                        { "name": "Sturdy Ingot Mold", "qty": 1 }
                      ]
                    },
                    {
                      "trivial": "5",
                      "result": "Iron Scraps",
                      "result_qty": 4,
                      "ingredients": [
                        { "name": "Iron Bar", "qty": 1 },
                        { "name": "Smithing Hammer", "qty": 1 }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var wiki = RecipeBook.TryParse(json)!;

        var plate = ScrapSuggestions.FromRecipes(wiki, "Corroded Bronze Plate Breastplate");
        Assert.NotNull(plate);
        Assert.Equal([new ScrapYield("Bronze Scraps", 4)], plate.Yields);
        Assert.False(plate.RangeAdjusted);

        var axe = ScrapSuggestions.FromRecipes(wiki, "corroded bronze axe");
        Assert.NotNull(axe);
        Assert.Equal([new ScrapYield("Bronze Scraps", 2), new ScrapYield("Rawhide Scraps", 2)], axe.Yields);

        var robe = ScrapSuggestions.FromRecipes(wiki, "Tattered Cloth Robe");
        Assert.NotNull(robe);
        Assert.Equal([new ScrapYield("Cloth Scraps", 2)], robe.Yields);

        var bar = ScrapSuggestions.FromRecipes(wiki, "Iron Bar");
        Assert.NotNull(bar);
        Assert.Equal([new ScrapYield("Iron Scraps", 4)], bar.Yields);

        // Forward craft (Bronze Bar -> Bronze Longsword) and bulk input (4 Bronze Scraps -> Bronze Bar) must not match
        Assert.Null(ScrapSuggestions.FromRecipes(wiki, "Bronze Bar"));
        Assert.Null(ScrapSuggestions.FromRecipes(wiki, "Bronze Scraps"));

        // Fixture's "Powder" has "Disenchants Into": "Arcane Powder x4–x5"
        var powder = ScrapSuggestions.FromRecipes(RecipeFixture.Book, "Powder");
        Assert.NotNull(powder);
        Assert.True(powder.RangeAdjusted);
        Assert.Equal([new ScrapYield("Arcane Powder", 4)], powder.Yields);
    }

    // ------------------------------------------------------------------ 4. ItemValues scrap valuation

    [Fact]
    public void ItemValues_resolves_exact_estimated_unpriced_chained_and_cyclic_scrap_rules()
    {
        var rows = Parse(File(
            Sale(T, "Aldric", "Bronze Scraps", 1, "0 platinum 0 gold 0 silver 14 copper"),
            Sale(T, "Aldric", "Corroded Bronze Plate Breastplate", 1, "0 platinum 0 gold 0 silver 20 copper"),
            Sale(T, "Aldric", "Rough Iron Plate Helm", 1, "0 platinum 0 gold 0 silver 30 copper")));
        var pub = new PublicPrices([("Rawhide Scraps", new PublicPrice(0, 37))], generated: null); // Charged => 10cp
        var custom = new CustomPrices([
            ("Cloth Scraps", 12),
            ("Corroded Bronze Plate Breastplate", 999), // ignored when item is also in ScrapItems
        ]);
        var scrap = new ScrapItems([
            ("Corroded Bronze Plate Breastplate", [new ScrapYield("Bronze Scraps", 4)]),
            ("Corroded Bronze Axe", [new ScrapYield("Bronze Scraps", 2), new ScrapYield("Rawhide Scraps", 2)]),
            ("Tattered Cloth Robe", [new ScrapYield("Cloth Scraps", 3)]),
            ("Rough Iron Plate Greaves", [new ScrapYield("Unknown Scraps", 2), new ScrapYield("Bronze Scraps", 1)]),
            ("Salvage Crate", [new ScrapYield("Corroded Bronze Plate Breastplate", 2), new ScrapYield("Cloth Scraps", 1)]),
            ("Cycle A", [new ScrapYield("Cycle B", 1)]),
            ("Cycle B", [new ScrapYield("Cycle A", 1)]),
        ]);

        var values = ItemValues.FromLedger(rows, pub, custom, scrap);

        // Exact scrap from vendor sale of Bronze Scraps (14 * 4 = 56), winning over vendor sale (20) and custom (999)
        var bp = values.Of("Corroded Bronze Plate Breastplate");
        Assert.Equal(new ItemValue(56, ValueSource.Scrap), bp);
        Assert.True(bp.Known);
        Assert.True(bp.Scrap);
        Assert.False(bp.Estimated);
        Assert.Equal(new ItemValue(20, ValueSource.Sold), values.NaturalOf("Corroded Bronze Plate Breastplate"));

        // Estimated scrap when one material is Charged (~10cp): 2*14 + 2*10 = 48
        var axe = values.Of("Corroded Bronze Axe");
        Assert.Equal(new ItemValue(48, ValueSource.ScrapEstimated), axe);
        Assert.True(axe.Known);
        Assert.True(axe.Scrap);
        Assert.True(axe.Estimated);

        // Custom-priced material feeds scrap value dynamically
        Assert.Equal(new ItemValue(36, ValueSource.Scrap), values.Of("Tattered Cloth Robe"));

        // Unpriced material leaves scrapped item ScrapUnpriced
        var greaves = values.Of("Rough Iron Plate Greaves");
        Assert.Equal(new ItemValue(0, ValueSource.ScrapUnpriced), greaves);
        Assert.False(greaves.Known);
        Assert.True(greaves.Scrap);
        Assert.False(greaves.Estimated);

        // Chained scrap rule expands cleanly: 2*(4*14) + 1*12 = 124
        Assert.Equal(new ItemValue(124, ValueSource.Scrap), values.Of("Salvage Crate"));
        Assert.Equal([new ScrapYield("Bronze Scraps", 8), new ScrapYield("Cloth Scraps", 1)], values.ExpandScrap("Salvage Crate"));

        // Cycle protection stops safely as ScrapUnpriced
        Assert.Equal(new ItemValue(0, ValueSource.ScrapUnpriced), values.Of("Cycle A"));

        // Unscrapped sibling still gets family estimate from Rough Iron Plate Helm, unaffected by scrap rules
        Assert.Equal(new ItemValue(30, ValueSource.Estimated), values.Of("Rough Iron Plate Boots"));
    }

    // ------------------------------------------------------------------ 5. LootDay & LootSummary

    [Fact]
    public void LootDay_counts_member_and_ours_scrapped_and_formats_clipboard_scrap_yield_tables()
    {
        var values = new ItemValues(
            [("Bronze Scraps", 14), ("Rawhide Scraps", 8)],
            custom: new CustomPrices([("Quest Token", 0)]),
            scrap: new ScrapItems([
                ("Corroded Bronze Plate Breastplate", [new ScrapYield("Bronze Scraps", 4)]),
                ("Corroded Bronze Axe", [new ScrapYield("Bronze Scraps", 2), new ScrapYield("Rawhide Scraps", 2)]),
            ]));

        var events = new[]
        {
            new LootEvent(T, LootKind.Item, "Aldric", true, "a bandit", "Corroded Bronze Plate Breastplate", 2, 0, 0),
            new LootEvent(T.AddSeconds(1), LootKind.Item, "Brom", false, "a bandit", "Corroded Bronze Axe", 1, 0, 0),
            new LootEvent(T.AddSeconds(2), LootKind.Item, "Aldric", true, "a bandit", "Bronze Scraps", 3, 0, 0),
            new LootEvent(T.AddSeconds(3), LootKind.Item, "Brom", false, "a bandit", "Quest Token", 1, 0, 0),
        };

        var day = LootDay.Of(new DateOnly(2026, 10, 24), events, values);

        Assert.Equal(3, day.Scrapped);
        Assert.Equal(3, day.VendorPriced);
        Assert.Equal(1, day.Custom);
        Assert.Equal(0, day.FamilyEstimated);
        Assert.Equal(0, day.PlainUnpriced);
        Assert.Equal(2, day.Ours.Scrapped);
        // 2*56 + 1*44 + 3*14 + 0 = 198
        Assert.Equal(198, day.ItemValue);

        // Scrap yield: Bronze Scraps = 2*4 + 1*2 = 10 (140cp); Rawhide Scraps = 1*2 = 2 (16cp)
        Assert.Equal(2, day.ScrapMaterials.Count);
        Assert.Equal(("Bronze Scraps", 10, 140), (day.ScrapMaterials[0].Material, day.ScrapMaterials[0].Count, day.ScrapMaterials[0].Total));
        Assert.Equal(("Rawhide Scraps", 2, 16), (day.ScrapMaterials[1].Material, day.ScrapMaterials[1].Count, day.ScrapMaterials[1].Total));
        Assert.Equal(2, day.ScrapMaterials[0].From.Count);

        var aldric = day.Member("Aldric")!;
        Assert.Equal(2, aldric.Scrapped);
        Assert.Single(aldric.ScrapMaterials);
        Assert.Equal(("Bronze Scraps", 8), (aldric.ScrapMaterials[0].Material, aldric.ScrapMaterials[0].Count));

        var itemLine = LootSummary.ItemLine(day.ItemsWorth.First(i => i.Item == "Corroded Bronze Axe"), values);
        Assert.Contains("scrap → Bronze Scraps ×2, Rawhide Scraps ×2", itemLine);

        var itemsText = LootSummary.ItemsText(day.Day, day.ItemsWorth, "Whole day", values);
        Assert.Contains("SCRAP YIELD", itemsText);
        Assert.Contains("Bronze Scraps ×10", itemsText);

        var breakdown = LootSummary.Breakdown(day.Day, events, values: values);
        Assert.Contains("SCRAP YIELD", breakdown);
        Assert.Contains("Bronze Scraps", breakdown);
    }

    // ------------------------------------------------------------------ 6. LootStore persistence & mutual exclusion

    [Fact]
    public void LootStore_persists_syncs_and_keeps_scrap_and_custom_prices_mutually_exclusive()
    {
        using var s = new Scratch();
        var today = DateTime.Now.AddMinutes(-5);
        s.Write(Path.Combine("ledger", "srv", "Aldric", "Ledger", "Aldric_Character_x.json"), File(
            Item(today, "Aldric", "Aldric", "a bandit", "Corroded Bronze Axe"),
            Sale(today.AddSeconds(-10), "Aldric", "Bronze Scraps", 1, "0 platinum 0 gold 0 silver 15 copper")));

        var ledgerDir = Path.Combine(s.Path, "ledger");
        var stateDir = Path.Combine(s.Path, "state");

        var primary = new LootStore(stateDir, ledgerDir);
        primary.Select(primary.Sources.Single());
        Assert.True(primary.Refresh());

        var secondary = new LootStore(stateDir, ledgerDir, primary.CustomPrices, primary.ScrapItems);
        secondary.Select(secondary.Sources.Single());
        secondary.Refresh();

        // Set custom price first, then switch to scrap -> custom price is removed
        Assert.True(primary.SetCustomPrice("Corroded Bronze Axe", 500));
        Assert.Equal(ValueSource.Custom, primary.Values.Of("Corroded Bronze Axe").Source);

        Assert.True(primary.SetScrapItem("Corroded Bronze Axe", [new ScrapYield("Bronze Scraps", 2)]));
        Assert.False(primary.CustomPrices.TryGet("Corroded Bronze Axe", out _));
        Assert.Equal(new ItemValue(30, ValueSource.Scrap), primary.Values.Of("Corroded Bronze Axe"));

        Assert.True(secondary.SyncOverrides());
        Assert.Equal(new ItemValue(30, ValueSource.Scrap), secondary.Values.Of("Corroded Bronze Axe"));
        Assert.False(secondary.SyncOverrides());

        // Persisted across restart
        var restarted = new LootStore(stateDir, ledgerDir);
        restarted.Select(restarted.Sources.Single());
        restarted.Refresh();
        Assert.Equal(new ItemValue(30, ValueSource.Scrap), restarted.Values.Of("Corroded Bronze Axe"));

        // Switching back to custom price removes scrap rule
        Assert.True(primary.SetCustomPrice("Corroded Bronze Axe", 250));
        Assert.False(primary.ScrapItems.TryGet("Corroded Bronze Axe", out _));
        Assert.Equal(new ItemValue(250, ValueSource.Custom), primary.Values.Of("Corroded Bronze Axe"));

        Assert.True(primary.SetScrapItem("Corroded Bronze Axe", [new ScrapYield("Bronze Scraps", 2)]));
        Assert.True(primary.ClearScrapItem("Corroded Bronze Axe"));
        Assert.False(primary.Values.Of("Corroded Bronze Axe").Known);
    }

    // ------------------------------------------------------------------ 7. MarketMath.Yours scrap valuation

    [Fact]
    public void MarketMath_Yours_values_scrapped_items_using_material_market_prices()
    {
        var book = new MarketBook();
        book.Merge(MarketFeed.TryParse(MarketFixture.Json)!);
        var snapshot = MarketSnapshot.From(book, "Trem", DateTime.Parse(MarketFixture.GeneratedAt).ToUniversalTime());

        // MarketFixture has Copper Ore (stack 20, asks 25cp and 15cp -> median 20cp each) and Rawhide Backpack (asks 500cp and 600cp -> median 550cp)
        var scrap = new ScrapItems([
            ("Scrap Metal Breastplate", [new ScrapYield("Copper Ore", 5)]),
            ("Half Priced Blade", [new ScrapYield("Copper Ore", 2), new ScrapYield("Unknown Material", 1)]),
            ("Unpriced Robe", [new ScrapYield("Unknown Material", 3)]),
        ]);

        var looted = new[]
        {
            ("Rawhide Backpack", 1),
            ("Scrap Metal Breastplate", 2),
            ("Half Priced Blade", 1),
            ("Unpriced Robe", 2),
            ("Mystery Pebble", 1),
        };

        var summary = MarketMath.Yours(looted, snapshot, scrap);

        Assert.Equal(5, summary.DistinctCount);
        Assert.Equal(3, summary.Priced.Count);
        Assert.Equal(2, summary.Unpriced.Count);

        // Rawhide Backpack: 550; Scrap Metal Breastplate: 2 * (5 * 20) = 200; Half Priced Blade: 1 * (2 * 20) = 40
        Assert.Equal(550 + 200 + 40, summary.Total);

        var plate = summary.Priced.Single(p => p.Name == "Scrap Metal Breastplate");
        Assert.True(plate.Scrapped);
        Assert.False(plate.PartialScrap);
        Assert.Equal(100, plate.Each);
        Assert.Equal(200, plate.Worth);
        Assert.Equal("Copper Ore", plate.Item.Name);

        var blade = summary.Priced.Single(p => p.Name == "Half Priced Blade");
        Assert.True(blade.Scrapped);
        Assert.True(blade.PartialScrap);
        Assert.Equal(40, blade.Worth);

        var unpricedRobe = summary.Unpriced.Single(u => u.Name == "Unpriced Robe");
        Assert.True(unpricedRobe.Scrapped);
        Assert.Equal([new ScrapYield("Unknown Material", 3)], unpricedRobe.Yields);
    }
}
