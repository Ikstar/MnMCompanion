using MnMCompanion.Gear;
using MnMCompanion.Market;

namespace MnMCompanion.Tests;

public class GearTests
{
    private static readonly string[] VexkalItems =
    [
        "Nosebleed Stud",
        "Patched Rawhide Gorget",
        "Grandmaster's Dark Leather Hood",
        "Riverbend Scoundrel's Mask",
        "Patched Hide Tunic",
        "Tattered Rawhide Vest",
        "Rawhide Bracer",
        "Riverbend Ruffian's Belt",
        "Polished Iron Ring",
        "Knucklehide Chausses",
        "Ragged Leather Cloak",
        "Rawhide Shoulderpads",
        "Krindor's Wristguard",
        "Drakeling's Emberclaw Gauntlets",
        "Ashira Pack Ring",
        "Cinder Beetle Boots",
        "Cinder Beetle Mandible",
        "Drakeling Skewer",
        "Steadyhand",
        "Bronzehead Arrow",
    ];

    private sealed class FakeOcr(Func<GrayImage, string> read) : IGearOcr
    {
        public Task<string> ReadTextAsync(GrayImage image, CancellationToken cancel = default) =>
            Task.FromResult(read(image));
    }

    private static GearCatalog BuildVexkalCatalog()
    {
        var cards = new Dictionary<string, ItemCard>(StringComparer.OrdinalIgnoreCase)
        {
            ["nosebleed stud"] = new() { Name = "Nosebleed Stud", Slot = "Ear", Ac = 2, Stats = new Dictionary<string, double> { ["dex"] = 2 } },
            ["patched rawhide gorget"] = new() { Name = "Patched Rawhide Gorget", Slot = "Neck", Ac = 3 },
            ["grandmaster's dark leather hood"] = new() { Name = "Grandmaster's Dark Leather Hood", Slot = "Head", Ac = 8, Stats = new Dictionary<string, double> { ["agi"] = 4, ["dex"] = 3 } },
            ["riverbend scoundrel's mask"] = new() { Name = "Riverbend Scoundrel's Mask", Slot = "Face", Ac = 4 },
            ["patched hide tunic"] = new() { Name = "Patched Hide Tunic", Slot = "Chest", Ac = 11 },
            ["tattered rawhide vest"] = new() { Name = "Tattered Rawhide Vest", Slot = "Shirt", Ac = 3 },
            ["rawhide bracer"] = new() { Name = "Rawhide Bracer", Slot = "Wrist", Ac = 4 },
            ["riverbend ruffian's belt"] = new() { Name = "Riverbend Ruffian's Belt", Slot = "Waist", Ac = 4, Stats = new Dictionary<string, double> { ["str"] = 2 } },
            ["polished iron ring"] = new() { Name = "Polished Iron Ring", Slot = "Finger", Ac = 1 },
            ["knucklehide chausses"] = new() { Name = "Knucklehide Chausses", Slot = "Legs", Ac = 7 },
            ["ragged leather cloak"] = new() { Name = "Ragged Leather Cloak", Slot = "Back", Ac = 3 },
            ["rawhide shoulderpads"] = new() { Name = "Rawhide Shoulderpads", Slot = "Shoulders", Ac = 5 },
            ["krindor's wristguard"] = new() { Name = "Krindor's Wristguard", Slot = "Wrist", Ac = 5, Stats = new Dictionary<string, double> { ["dex"] = 3 } },
            ["drakeling's emberclaw gauntlets"] = new() { Name = "Drakeling's Emberclaw Gauntlets", Slot = "Hands", Ac = 6, Stats = new Dictionary<string, double> { ["str"] = 4 } },
            ["ashira pack ring"] = new() { Name = "Ashira Pack Ring", Slot = "Finger", Ac = 2 },
            ["cinder beetle boots"] = new() { Name = "Cinder Beetle Boots", Slot = "Feet", Ac = 6 },
            ["cinder beetle mandible"] = new() { Name = "Cinder Beetle Mandible", Slot = "Primary", Handed = "1H Piercing", Dmg = 7, Delay = 24 },
            ["drakeling skewer"] = new() { Name = "Drakeling Skewer", Slot = "Secondary", Handed = "1H Piercing", Dmg = 6, Delay = 22 },
            ["steadyhand"] = new() { Name = "Steadyhand", Slot = "Range", Dmg = 12, Delay = 37, Stats = new Dictionary<string, double> { ["str"] = 3, ["dex"] = 2 } },
            ["bronzehead arrow"] = new() { Name = "Bronzehead Arrow", Slot = "Ammo", Dmg = 3 },
        };

        var book = new MarketBook { Cards = cards };
        return GearCatalog.Build(book);
    }

    // ========================================================================= 1. Multi-resolution & crop detector tests

    [Theory]
    [InlineData(1280, 720, 120, 40, 26.0, true)]
    [InlineData(1920, 1080, 560, 210, 42.0, true)]
    [InlineData(2000, 1125, 245, 47, 44.0, true)]
    [InlineData(2560, 1440, 1850, 640, 56.0, true)]
    [InlineData(3840, 2160, 400, 100, 84.0, true)]
    [InlineData(312, 314, 2, 2, 44.0, false)]
    public void Detector_locates_paperdoll_and_extracts_banners_at_any_resolution_or_crop(
        int width, int height, int xLeft, int yTop, double pitch, bool includePersona)
    {
        var image = RenderSyntheticSheet(width, height, xLeft, yTop, pitch, includePersona, includeTextBanners: true, includeIcons: true);
        var sheet = CharacterSheetDetector.Detect(image);

        Assert.True(sheet.Found, $"Expected paperdoll detection at {width}x{height} P={pitch}");
        Assert.NotNull(sheet.Paperdoll);
        Assert.InRange(Math.Abs(sheet.Paperdoll!.XLeft - xLeft), 0, 2.5);
        Assert.InRange(Math.Abs(sheet.Paperdoll.YTop - yTop), 0, 2.5);
        Assert.InRange(Math.Abs(sheet.Paperdoll.Pitch - pitch), 0, 1.5);
        Assert.Equal(includePersona, sheet.Paperdoll.HasPersonaColumn);

        if (includePersona)
        {
            Assert.NotNull(sheet.Persona?.Identity);
            Assert.NotNull(sheet.Persona?.Vitals);
            Assert.NotNull(sheet.Persona?.StatsLeft);
            Assert.NotNull(sheet.Persona?.StatsRight);
            Assert.NotNull(sheet.Persona?.ResistsLeft);
            Assert.NotNull(sheet.Persona?.ResistsRight);
            Assert.NotNull(sheet.Persona?.Devotion);
        }
        else
        {
            Assert.Null(sheet.Persona);
        }

        // Ear2 is empty; all other 20 slots are occupied with dark banners + bright white text.
        Assert.False(sheet.SlotPatches.ContainsKey(GearSlot.Ear2), "Empty Ear2 slot should not produce an OCR banner patch");
        Assert.Equal(20, sheet.SlotPatches.Count);

        foreach (var (slot, patch) in sheet.SlotPatches)
        {
            // Normalized OCR patch width (~260 inner + 48 quiet border = ~308 px) regardless of source resolution.
            Assert.InRange(patch.Width, 240, 340);
            Assert.True(patch.Height > 52, $"Slot {slot} patch height too small: {patch.Height}");

            // Quiet border is pure white (255) and interior contains dark ink (< 60).
            Assert.Equal(255, patch.Pixels[0]);
            Assert.Equal(255, patch.Pixels[^1]);
            Assert.Contains(patch.Pixels, b => b < 60);
        }
    }

    [Fact]
    public void Detector_returns_not_found_for_non_inventory_images()
    {
        int w = 800, h = 600;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                rgb[i] = (byte)(30 + (x * 180) / w);
                rgb[i + 1] = (byte)(25 + (y * 140) / h);
                rgb[i + 2] = 40;
            }
        }

        var sheet = CharacterSheetDetector.Detect(new RgbImage(w, h, rgb));
        Assert.False(sheet.Found);
        Assert.Empty(sheet.SlotPatches);
    }

    [Fact]
    public async Task Scanner_detects_icon_only_paperdoll_and_advises_enabling_slot_names()
    {
        var image = RenderSyntheticSheet(1280, 720, 200, 80, pitch: 34.0, includePersona: true, includeTextBanners: false, includeIcons: true);
        var sheet = CharacterSheetDetector.Detect(image);

        Assert.True(sheet.Found);
        Assert.Empty(sheet.SlotPatches);
        Assert.True(sheet.NonEmptyVisualSlots >= 10);

        var scan = await GearScreenshotScanner.ScanAsync(image, new FakeOcr(_ => ""), BuildVexkalCatalog());
        Assert.True(scan.Found);
        Assert.False(scan.LabelsVisible);
        Assert.Contains("item names on slots", scan.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ========================================================================= 2. Line de-wrapping & fuzzy catalog matching

    [Theory]
    [InlineData("Nosebleed\nStud", GearSlot.Ear1, "Nosebleed Stud")]
    [InlineData("Patched\nRawhide\nGorget", GearSlot.Neck, "Patched Rawhide Gorget")]
    [InlineData("Grandmaste\nr's Dark\nLeather\nHood", GearSlot.Head, "Grandmaster's Dark Leather Hood")]
    [InlineData("Riverbend\nScoundrel's\nMask", GearSlot.Face, "Riverbend Scoundrel's Mask")]
    [InlineData("Patched\nHide Tunic", GearSlot.Chest, "Patched Hide Tunic")]
    [InlineData("Tattered\nRawhide\nVest", GearSlot.Shirt, "Tattered Rawhide Vest")]
    [InlineData("Rawhide\nBracer", GearSlot.Wrist1, "Rawhide Bracer")]
    [InlineData("Riverbend\nRuffian's\nBelt", GearSlot.Waist, "Riverbend Ruffian's Belt")]
    [InlineData("Polished\nIron Ring", GearSlot.Finger1, "Polished Iron Ring")]
    [InlineData("Knucklehide\nChausses", GearSlot.Legs, "Knucklehide Chausses")]
    [InlineData("Ragged\nLeather\nCloak", GearSlot.Back, "Ragged Leather Cloak")]
    [InlineData("Rawhide\nShoulderpa\nds", GearSlot.Shoulders, "Rawhide Shoulderpads")]
    [InlineData("Krindor's\nWristguard", GearSlot.Wrist2, "Krindor's Wristguard")]
    [InlineData("Drakeling's\nEmberclaw\nGauntlets", GearSlot.Hands, "Drakeling's Emberclaw Gauntlets")]
    [InlineData("Ashira Pack\nRing", GearSlot.Finger2, "Ashira Pack Ring")]
    [InlineData("Cinder\nBeetle\nBoots", GearSlot.Feet, "Cinder Beetle Boots")]
    [InlineData("Cinder\nBeetle\nMandible", GearSlot.Primary, "Cinder Beetle Mandible")]
    [InlineData("Drakeling\nSkewer", GearSlot.Secondary, "Drakeling Skewer")]
    [InlineData("Steadyhand", GearSlot.Range, "Steadyhand")]
    [InlineData("Bronzehead\nArrow", GearSlot.Ammo, "Bronzehead Arrow")]
    public void Matcher_resolves_all_20_vexkal_items_including_mid_word_wraps(string rawOcr, GearSlot slot, string expected)
    {
        var catalog = BuildVexkalCatalog();
        var match = GearItemMatcher.Match(slot, rawOcr, catalog);

        Assert.NotNull(match);
        Assert.Equal(expected, match!.Item);
        Assert.True(match.Confidence >= 0.95);
    }

    [Theory]
    [InlineData("Riverbend ScoundreI's Mask", GearSlot.Face, "Riverbend Scoundrel's Mask")]
    [InlineData("Krindor s Wristguard", GearSlot.Wrist2, "Krindor's Wristguard")]
    [InlineData("Cinder BeetIe MandibIe", GearSlot.Primary, "Cinder Beetle Mandible")]
    [InlineData("Grandmaste\nr's Dark\nLeather\nHood", GearSlot.Head, "Grandmaster's Dark Leather Hood")]
    public void Matcher_handles_ocr_character_confusions(string rawOcr, GearSlot slot, string expected)
    {
        var match = GearItemMatcher.Match(slot, rawOcr, BuildVexkalCatalog());
        Assert.NotNull(match);
        Assert.Equal(expected, match!.Item);
    }

    [Fact]
    public void PostProcessor_keeps_lowercase_prepositions_separated_while_joining_midword_wraps()
    {
        Assert.Equal("Cloak of the Night", GearTextPostProcessor.CleanAndJoin("Cloak\nof\nthe Night"));
        Assert.Equal("Grandmaster's Dark Leather Hood", GearTextPostProcessor.CleanAndJoin("Grandmaste\nr's Dark\nLeather\nHood"));
        Assert.Equal("Rawhide Shoulderpads", GearTextPostProcessor.CleanAndJoin("Rawhide\nShoulderpa\nds"));
        Assert.Equal("Warm Leather Cap", GearItemMatcher.Match(GearSlot.Head, "Warrn Leather Cap",
            new GearCatalog([new GearCatalogEntry("Warm Leather Cap", "Head", false)]))!.Item);
    }

    [Theory]
    [InlineData("Ear", GearSlot.Ear2)]
    [InlineData("Item", GearSlot.Finger1)]
    [InlineData("Range", GearSlot.Range)]
    [InlineData("  ", GearSlot.Primary)]
    public void Matcher_ignores_empty_slot_placeholder_labels(string rawOcr, GearSlot slot)
    {
        Assert.Null(GearItemMatcher.Match(slot, rawOcr, BuildVexkalCatalog()));
    }

    [Fact]
    public void Matcher_preserves_unknown_item_name_cleaned_when_absent_from_catalog()
    {
        var match = GearItemMatcher.Match(GearSlot.Chest, "Sunforged\nBreastpla\nte", GearCatalog.Empty);
        Assert.NotNull(match);
        Assert.Equal("Sunforged Breastplate", match!.Item);
        Assert.False(match.Manual);
        Assert.True(match.Confidence < 0.8);
    }

    [Fact]
    public void Matcher_prefers_slot_compatible_item_on_fuzzy_tie()
    {
        var catalog = new GearCatalog([
            new GearCatalogEntry("Emerald Band", "Finger", false),
            new GearCatalogEntry("Emerald Stud", "Ear", false),
        ]);
        var earMatch = GearItemMatcher.Match(GearSlot.Ear1, "Emera1d Slud", catalog);
        Assert.NotNull(earMatch);
        Assert.Equal("Emerald Stud", earMatch!.Item);
    }

    // ========================================================================= 3. Persona parser & GearStore persistence

    [Fact]
    public void PersonaParser_extracts_identity_vitals_stats_resists_and_devotion()
    {
        var p = PersonaParser.Parse(
            identityOcr: "VEXKAL\nLEVEL 20\nGOBLIN\nARCHER\nHARADREL",
            vitalsOcr: "HEALTH 555 / 555\nMANA 5 / 5\nENDURANCE 100 / 100\nARMOR CLASS 69",
            statsLeftOcr: "5TR 60\nSTA 30\nAGI 25\nDEX 38",
            statsRightOcr: "INT 37\nWIS 36\nCHA 33",
            resistsLeftOcr: "COLD -5\nCORR 0\nD1S 2\nELEC 0",
            resistsRightOcr: "FIRE 0\nHOLY 0\nMAG 5\nP0I 5",
            devotionOcr: "DEVOTION\nNOVICE");

        Assert.Equal("Vexkal", p.Name);
        Assert.Equal(20, p.Level);
        Assert.Equal("Goblin", p.Race);
        Assert.Equal("Archer", p.Class);
        Assert.Equal("Haradrel", p.Origin);
        Assert.Equal(new VitalStat(555, 555), p.Health);
        Assert.Equal(new VitalStat(5, 5), p.Mana);
        Assert.Equal(new VitalStat(100, 100), p.Endurance);
        Assert.Equal(69, p.ArmorClass);
        Assert.Equal(60, p.Stats["STR"]);
        Assert.Equal(38, p.Stats["DEX"]);
        Assert.Equal(37, p.Stats["INT"]);
        Assert.Equal(33, p.Stats["CHA"]);
        Assert.Equal(-5, p.Resistances["COLD"]);
        Assert.Equal(2, p.Resistances["DIS"]);
        Assert.Equal(5, p.Resistances["POI"]);
        Assert.Equal("Novice", p.Devotion);
    }

    [Fact]
    public void GearStore_persists_profile_tracks_diffs_and_keeps_persona_on_tight_crop()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mnmcompanion-geartest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GearStore(dir);
            var persona = new PersonaSnapshot { Name = "Vexkal", Level = 20, Race = "Goblin", Class = "Archer", ArmorClass = 69 };

            var firstSlots = new Dictionary<GearSlot, EquippedItem>
            {
                [GearSlot.Primary] = new(GearSlot.Primary, "Cinder Beetle Mandible"),
                [GearSlot.Range] = new(GearSlot.Range, "Steadyhand"),
            };
            var d1 = store.ApplyScan(firstSlots, persona, new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(2, d1.Equipped);
            Assert.Equal(2, d1.Added);
            Assert.Equal(0, d1.Changed);

            // Second scan: tight paperdoll-only crop (persona is null) updates weapon and adds Head while retaining earlier Persona.
            var secondSlots = new Dictionary<GearSlot, EquippedItem>
            {
                [GearSlot.Primary] = new(GearSlot.Primary, "Drakeling Skewer"),
                [GearSlot.Head] = new(GearSlot.Head, "Grandmaster's Dark Leather Hood"),
            };
            var d2 = store.ApplyScan(secondSlots, persona: null, new DateTime(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc));
            Assert.Equal(2, d2.Equipped);
            Assert.Equal(1, d2.Added);
            Assert.Equal(1, d2.Changed);
            Assert.Equal(1, d2.Cleared);

            // Manual edit & reload from disk
            Assert.True(store.SetSlot(GearSlot.Ear2, "Nosebleed Stud"));
            Assert.True(store.SetSlot(GearSlot.Head, null));

            var reloaded = new GearStore(dir);
            Assert.Equal("Vexkal", reloaded.Current.Persona?.Name);
            Assert.Equal(20, reloaded.Current.Persona?.Level);
            Assert.Equal("Drakeling Skewer", reloaded.Current.Get(GearSlot.Primary)?.Item);
            Assert.Equal("Nosebleed Stud", reloaded.Current.Get(GearSlot.Ear2)?.Item);
            Assert.True(reloaded.Current.Get(GearSlot.Ear2)?.Manual);
            Assert.Null(reloaded.Current.Get(GearSlot.Head));

            var text = reloaded.Current.ToClipboardText();
            Assert.Contains("Vexkal — Level 20 Goblin Archer", text);
            Assert.Contains("[Primary] Drakeling Skewer", text);
            Assert.Contains("[Ear 2] Nosebleed Stud", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Scanner_end_to_end_populates_slots_and_persona_from_synthetic_screenshot()
    {
        var image = RenderSyntheticSheet(1920, 1080, 420, 160, pitch: 42.0, includePersona: true, includeTextBanners: true, includeIcons: true);
        var sheet = CharacterSheetDetector.Detect(image);
        Assert.True(sheet.Found);

        // Map exact GrayImage instances returned for each patch back to expected OCR outputs.
        var patchMap = new Dictionary<GrayImage, string>(ReferenceEqualityComparer.Instance);
        var slotOcr = new Dictionary<GearSlot, string>
        {
            [GearSlot.Ear1] = "Nosebleed\nStud",
            [GearSlot.Neck] = "Patched\nRawhide\nGorget",
            [GearSlot.Head] = "Grandmaste\nr's Dark\nLeather\nHood",
            [GearSlot.Face] = "Riverbend\nScoundrel's\nMask",
            [GearSlot.Chest] = "Patched\nHide Tunic",
            [GearSlot.Shirt] = "Tattered\nRawhide\nVest",
            [GearSlot.Wrist1] = "Rawhide\nBracer",
            [GearSlot.Waist] = "Riverbend\nRuffian's\nBelt",
            [GearSlot.Finger1] = "Polished\nIron Ring",
            [GearSlot.Legs] = "Knucklehide\nChausses",
            [GearSlot.Back] = "Ragged\nLeather\nCloak",
            [GearSlot.Shoulders] = "Rawhide\nShoulderpa\nds",
            [GearSlot.Wrist2] = "Krindor's\nWristguard",
            [GearSlot.Hands] = "Drakeling's\nEmberclaw\nGauntlets",
            [GearSlot.Finger2] = "Ashira Pack\nRing",
            [GearSlot.Feet] = "Cinder\nBeetle\nBoots",
            [GearSlot.Primary] = "Cinder\nBeetle\nMandible",
            [GearSlot.Secondary] = "Drakeling\nSkewer",
            [GearSlot.Range] = "Steadyhand",
            [GearSlot.Ammo] = "Bronzehead\nArrow",
        };

        var fakeOcr = new FakeOcr(img =>
        {
            // Match by dimensions and pixel checksum against sheet patches so a fresh Detect() call inside ScanAsync hits cleanly.
            foreach (var (s, sp) in sheet.SlotPatches)
                if (SamePatch(sp, img)) return slotOcr[s];
            if (sheet.Persona?.Identity is { } id && SamePatch(id, img)) return "VEXKAL\nLEVEL 20\nGOBLIN\nARCHER\nHARADREL";
            if (sheet.Persona?.Vitals is { } vt && SamePatch(vt, img)) return "HEALTH 555 / 555\nMANA 5 / 5\nENDURANCE 100 / 100\nARMOR CLASS 69";
            if (sheet.Persona?.StatsLeft is { } sl && SamePatch(sl, img)) return "STR 60\nSTA 30\nAGI 25\nDEX 38";
            if (sheet.Persona?.StatsRight is { } sr && SamePatch(sr, img)) return "INT 37\nWIS 36\nCHA 33";
            if (sheet.Persona?.ResistsLeft is { } rl && SamePatch(rl, img)) return "COLD -5\nCORR 0\nDIS 2\nELEC 0";
            if (sheet.Persona?.ResistsRight is { } rr && SamePatch(rr, img)) return "FIRE 0\nHOLY 0\nMAG 5\nPOI 5";
            if (sheet.Persona?.Devotion is { } dv && SamePatch(dv, img)) return "NOVICE";
            return "";
        });

        var result = await GearScreenshotScanner.ScanAsync(image, fakeOcr, BuildVexkalCatalog());
        Assert.True(result.Found);
        Assert.True(result.LabelsVisible);
        Assert.Equal(20, result.Slots.Count);
        Assert.Equal("Grandmaster's Dark Leather Hood", result.Slots[GearSlot.Head].Item);
        Assert.Equal("Rawhide Shoulderpads", result.Slots[GearSlot.Shoulders].Item);
        Assert.Equal("Steadyhand", result.Slots[GearSlot.Range].Item);
        Assert.False(result.Slots.ContainsKey(GearSlot.Ear2));
        Assert.Equal("Vexkal", result.Persona?.Name);
        Assert.Equal(69, result.Persona?.ArmorClass);
        Assert.Equal(-5, result.Persona?.Resistances["COLD"]);
    }

    // ========================================================================= Synthetic Inventory/Persona renderer

    private static bool SamePatch(GrayImage a, GrayImage b) =>
        a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels);

    private static RgbImage RenderSyntheticSheet(
        int width,
        int height,
        int xLeft,
        int yTop,
        double pitch,
        bool includePersona,
        bool includeTextBanners,
        bool includeIcons)
    {
        var rgb = new byte[width * height * 3];

        // Dim outdoor background gradient
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                rgb[i] = (byte)(18 + (x % 37) / 4);
                rgb[i + 1] = (byte)(15 + (y % 29) / 4);
                rgb[i + 2] = 14;
            }
        }

        void FillRect(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            x0 = Math.Clamp(x0, 0, width);
            x1 = Math.Clamp(x1, 0, width);
            y0 = Math.Clamp(y0, 0, height);
            y1 = Math.Clamp(y1, 0, height);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * width + x) * 3;
                    rgb[i] = r;
                    rgb[i + 1] = g;
                    rgb[i + 2] = b;
                }
            }
        }

        // Paperdoll frame backdrop
        FillRect(
            (int)Math.Round(xLeft - 0.12 * pitch),
            (int)Math.Round(yTop - 0.25 * pitch),
            (int)Math.Round(xLeft + 7.15 * pitch),
            (int)Math.Round(yTop + 7.15 * pitch),
            85, 86, 89);

        // Dark arched character portrait in center
        FillRect(
            (int)Math.Round(xLeft + 1.4 * pitch),
            (int)Math.Round(yTop + 1.2 * pitch),
            (int)Math.Round(xLeft + 5.6 * pitch),
            (int)Math.Round(yTop + 5.6 * pitch),
            38, 42, 40);

        if (includePersona)
        {
            FillRect(
                (int)Math.Round(xLeft - 3.35 * pitch),
                (int)Math.Round(yTop - 0.25 * pitch),
                (int)Math.Round(xLeft - 0.80 * pitch),
                (int)Math.Round(yTop + 10.95 * pitch),
                74, 75, 78);

            // Representative bright text lines inside Persona blocks
            for (int row = 0; row < 5; row++)
            {
                int ty = (int)Math.Round(yTop + (0.30 + row * 0.28) * pitch);
                FillRect((int)Math.Round(xLeft - 3.05 * pitch), ty, (int)Math.Round(xLeft - 1.9 * pitch), ty + Math.Max(2, (int)Math.Round(0.11 * pitch)), 210, 210, 210);
            }
            for (int row = 0; row < 4; row++)
            {
                int ty = (int)Math.Round(yTop + (5.75 + row * 0.28) * pitch);
                FillRect((int)Math.Round(xLeft - 3.05 * pitch), ty, (int)Math.Round(xLeft - 1.05 * pitch), ty + Math.Max(2, (int)Math.Round(0.11 * pitch)), 215, 215, 215);
            }
            for (int row = 0; row < 4; row++)
            {
                int ty = (int)Math.Round(yTop + (7.50 + row * 0.24) * pitch);
                FillRect((int)Math.Round(xLeft - 3.00 * pitch), ty, (int)Math.Round(xLeft - 2.25 * pitch), ty + Math.Max(2, (int)Math.Round(0.10 * pitch)), 110, 210, 110);
                if (row < 3)
                    FillRect((int)Math.Round(xLeft - 1.85 * pitch), ty, (int)Math.Round(xLeft - 1.05 * pitch), ty + Math.Max(2, (int)Math.Round(0.10 * pitch)), 110, 210, 110);
            }
        }

        int sw = (int)Math.Round(0.94 * pitch);
        int sh = (int)Math.Round(0.94 * pitch);
        int border = Math.Max(1, (int)Math.Round(0.03 * pitch));

        foreach (var spec in GearSlots.All)
        {
            int sx = (int)Math.Round(xLeft + spec.RelX * pitch);
            int sy = (int)Math.Round(yTop + spec.RelY * pitch);

            // Outer bevel border
            FillRect(sx, sy, sx + sw, sy + sh, 108, 110, 113);
            // Stone interior
            FillRect(sx + border, sy + border, sx + sw - border, sy + sh - border, 48, 50, 53);

            bool emptySlot = spec.Slot == GearSlot.Ear2;
            if (emptySlot)
            {
                // Faint grey watermark ("Ear") with no dark plate
                int wy = sy + (int)Math.Round(0.45 * sh);
                FillRect(sx + (int)Math.Round(0.32 * sw), wy, sx + (int)Math.Round(0.68 * sw), wy + Math.Max(2, (int)Math.Round(0.10 * sh)), 94, 96, 98);
                continue;
            }

            if (includeIcons)
            {
                // Item icon highlight in upper half
                FillRect(sx + (int)Math.Round(0.28 * sw), sy + (int)Math.Round(0.16 * sh), sx + (int)Math.Round(0.72 * sw), sy + (int)Math.Round(0.52 * sh), 152, 130, 86);
            }

            if (includeTextBanners)
            {
                // Dark translucent banner across bottom ~45% of slot interior + 2 crisp white text lines (including edge-touching words)
                int banTop = sy + (int)Math.Round(0.52 * sh);
                int banBot = sy + sh - border;
                FillRect(sx + border, banTop, sx + sw - border, banBot, 16, 17, 19);

                int lineH = Math.Max(2, (int)Math.Round(0.11 * sh));
                int line1Y = banTop + Math.Max(1, (int)Math.Round(0.06 * sh));
                int line2Y = banTop + Math.Max(line1Y - banTop + lineH + 1, (int)Math.Round(0.24 * sh));

                // Full-width top text line (like "Knucklehide" / "Grandmaste") and centered second line
                int strokeW = Math.Max(1, (int)Math.Round(0.045 * sw));
                int stepX = Math.Max(2, strokeW * 2);
                for (int tx = sx + border; tx <= sx + sw - border - strokeW; tx += stepX)
                    FillRect(tx, line1Y, tx + strokeW, line1Y + lineH, 215, 215, 215);
                int spanW = Math.Max(12, sw - 2 * border - 2);
                int cellW = Math.Max(2, spanW / 6);
                int baseX = sx + border + 1;
                FillRect(baseX, line2Y, baseX + Math.Max(1, cellW / 2), line2Y + lineH, 210, 210, 210);
                int slotBits = (int)spec.Slot + 1;
                for (int bit = 0; bit < 5; bit++)
                {
                    if (((slotBits >> bit) & 1) != 0)
                    {
                        int cx = baseX + (bit + 1) * cellW;
                        FillRect(cx, line2Y, Math.Min(sx + sw - border, cx + Math.Max(1, cellW - 1)), line2Y + lineH, 210, 210, 210);
                    }
                }
            }
        }

        return new RgbImage(width, height, rgb);
    }
}
