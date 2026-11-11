namespace MnMCompanion;

/// <summary>One tab's saved window state: whether it is out in its own window, and how that window looks.</summary>
public sealed record TabSlot(string Name, bool Open, int Scale, bool OnTop, bool Compact);

/// <summary>Tabs as they are used: only known pages, in their proper spelling, with at least one page left in the main window.</summary>
public sealed record NormalizedTabs(string ActiveDockedPage, IReadOnlyDictionary<string, TabSlot> Tabs);

/// <summary>
/// The rules for taking tabs out of the main window into their own windows: the order of the pages, which pages have a compact view,
/// and that the main window always keeps at least one page.
/// </summary>
public static class TabDockPolicy
{
    /// <summary>Every page, in the order the main window shows them.</summary>
    public static readonly IReadOnlyList<string> PageNames = ["Map", "Loot", "Vendor", "Trades", "Items", "Harvest", "Recipes", "Market"];

    /// <summary>The pages that have a compact view.</summary>
    public static readonly IReadOnlyList<string> CompactPages = ["Loot", "Vendor", "Items", "Harvest"];

    /// <summary>Whether <paramref name="name"/> is a page (in any letter case); <paramref name="canonical"/> is its proper spelling, or empty.</summary>
    public static bool IsKnownPage(string? name, out string canonical)
    {
        if (name is not null)
        {
            foreach (var page in PageNames)
            {
                if (string.Equals(page, name, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = page;
                    return true;
                }
            }
        }
        canonical = "";
        return false;
    }

    /// <summary>Whether the page has a compact view.</summary>
    public static bool SupportsCompact(string? pageName) =>
        pageName is not null && CompactPages.Any(p => string.Equals(p, pageName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a page may be taken out: not when it is the last one left in the main window.</summary>
    public static bool CanDetach(int dockedCount) => dockedCount > 1;

    /// <summary>The pages still in the main window, in page order. A page with nothing saved for it is in the main window.</summary>
    public static IReadOnlyList<string> DockedPages(IReadOnlyDictionary<string, TabSlot> tabs)
    {
        var docked = new List<string>(PageNames.Count);
        foreach (var p in PageNames)
            if (!TryFind(tabs, p, out var s) || !s.Open) docked.Add(p);
        return docked;
    }

    /// <summary>
    /// The page the main window shows after <paramref name="detachingPage"/> is taken out: the one after it, or the one before it when it was last.
    /// Never fails: an unknown or lone page gives the first other page, or Loot.
    /// </summary>
    public static string NextDockedPage(IReadOnlyList<string> dockedBeforeDetach, string detachingPage)
    {
        var idx = -1;
        for (var i = 0; i < dockedBeforeDetach.Count; i++)
        {
            if (string.Equals(dockedBeforeDetach[i], detachingPage, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        if (idx >= 0 && idx + 1 < dockedBeforeDetach.Count) return dockedBeforeDetach[idx + 1];
        if (idx > 0) return dockedBeforeDetach[idx - 1];
        foreach (var page in dockedBeforeDetach)
            if (!string.Equals(page, detachingPage, StringComparison.OrdinalIgnoreCase)) return page;
        return "Loot";
    }

    /// <summary>
    /// Saved tabs made usable: unknown pages dropped, names spelled properly (an open entry wins over a closed one spelled differently),
    /// scales snapped to an offered one, compact only where a page has it, and if every page was out, <paramref name="miniPage"/> (or Loot) comes back in.
    /// </summary>
    public static NormalizedTabs Normalize(string? miniPage, IReadOnlyDictionary<string, TabSlot>? raw)
    {
        var tabs = new Dictionary<string, TabSlot>(StringComparer.OrdinalIgnoreCase);
        if (raw is not null)
        {
            foreach (var (key, slot) in raw.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (slot is null || !IsKnownPage(key, out var canonical)) continue;
                var normalized = slot with
                {
                    Name = canonical,
                    Scale = UiScale.Nearest(slot.Scale),
                    Compact = SupportsCompact(canonical) && slot.Compact,
                };
                if (!tabs.TryGetValue(canonical, out var kept) || (!kept.Open && normalized.Open))
                    tabs[canonical] = normalized;
            }
        }

        var preferred = IsKnownPage(miniPage, out var p) ? p : "Loot";
        if (PageNames.All(page => tabs.TryGetValue(page, out var s) && s.Open))
            tabs[preferred] = tabs[preferred] with { Open = false };

        var docked = DockedPages(tabs);
        var active = docked.Contains(preferred) ? preferred : docked[0];
        return new NormalizedTabs(active, tabs);
    }

    /// <summary>
    /// A page's slot, found by its proper spelling or, failing that, in any letter case
    /// (an open entry wins over a closed one, as in <see cref="Normalize"/>).
    /// </summary>
    private static bool TryFind(IReadOnlyDictionary<string, TabSlot> tabs, string page, out TabSlot slot)
    {
        if (tabs.TryGetValue(page, out var exact) && exact is not null)
        {
            slot = exact;
            return true;
        }
        TabSlot? found = null;
        foreach (var (key, value) in tabs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (value is null || !string.Equals(key, page, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is null || (!found.Open && value.Open)) found = value;
        }
        slot = found!;
        return found is not null;
    }
}
