using System.Text.RegularExpressions;
using MnMCompanion.Recipes;

namespace MnMCompanion.Loot;

/// <summary>A scrap yield inferred from the locally cached community wiki recipes.</summary>
public sealed record ScrapSuggestion(IReadOnlyList<ScrapYield> Yields, string Source, bool RangeAdjusted = false);

/// <summary>
/// Reads scrap, salvage, and disenchant yields out of an already-loaded <see cref="RecipeBook"/> (never hits the network).
/// Players can accept the suggestion with one click or edit it before saving.
/// </summary>
public static class ScrapSuggestions
{
    private static readonly string[] ToolWords =
    [
        "Hammer and Chisel", "Rivet Cutters", "Seam Ripper", "Disenchanting Cube",
        "Pliers", "Hammer", "Chisel", "Cutters", "Ripper", "Scissors", "Knife", "Kit", "Mold", "Cube",
    ];

    private static readonly string[] NonToolWords =
    [
        "Scraps", "Bar", "Ore", "Ingot", "Thread", "Padding", "Lining",
    ];

    private static readonly string[] BreakdownWords =
    [
        "Salvag", "Breakdown", "Break Down", "Deconstruct", "Disenchant", "Scrap", "Recycle", "Unwind", "Unravel",
    ];

    private static readonly string[] ScrapOutputWords =
    [
        "Scraps", "Powder", "Dust", "Essence", "Mote", "Shards", "Chips", "Fibers", "Strips",
    ];

    private static readonly string[] ExtraNoteWords =
    [
        "Yield", "Secondary", "Also", "Extra", "Return", "Scrap",
    ];

    private static readonly string[] BreakdownNoteLabels =
    [
        "Disenchant", "Scrap", "Salvag", "Break", "Deconstruct",
    ];

    private static readonly Regex RangeAfterMultiplier = new(@"([x×]\s*)(\d+)\s*[-–—]\s*(?:[x×]\s*)?(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RangeBeforeName = new(@"\b(\d+)\s*[-–—]\s*(\d+)(\s*[x×]?(?=\s+\S))", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RangeInParens = new(@"\(\s*(\d+)\s*[-–—]\s*(\d+)\s*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ScrapSuggestion? FromRecipes(RecipeBook? book, string? itemName)
    {
        if (book is null || CustomPrices.CleanName(itemName) is not { } clean) return null;

        // Pass 1: Salvage / breakdown recipes where the item is broken down using tools
        foreach (var r in book.Recipes)
        {
            if (r.Result.Equals(clean, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsBreakdownContext(r.Skill, r.Group) && !IsScrapOutputName(r.Result)) continue;

            var target = r.Ingredients.FirstOrDefault(i =>
                i.Qty == 1 &&
                (i.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Or, clean, StringComparison.OrdinalIgnoreCase)));
            if (target is null) continue;

            bool onlyToolsLeft = r.Ingredients.All(i => ReferenceEquals(i, target) || IsSalvageTool(i.Name));
            if (!onlyToolsLeft) continue;

            var combined = new List<ScrapYield>();
            bool rangeAdjusted = false;

            if (r.ResultQty == 1 && (r.Result.Contains(',') || r.Result.Contains('+') || r.Result.Contains(';')))
            {
                if (!TryParseWikiYieldText(r.Result, clean, out var parsedResult, out var resRange)) continue;
                combined.AddRange(parsedResult);
                rangeAdjusted |= resRange;
            }
            else if (CustomPrices.CleanName(r.Result) is { } resClean && !resClean.Equals(clean, StringComparison.OrdinalIgnoreCase))
            {
                combined.Add(new ScrapYield(resClean, Math.Clamp(r.ResultQty, 1, ScrapItems.MaxQty)));
            }

            foreach (var note in r.Notes)
            {
                if (!ContainsAny(note.Label, ExtraNoteWords)) continue;
                if (TryParseWikiYieldText(note.Text, clean, out var extra, out var extraRange))
                {
                    combined.AddRange(extra);
                    rangeAdjusted |= extraRange;
                }
            }

            var merged = Merge(clean, combined);
            if (merged.Count > 0)
            {
                var source = r.Group.Length > 0 ? $"{r.Skill} · {r.Group}" : r.Skill;
                return new ScrapSuggestion(merged, source, rangeAdjusted);
            }
        }

        // Pass 2: Recipe notes (e.g. "Disenchants Into", "Scraps Into", "Salvages Into")
        foreach (var r in book.Recipes)
        {
            bool matchesResult = r.Result.Equals(clean, StringComparison.OrdinalIgnoreCase);
            bool matchesSingleInput = false;
            if (!matchesResult)
            {
                var nonTools = r.Ingredients.Where(i => !IsSalvageTool(i.Name)).ToList();
                matchesSingleInput = nonTools.Count == 1 && nonTools[0].Qty == 1 &&
                    (nonTools[0].Name.Equals(clean, StringComparison.OrdinalIgnoreCase) || string.Equals(nonTools[0].Or, clean, StringComparison.OrdinalIgnoreCase));
            }
            if (!matchesResult && !matchesSingleInput) continue;

            foreach (var note in r.Notes)
            {
                if (!ContainsAny(note.Label, BreakdownNoteLabels)) continue;
                if (TryParseWikiYieldText(note.Text, clean, out var noteYields, out bool rangeAdjusted))
                    return new ScrapSuggestion(noteYields, $"{r.Skill} · {note.Label}", rangeAdjusted);
            }
        }

        return null;
    }

    public static bool TryParseWikiYieldText(string? text, string? targetItem, out IReadOnlyList<ScrapYield> yields, out bool rangeAdjusted)
    {
        yields = [];
        rangeAdjusted = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool adjusted = false;
        string normalized = RangeAfterMultiplier.Replace(text.Trim(), m =>
        {
            if (int.TryParse(m.Groups[2].Value, out int lo) && int.TryParse(m.Groups[3].Value, out int hi) && lo != hi)
                adjusted = true;
            return m.Groups[1].Value + m.Groups[2].Value;
        });
        normalized = RangeBeforeName.Replace(normalized, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out int lo) && int.TryParse(m.Groups[2].Value, out int hi) && lo != hi)
                adjusted = true;
            return m.Groups[1].Value + m.Groups[3].Value;
        });
        normalized = RangeInParens.Replace(normalized, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out int lo) && int.TryParse(m.Groups[2].Value, out int hi) && lo != hi)
                adjusted = true;
            return $"({m.Groups[1].Value})";
        });

        rangeAdjusted = adjusted;
        return ScrapItems.TryParseYields(normalized, targetItem, out yields, out _);
    }

    private static bool IsSalvageTool(string name) =>
        !ContainsAny(name, NonToolWords) && ContainsAny(name, ToolWords);

    private static bool IsBreakdownContext(string skill, string group) =>
        ContainsAny(skill, BreakdownWords) || ContainsAny(group, BreakdownWords);

    private static bool IsScrapOutputName(string result) =>
        ContainsAny(result, ScrapOutputWords);

    private static bool ContainsAny(string text, string[] terms)
    {
        foreach (var term in terms)
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static List<ScrapYield> Merge(string targetClean, IEnumerable<ScrapYield> raw)
    {
        var list = new List<ScrapYield>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var y in raw)
        {
            if (CustomPrices.CleanName(y.Item) is not { } matClean) continue;
            if (matClean.Equals(targetClean, StringComparison.OrdinalIgnoreCase)) continue;
            int qty = Math.Clamp(y.Qty, 1, ScrapItems.MaxQty);
            if (byName.TryGetValue(matClean, out var idx))
            {
                list[idx] = list[idx] with { Qty = (int)Math.Min((long)list[idx].Qty + qty, ScrapItems.MaxQty) };
            }
            else if (list.Count < ScrapItems.MaxYieldsPerItem)
            {
                byName[matClean] = list.Count;
                list.Add(new ScrapYield(matClean, qty));
            }
        }
        return list;
    }
}
