using System.Text;
using System.Text.RegularExpressions;
using MnMCompanion.Loot;
using MnMCompanion.Market;
using MnMCompanion.Recipes;

namespace MnMCompanion.Gear;

/// <summary>Platform OCR adapter over preprocessed grayscale text patches.</summary>
public interface IGearOcr
{
    Task<string> ReadTextAsync(GrayImage image, CancellationToken cancel = default);
}

/// <summary>An item known to the local catalog (wiki recipes, market cards, custom/scrap prices, or looted items).</summary>
public sealed record GearCatalogEntry(string Name, string? Slot, bool Owned);

/// <summary>
/// In-memory catalog of known items used to clean up OCR misreads and power click-to-edit suggestions.
/// Built solely from already-cached local data (zero network or database calls).
/// </summary>
public sealed class GearCatalog
{
    public static GearCatalog Empty { get; } = new([]);

    private readonly Dictionary<string, GearCatalogEntry> _byExact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GearCatalogEntry>> _byAlnum = new(StringComparer.Ordinal);
    private readonly List<(GearCatalogEntry Entry, string Alnum, string RnNorm)> _entries = [];

    public GearCatalog(IEnumerable<GearCatalogEntry> entries)
    {
        foreach (var raw in entries)
        {
            if (CustomPrices.CleanName(raw.Name) is not { } clean) continue;
            if (_byExact.TryGetValue(clean, out var existing))
            {
                var merged = new GearCatalogEntry(
                    existing.Name,
                    existing.Slot ?? raw.Slot,
                    existing.Owned || raw.Owned);
                _byExact[clean] = merged;
            }
            else
            {
                _byExact[clean] = raw with { Name = clean };
            }
        }

        foreach (var entry in _byExact.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var alnum = GearTextPostProcessor.StripToAlnum(entry.Name);
            if (alnum.Length == 0) continue;
            var rnNorm = alnum.Replace("rn", "m", StringComparison.Ordinal);
            _entries.Add((entry, alnum, rnNorm));
            if (!_byAlnum.TryGetValue(alnum, out var bucket))
                _byAlnum[alnum] = bucket = [];
            bucket.Add(entry);
        }
    }

    public int Count => _entries.Count;

    public bool TryGetExact(string name, out GearCatalogEntry? entry) => _byExact.TryGetValue(name, out entry);

    public IReadOnlyList<GearCatalogEntry> All => _entries.Select(x => x.Entry).ToList();

    internal IReadOnlyList<(GearCatalogEntry Entry, string Alnum, string RnNorm)> Indexed => _entries;

    internal bool TryGetByAlnum(string alnum, out IReadOnlyList<GearCatalogEntry> entries)
    {
        if (_byAlnum.TryGetValue(alnum, out var list))
        {
            entries = list;
            return true;
        }
        entries = [];
        return false;
    }

    /// <summary>Builds a catalog from whatever local data is currently loaded.</summary>
    public static GearCatalog Build(
        MarketBook? market = null,
        RecipeBook? recipes = null,
        CustomPrices? custom = null,
        ScrapItems? scrap = null,
        PublicPrices? publicPrices = null,
        IEnumerable<string>? ownedItems = null)
    {
        var list = new List<GearCatalogEntry>();

        if (market is not null)
        {
            foreach (var card in market.Cards.Values)
                list.Add(new GearCatalogEntry(card.Name, card.Slot, Owned: false));
            foreach (var row in market.Rows.Values)
                list.Add(new GearCatalogEntry(row.Item, market.Card(row.Item)?.Slot, Owned: false));
            foreach (var hist in market.History)
                list.Add(new GearCatalogEntry(hist.Item, market.Card(hist.Item)?.Slot, Owned: false));
        }

        if (recipes is not null)
        {
            foreach (var r in recipes.Recipes)
            {
                var slot = r.Notes.FirstOrDefault(n => n.Label.Equals("Slot", StringComparison.OrdinalIgnoreCase))?.Text;
                list.Add(new GearCatalogEntry(r.Result, slot, Owned: false));
                foreach (var ing in r.Ingredients)
                {
                    list.Add(new GearCatalogEntry(ing.Name, null, Owned: false));
                    if (!string.IsNullOrWhiteSpace(ing.Or))
                        list.Add(new GearCatalogEntry(ing.Or, null, Owned: false));
                }
            }
        }

        if (custom is not null)
            foreach (var p in custom.All)
                list.Add(new GearCatalogEntry(p.Item, null, Owned: true));

        if (scrap is not null)
            foreach (var r in scrap.All)
                list.Add(new GearCatalogEntry(r.Item, null, Owned: true));

        if (publicPrices is not null)
            foreach (var kv in publicPrices.Items)
                list.Add(new GearCatalogEntry(kv.Key, null, Owned: false));

        if (ownedItems is not null)
            foreach (var item in ownedItems)
                list.Add(new GearCatalogEntry(item, null, Owned: true));

        return new GearCatalog(list);
    }

    /// <summary>Returns ranked suggestions for manual slot editing, prioritizing slot-compatible items and word matches.</summary>
    public IReadOnlyList<GearCatalogEntry> Suggest(GearSlot slot, string? query, int limit = 10)
    {
        var words = NameSearch.Words(query);
        return _entries
            .Select(x => x.Entry)
            .Where(e => words.Length == 0 || NameSearch.Matches(e.Name, query))
            .OrderByDescending(e => GearItemMatcher.IsSlotCompatible(e.Slot, slot))
            .ThenByDescending(e => e.Owned)
            .ThenByDescending(e => words.Length > 0 && e.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}

/// <summary>Cleans OCR output and rejoins mid-word wrapped slot labels without corrupting word boundaries.</summary>
public static class GearTextPostProcessor
{
    private static readonly Regex EdgeJunk = new(@"^[\|\[\]\{\}~_•·`'"".,:;\\/\-]+|[\|\[\]\{\}~_•·`""\\]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StandaloneLowerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "the", "and", "in", "to", "a", "an", "on", "for", "with", "from", "at", "by", "or",
    };

    public static string NormalizeQuotes(string text) =>
        text.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"');

    public static IReadOnlyList<string> CleanLines(string? rawOcr)
    {
        if (string.IsNullOrWhiteSpace(rawOcr)) return [];
        var normalized = NormalizeQuotes(rawOcr);
        var lines = new List<string>();
        foreach (var raw in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = EdgeJunk.Replace(Spaces.Replace(raw.Trim(), " "), "").Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length == 1 && !char.IsLetterOrDigit(trimmed[0])) continue;
            lines.Add(trimmed);
        }
        return lines;
    }

    /// <summary>
    /// Rejoins wrapped lines inside narrow slots. For example:
    /// <c>["Grandmaste", "r's Dark", "Leather", "Hood"]</c> -> <c>"Grandmaster's Dark Leather Hood"</c>,
    /// while <c>["Ring", "of", "the Wise"]</c> -> <c>"Ring of the Wise"</c>.
    /// </summary>
    public static string JoinWrappedLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "";
        var sb = new StringBuilder(lines[0]);
        for (int i = 1; i < lines.Count; i++)
        {
            var prev = sb.ToString();
            var next = lines[i];
            if (ShouldJoinWithoutSpace(prev, next))
            {
                if (prev.EndsWith('-')) sb.Length--;
                sb.Append(next);
            }
            else
            {
                sb.Append(' ').Append(next);
            }
        }
        return Spaces.Replace(sb.ToString(), " ").Trim();
    }

    public static string CleanAndJoin(string? rawOcr) => JoinWrappedLines(CleanLines(rawOcr));

    public static string StripToAlnum(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static bool ShouldJoinWithoutSpace(string prev, string next)
    {
        if (prev.Length == 0 || next.Length == 0) return false;
        char last = prev[^1];
        bool prevCanWrap = (last is >= 'a' and <= 'z') || last == '-';
        if (!prevCanWrap) return false;

        char first = next[0];
        if (first is not (>= 'a' and <= 'z')) return false;

        int space = next.IndexOf(' ');
        string firstToken = space > 0 ? next[..space] : next;
        string bareToken = firstToken.TrimEnd('\'', ',', '.', ':', ';', '!', '?');
        return !StandaloneLowerWords.Contains(bareToken);
    }
}

/// <summary>Matches cleaned/de-wrapped OCR strings against the item catalog with OCR-confusion weighting and slot hints.</summary>
public static class GearItemMatcher
{
    private const double MinAcceptedAdjustedScore = 0.78;
    private const double MinAcceptedRawSimilarity = 0.74;

    private static readonly Regex PossessiveSpace = new(@"\b([A-Za-z]{3,})\s+s\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static EquippedItem? Match(GearSlot slot, string? rawOcr, GearCatalog catalog)
    {
        var lines = GearTextPostProcessor.CleanLines(rawOcr);
        if (lines.Count == 0) return null;

        var joined = GearTextPostProcessor.JoinWrappedLines(lines);
        if (joined.Length == 0 || GearSlots.IsPlaceholder(joined)) return null;

        if (catalog.TryGetExact(joined, out var exact) && exact is not null)
            return new EquippedItem(slot, exact.Name, rawOcr, Confidence: 1.0, Manual: false);

        var alnum = GearTextPostProcessor.StripToAlnum(joined);
        if (alnum.Length == 0) return null;

        if (catalog.TryGetByAlnum(alnum, out var bucket) && bucket.Count > 0)
        {
            var picked = PickBestBucketMatch(bucket, slot);
            return new EquippedItem(slot, picked.Name, rawOcr, Confidence: 0.99, Manual: false);
        }

        var queryRn = alnum.Replace("rn", "m", StringComparison.Ordinal);
        GearCatalogEntry? bestEntry = null;
        double bestAdjusted = 0.0;
        double bestRawSim = 0.0;

        foreach (var (entry, candAlnum, candRn) in catalog.Indexed)
        {
            int maxLen = Math.Max(alnum.Length, candAlnum.Length);
            if (maxLen == 0) continue;
            if (Math.Abs(alnum.Length - candAlnum.Length) > Math.Max(2, (int)Math.Ceiling(maxLen * 0.30))) continue;

            double dist = WeightedDamerauDistance(alnum, candAlnum);
            if (queryRn != alnum || candRn != candAlnum)
            {
                double rnDist = WeightedDamerauDistance(queryRn, candRn) + 0.15;
                if (rnDist < dist) dist = rnDist;
            }

            double rawSim = 1.0 - dist / maxLen;
            if (rawSim < MinAcceptedRawSimilarity) continue;

            double adjusted = rawSim;
            if (IsSlotCompatible(entry.Slot, slot)) adjusted += 0.04;
            if (entry.Owned) adjusted += 0.015;

            if (adjusted > bestAdjusted)
            {
                bestAdjusted = adjusted;
                bestRawSim = rawSim;
                bestEntry = entry;
            }
        }

        if (bestEntry is not null && bestAdjusted >= MinAcceptedAdjustedScore)
            return new EquippedItem(slot, bestEntry.Name, rawOcr, Confidence: Math.Round(Math.Clamp(bestRawSim, 0.0, 0.98), 2), Manual: false);

        // Fallback when item is not in the local catalog: preserve the cleaned, de-wrapped OCR string so it remains visible and editable.
        var fallback = PossessiveSpace.Replace(joined, "$1's");
        if (GearSlots.IsPlaceholder(fallback)) return null;
        return new EquippedItem(slot, fallback, rawOcr, Confidence: 0.65, Manual: false);
    }

    public static bool IsSlotCompatible(string? itemSlotText, GearSlot target)
    {
        if (string.IsNullOrWhiteSpace(itemSlotText)) return false;
        var tokens = itemSlotText.ToUpperInvariant().Split([' ', ',', '/', '|'], StringSplitOptions.RemoveEmptyEntries);
        var spec = GearSlots.Spec(target);
        foreach (var token in tokens)
            if (spec.WikiTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static GearCatalogEntry PickBestBucketMatch(IReadOnlyList<GearCatalogEntry> bucket, GearSlot slot) =>
        bucket
            .OrderByDescending(e => IsSlotCompatible(e.Slot, slot))
            .ThenByDescending(e => e.Owned)
            .First();

    private static double WeightedDamerauDistance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var dp = new double[n + 1, m + 1];
        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                double subCost = SubstitutionCost(a[i - 1], b[j - 1]);
                double best = Math.Min(
                    dp[i - 1, j] + 1.0,
                    Math.Min(dp[i, j - 1] + 1.0, dp[i - 1, j - 1] + subCost));

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    best = Math.Min(best, dp[i - 2, j - 2] + 0.65);

                dp[i, j] = best;
            }
        }
        return dp[n, m];
    }

    private static double SubstitutionCost(char a, char b)
    {
        if (a == b) return 0.0;
        if (a > b) (a, b) = (b, a);
        return (a, b) switch
        {
            ('1', 'i') or ('1', 'l') or ('i', 'l') => 0.35,
            ('0', 'o') or ('5', 's') or ('8', 'b') or ('9', 'g') => 0.35,
            ('c', 'e') or ('n', 'r') or ('u', 'v') => 0.35,
            _ => 1.0,
        };
    }
}

/// <summary>Parses OCR text from the split Persona column patches into a structured <see cref="PersonaSnapshot"/>.</summary>
public static class PersonaParser
{
    private static readonly Regex LevelRegex = new(@"(?:LEVEL|LVL|L[E3]V[E3]L)\s*[:.]?\s*(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SlashVital = new(@"(\d{1,6})\s*/\s*(\d{1,6})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SignedInt = new(@"(-?\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StandaloneDigits = new(@"^\d{1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PersonaSnapshot Parse(
        string? identityOcr,
        string? vitalsOcr,
        string? statsLeftOcr,
        string? statsRightOcr,
        string? resistsLeftOcr,
        string? resistsRightOcr,
        string? devotionOcr)
    {
        var (name, level, race, cls, origin) = ParseIdentity(identityOcr);
        var (health, mana, endurance, ac) = ParseVitals(vitalsOcr);

        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ParsePairs(statsLeftOcr, ["STR", "STA", "AGI", "DEX"], NormalizeStatKey, stats);
        ParsePairs(statsRightOcr, ["INT", "WIS", "CHA"], NormalizeStatKey, stats);

        var resists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ParsePairs(resistsLeftOcr, ["COLD", "CORR", "DIS", "ELEC"], NormalizeResistKey, resists);
        ParsePairs(resistsRightOcr, ["FIRE", "HOLY", "MAG", "POI"], NormalizeResistKey, resists);

        var devotion = ParseDevotion(devotionOcr);

        return new PersonaSnapshot
        {
            Name = name,
            Level = level,
            Race = race,
            Class = cls,
            Origin = origin,
            Health = health,
            Mana = mana,
            Endurance = endurance,
            ArmorClass = ac,
            Stats = stats,
            Resistances = resists,
            Devotion = devotion,
        };
    }

    private static (string? Name, int? Level, string? Race, string? Class, string? Origin) ParseIdentity(string? ocr)
    {
        var lines = GearTextPostProcessor.CleanLines(ocr)
            .Where(l => !l.Equals("PERSONA", StringComparison.OrdinalIgnoreCase) && !l.Equals("EXPERIENCE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lines.Count == 0) return (null, null, null, null, null);

        string? name = null;
        int? level = null;
        int levelIdx = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var m = LevelRegex.Match(lines[i]);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int lv) && lv is >= 1 and <= 200)
            {
                level = lv;
                levelIdx = i;
                break;
            }
            if (i == 1 && StandaloneDigits.IsMatch(lines[i]) && int.TryParse(lines[i], out int lv2) && lv2 is >= 1 and <= 200)
            {
                level = lv2;
                levelIdx = i;
                break;
            }
        }

        if (levelIdx > 0)
            name = FormatTitleWords(lines[0]);
        else if (levelIdx == -1 && lines[0].Length > 1 && lines[0].Any(char.IsLetter))
            name = FormatTitleWords(lines[0]);

        var after = new List<string>();
        int startAfter = levelIdx >= 0 ? levelIdx + 1 : 1;
        for (int i = startAfter; i < lines.Count; i++)
        {
            var cleaned = FormatTitleWords(lines[i]);
            if (cleaned is { Length: > 0 }) after.Add(cleaned);
        }

        return (
            name,
            level,
            after.Count > 0 ? after[0] : null,
            after.Count > 1 ? after[1] : null,
            after.Count > 2 ? after[2] : null);
    }

    private static (VitalStat? Health, VitalStat? Mana, VitalStat? Endurance, int? Ac) ParseVitals(string? ocr)
    {
        VitalStat? health = null, mana = null, endurance = null;
        int? ac = null;
        var orderedVitals = new List<VitalStat>();
        int? lastStandalone = null;

        foreach (var line in GearTextPostProcessor.CleanLines(ocr))
        {
            var upper = line.ToUpperInvariant();
            var slash = SlashVital.Match(line);
            if (slash.Success &&
                int.TryParse(slash.Groups[1].Value, out int cur) &&
                int.TryParse(slash.Groups[2].Value, out int max))
            {
                var stat = new VitalStat(cur, max);
                orderedVitals.Add(stat);
                if (upper.Contains("HEALTH") || upper.Contains("HP")) health ??= stat;
                else if (upper.Contains("MANA") || upper.Contains("MP")) mana ??= stat;
                else if (upper.Contains("ENDUR")) endurance ??= stat;
                continue;
            }

            if ((upper.Contains("ARMOR") || upper.Contains("CLASS") || upper.StartsWith("AC")) &&
                SignedInt.Match(line) is { Success: true } acm &&
                int.TryParse(acm.Groups[1].Value, out int parsedAc))
            {
                ac = parsedAc;
            }
            else if (SignedInt.Match(line) is { Success: true } fallbackNum &&
                     int.TryParse(fallbackNum.Groups[1].Value, out int num))
            {
                lastStandalone = num;
            }
        }

        if (health is null && orderedVitals.Count >= 1) health = orderedVitals[0];
        if (mana is null && orderedVitals.Count >= 2) mana = orderedVitals[1];
        if (endurance is null && orderedVitals.Count >= 3) endurance = orderedVitals[2];
        ac ??= lastStandalone;

        return (health, mana, endurance, ac);
    }

    private static void ParsePairs(
        string? ocr,
        IReadOnlyList<string> expectedKeys,
        Func<string, string?> normalizeKey,
        Dictionary<string, int> target)
    {
        var lines = GearTextPostProcessor.CleanLines(ocr);
        var orderedNums = new List<int>();
        foreach (var line in lines)
        {
            var matches = SignedInt.Matches(line);
            if (matches.Count == 0) continue;
            var numMatch = matches[^1];
            if (!int.TryParse(numMatch.Groups[1].Value, out int val)) continue;
            orderedNums.Add(val);

            var keyPart = line[..numMatch.Index].Trim();
            if (normalizeKey(keyPart) is { } key)
                target[key] = val;
        }

        // Positional fallback if OCR slightly mangled labels but read all lines cleanly in order.
        if (orderedNums.Count == expectedKeys.Count)
        {
            for (int i = 0; i < expectedKeys.Count; i++)
                target.TryAdd(expectedKeys[i], orderedNums[i]);
        }
    }

    private static string? ParseDevotion(string? ocr)
    {
        foreach (var line in GearTextPostProcessor.CleanLines(ocr))
        {
            if (line.Equals("DEVOTION", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Any(char.IsLetter)) return FormatTitleWords(line);
        }
        return null;
    }

    private static string? NormalizeStatKey(string token)
    {
        var k = Regex.Replace(token.ToUpperInvariant(), @"[^A-Z0-9]", "");
        return k switch
        {
            "STR" or "5TR" or "S7R" => "STR",
            "STA" or "5TA" or "S7A" => "STA",
            "AGI" or "AG1" or "4GI" => "AGI",
            "DEX" or "D3X" => "DEX",
            "INT" or "1NT" or "IN7" => "INT",
            "WIS" or "W1S" or "WI5" => "WIS",
            "CHA" or "CH4" => "CHA",
            _ => null,
        };
    }

    private static string? NormalizeResistKey(string token)
    {
        var k = Regex.Replace(token.ToUpperInvariant(), @"[^A-Z0-9]", "");
        return k switch
        {
            "COLD" or "C0LD" => "COLD",
            "CORR" or "C0RR" => "CORR",
            "DIS" or "D1S" or "DI5" => "DIS",
            "ELEC" or "EL3C" or "3LEC" => "ELEC",
            "FIRE" or "F1RE" or "FIR3" => "FIRE",
            "HOLY" or "H0LY" => "HOLY",
            "MAG" or "M4G" => "MAG",
            "POI" or "P0I" or "P01" or "PO1" => "POI",
            _ => null,
        };
    }

    private static string? FormatTitleWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 1 && (w.All(c => !char.IsLetter(c) || char.IsUpper(c)) || w.All(c => !char.IsLetter(c) || char.IsLower(c))))
                words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }
}

/// <summary>Full outcome of scanning one character/inventory screenshot.</summary>
public sealed record GearScanResult(
    bool Found,
    bool LabelsVisible,
    string Message,
    IReadOnlyDictionary<GearSlot, EquippedItem> Slots,
    PersonaSnapshot? Persona,
    DetectedPaperdoll? Paperdoll);

/// <summary>Coordinates paperdoll detection, OCR across slot and Persona patches, and catalog matching.</summary>
public static class GearScreenshotScanner
{
    public static async Task<GearScanResult> ScanAsync(
        RgbImage image,
        IGearOcr ocr,
        GearCatalog catalog,
        CancellationToken cancel = default)
    {
        var sheet = CharacterSheetDetector.Detect(image);
        if (!sheet.Found || sheet.Paperdoll is null)
        {
            return new GearScanResult(
                Found: false,
                LabelsVisible: false,
                Message: "Couldn't find the character inventory paperdoll in that picture. Make sure the Inventory tab with your equipped slots is visible.",
                Slots: new Dictionary<GearSlot, EquippedItem>(),
                Persona: null,
                Paperdoll: null);
        }

        if (sheet.SlotPatches.Count == 0 && sheet.NonEmptyVisualSlots >= 3)
        {
            return new GearScanResult(
                Found: true,
                LabelsVisible: false,
                Message: "Found the inventory paperdoll, but slot item names weren't visible. Enable item names on slots in the game's settings, or click any slot to set it by hand.",
                Slots: new Dictionary<GearSlot, EquippedItem>(),
                Persona: null,
                Paperdoll: sheet.Paperdoll);
        }

        var slots = new Dictionary<GearSlot, EquippedItem>();
        foreach (var (slot, patch) in sheet.SlotPatches)
        {
            cancel.ThrowIfCancellationRequested();
            var raw = await ocr.ReadTextAsync(patch, cancel).ConfigureAwait(false);
            if (GearItemMatcher.Match(slot, raw, catalog) is { } matched)
                slots[slot] = matched;
        }

        PersonaSnapshot? persona = null;
        if (sheet.Persona is { } p)
        {
            async Task<string?> ReadOpt(GrayImage? img) =>
                img is null ? null : await ocr.ReadTextAsync(img, cancel).ConfigureAwait(false);

            var idText = await ReadOpt(p.Identity).ConfigureAwait(false);
            var vitalsText = await ReadOpt(p.Vitals).ConfigureAwait(false);
            var stL = await ReadOpt(p.StatsLeft).ConfigureAwait(false);
            var stR = await ReadOpt(p.StatsRight).ConfigureAwait(false);
            var rsL = await ReadOpt(p.ResistsLeft).ConfigureAwait(false);
            var rsR = await ReadOpt(p.ResistsRight).ConfigureAwait(false);
            var dev = await ReadOpt(p.Devotion).ConfigureAwait(false);

            var parsed = PersonaParser.Parse(idText, vitalsText, stL, stR, rsL, rsR, dev);
            if (parsed.HasAny) persona = parsed;
        }

        return new GearScanResult(
            Found: true,
            LabelsVisible: true,
            Message: $"Read {slots.Count}/{GearSlots.All.Count} equipped slots.",
            Slots: slots,
            Persona: persona,
            Paperdoll: sheet.Paperdoll);
    }
}
