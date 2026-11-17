using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MnMCompanion.Loot;

/// <summary>One crafting material and quantity yielded when scrapping a single unit of an item.</summary>
public sealed record ScrapYield(string Item, int Qty)
{
    /// <summary>"Bronze Scraps ×4" (scaled by <paramref name="multiplier"/>), or "Bronze Scraps" when the total is 1.</summary>
    public string Show(int multiplier = 1)
    {
        long total = (long)Qty * Math.Max(1, multiplier);
        return total > 1 ? $"{Item} ×{total:N0}" : Item;
    }
}

/// <summary>A player-configured rule saying what materials one of <see cref="Item"/> scraps into instead of being sold.</summary>
public sealed record ScrapRule(string Item, IReadOnlyList<ScrapYield> Yields);

/// <summary>
/// Per-item scrap rules kept locally on this PC in <see cref="FileName"/> (never shared). When an item has a scrap rule,
/// Companion values it by the sum of the materials it breaks down into instead of its vendor sale price.
/// Thread-safe; raises <see cref="Changed"/> when modified or reloaded from disk.
/// </summary>
public sealed class ScrapItems
{
    public const string FileName = "scrap-items.json";
    public const int MaxName = CustomPrices.MaxName;
    public const int MaxItems = CustomPrices.MaxItems;
    public const int MaxYieldsPerItem = 8;
    public const int MaxQty = 1_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Regex LeadingQty = new(@"^(\d+)\s*(?:[x×]\s*)?(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingQty = new(@"^(.+?)\s*(?:[x×]\s*(\d+)|\(\s*(\d+)\s*\))$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BareQty = new(@"^\d+\s*[x×]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingDecimal = new(@"^\d+\.\d+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingDecimal = new(@"^(.+?)\s*[x×]\s*\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingNegative = new(@"^(.+?)\s*[x×]\s*-\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ScrapItems Empty { get; } = new();

    private readonly string? _path;
    private readonly Dictionary<string, ScrapRule> _items;
    private readonly object _gate = new();
    private DateTime _loadedWrite;
    private long _version;

    private sealed record YieldRow(string Item, int Qty);
    private sealed record Row(string Item, List<YieldRow> Yields);

    /// <summary>An in-memory scrap book (not persisted).</summary>
    public ScrapItems()
    {
        _items = new Dictionary<string, ScrapRule>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An in-memory scrap book seeded with initial rules (not persisted).</summary>
    public ScrapItems(IEnumerable<(string Name, IReadOnlyList<ScrapYield> Yields)> items) : this()
    {
        foreach (var (name, yields) in items)
        {
            if (CustomPrices.CleanName(name) is not { } clean) continue;
            if (NormalizeYields(clean, yields) is not { } normalized) continue;
            if (!_items.ContainsKey(clean) && _items.Count >= MaxItems) break;
            _items[clean] = new ScrapRule(clean, normalized);
        }
    }

    /// <summary>A scrap book kept in <see cref="FileName"/> inside <paramref name="stateRoot"/>.</summary>
    public ScrapItems(string stateRoot)
    {
        _path = Path.Combine(stateRoot, FileName);
        _items = Load(_path);
        _loadedWrite = Stamp();
    }

    /// <summary>Bumps whenever scrap rules are added, changed, cleared, or reloaded from disk.</summary>
    public long Version => Volatile.Read(ref _version);

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Every scrap rule, sorted by item name.</summary>
    public IReadOnlyList<ScrapRule> All
    {
        get { lock (_gate) return _items.Values.OrderBy(r => r.Item, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    /// <summary>Raised after any scrap rule changes (via <see cref="Set"/>, <see cref="Remove"/>, <see cref="ClearAll"/> or <see cref="Reload"/>).</summary>
    public event Action? Changed;

    public bool TryGet(string? item, out ScrapRule rule)
    {
        if (CustomPrices.CleanName(item) is not { } clean)
        {
            rule = null!;
            return false;
        }
        lock (_gate)
        {
            if (_items.TryGetValue(clean, out var found))
            {
                rule = found;
                return true;
            }
            rule = null!;
            return false;
        }
    }

    public IReadOnlyList<ScrapYield>? Of(string? item) => TryGet(item, out var rule) ? rule.Yields : null;

    /// <summary>Sets the scrap yields for one of <paramref name="item"/>. Returns false if invalid, self-referential, or at capacity.</summary>
    public bool Set(string? item, IReadOnlyList<ScrapYield>? yields)
    {
        if (CustomPrices.CleanName(item) is not { } clean) return false;
        if (NormalizeYields(clean, yields) is not { } normalized) return false;
        Action? notify = null;
        lock (_gate)
        {
            if (!_items.ContainsKey(clean) && _items.Count >= MaxItems) return false;
            var next = new ScrapRule(clean, normalized);
            if (_items.TryGetValue(clean, out var had) && SameRule(had, next)) return true;
            _items[clean] = next;
            Interlocked.Increment(ref _version);
            Save();
            notify = Changed;
        }
        notify?.Invoke();
        return true;
    }

    /// <summary>Removes any scrap rule for <paramref name="item"/> so it returns to its vendor or custom price. True if one was removed.</summary>
    public bool Remove(string? item)
    {
        if (CustomPrices.CleanName(item) is not { } clean) return false;
        Action? notify = null;
        lock (_gate)
        {
            if (!_items.Remove(clean)) return false;
            Interlocked.Increment(ref _version);
            Save();
            notify = Changed;
        }
        notify?.Invoke();
        return true;
    }

    /// <summary>Removes every scrap rule. Returns how many were cleared.</summary>
    public int ClearAll()
    {
        int removed;
        Action? notify = null;
        lock (_gate)
        {
            if (_items.Count == 0) return 0;
            removed = _items.Count;
            _items.Clear();
            Interlocked.Increment(ref _version);
            Save();
            notify = Changed;
        }
        notify?.Invoke();
        return removed;
    }

    /// <summary>Reads <see cref="FileName"/> again if another instance updated it on disk. True when rules changed.</summary>
    public bool Reload()
    {
        if (_path is null) return false;
        Action? notify = null;
        lock (_gate)
        {
            var stamp = Stamp();
            if (stamp == _loadedWrite) return false;
            _loadedWrite = stamp;
            var now = Load(_path);
            if (Same(_items, now)) return false;
            _items.Clear();
            foreach (var kv in now) _items[kv.Key] = kv.Value;
            Interlocked.Increment(ref _version);
            notify = Changed;
        }
        notify?.Invoke();
        return true;
    }

    private DateTime Stamp() => _path is not null && File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

    private static bool SameRule(ScrapRule a, ScrapRule b) =>
        a.Item == b.Item && a.Yields.Count == b.Yields.Count && a.Yields.SequenceEqual(b.Yields);

    private static bool Same(Dictionary<string, ScrapRule> a, Dictionary<string, ScrapRule> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && SameRule(kv.Value, other));

    private static List<ScrapYield>? NormalizeYields(string targetClean, IReadOnlyList<ScrapYield>? yields)
    {
        if (yields is null || yields.Count == 0) return null;
        var merged = new List<ScrapYield>(yields.Count);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var y in yields)
        {
            if (y is null || CustomPrices.CleanName(y.Item) is not { } matClean) return null;
            if (y.Qty is < 1 or > MaxQty) return null;
            if (matClean.Equals(targetClean, StringComparison.OrdinalIgnoreCase)) return null;
            if (byName.TryGetValue(matClean, out var idx))
            {
                long sum = (long)merged[idx].Qty + y.Qty;
                if (sum > MaxQty) return null;
                merged[idx] = merged[idx] with { Qty = (int)sum };
            }
            else
            {
                if (merged.Count >= MaxYieldsPerItem) return null;
                byName[matClean] = merged.Count;
                merged.Add(new ScrapYield(matClean, y.Qty));
            }
        }
        return merged.Count == 0 ? null : merged;
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var rows = _items.Values
                .OrderBy(r => r.Item, StringComparer.OrdinalIgnoreCase)
                .Select(r => new Row(r.Item, r.Yields.Select(y => new YieldRow(y.Item, y.Qty)).ToList()))
                .ToList();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rows, Json));
            File.Move(temp, _path, overwrite: true);
            _loadedWrite = Stamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, ScrapRule> Load(string path)
    {
        var map = new Dictionary<string, ScrapRule>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return map;
            if (JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path), Json) is not { } rows) return map;
            foreach (var r in rows)
            {
                if (r is null || CustomPrices.CleanName(r.Item) is not { } clean || r.Yields is null) continue;
                var rawYields = r.Yields.Where(y => y is not null).Select(y => new ScrapYield(y.Item, y.Qty)).ToList();
                if (NormalizeYields(clean, rawYields) is not { } normalized) continue;
                if (!map.ContainsKey(clean) && map.Count >= MaxItems) break;
                map[clean] = new ScrapRule(clean, normalized);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return map;
    }

    /// <summary>Formats yields for editing in an input box: "4 Bronze Scraps, 2 Rawhide Scraps".</summary>
    public static string FormatInput(IReadOnlyList<ScrapYield> yields) =>
        string.Join(", ", yields.Select(y => $"{y.Qty} {y.Item}"));

    /// <summary>Formats yields for display: "Bronze Scraps ×4, Rawhide Scraps ×2".</summary>
    public static string DescribeYields(IReadOnlyList<ScrapYield> yields, int multiplier = 1) =>
        string.Join(", ", yields.Select(y => y.Show(multiplier)));

    /// <summary>
    /// Parses yield text entered by the player: <c>4 Bronze Scraps</c>, <c>2x Bronze Scraps, 2x Rawhide Scraps</c>,
    /// <c>Bronze Scraps ×4</c>, <c>Bronze Scraps (4)</c>, or <c>Cloth Scraps</c> (counts as 1).
    /// </summary>
    public static bool TryParseYields(string? text, string? targetItem, out IReadOnlyList<ScrapYield> yields, out string? error)
    {
        yields = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Type what one item scraps into, such as 4 Bronze Scraps.";
            return false;
        }

        var parts = text.Split([',', '+', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Type what one item scraps into, such as 4 Bronze Scraps.";
            return false;
        }

        var targetClean = CustomPrices.CleanName(targetItem);
        var merged = new List<ScrapYield>(parts.Length);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.StartsWith('-') || TrailingNegative.IsMatch(part))
            {
                error = "Quantities can't be negative.";
                return false;
            }
            if (LeadingDecimal.IsMatch(part) || TrailingDecimal.IsMatch(part))
            {
                error = "Quantities must be whole numbers (for example 4 Bronze Scraps).";
                return false;
            }
            if (BareQty.IsMatch(part))
            {
                error = "Include the material name after the quantity (for example 4 Bronze Scraps).";
                return false;
            }

            string rawName;
            int qty;
            if (LeadingQty.Match(part) is { Success: true } lead)
            {
                if (!int.TryParse(lead.Groups[1].Value, out qty))
                {
                    error = $"Quantity is too large (at most {MaxQty:N0}).";
                    return false;
                }
                rawName = lead.Groups[2].Value;
            }
            else if (TrailingQty.Match(part) is { Success: true } trail)
            {
                var num = trail.Groups[2].Success ? trail.Groups[2].Value : trail.Groups[3].Value;
                if (!int.TryParse(num, out qty))
                {
                    error = $"Quantity is too large (at most {MaxQty:N0}).";
                    return false;
                }
                rawName = trail.Groups[1].Value;
            }
            else
            {
                rawName = part;
                qty = 1;
            }

            if (qty < 1)
            {
                error = "Quantity must be at least 1.";
                return false;
            }
            if (qty > MaxQty)
            {
                error = $"Quantity is too large (at most {MaxQty:N0}).";
                return false;
            }

            var matClean = CustomPrices.CleanName(rawName);
            if (matClean is null || long.TryParse(matClean, out _))
            {
                error = "Type a material name (for example 4 Bronze Scraps).";
                return false;
            }
            if (targetClean is not null && matClean.Equals(targetClean, StringComparison.OrdinalIgnoreCase))
            {
                error = "An item can't scrap into itself.";
                return false;
            }

            if (byName.TryGetValue(matClean, out var idx))
            {
                long sum = (long)merged[idx].Qty + qty;
                if (sum > MaxQty)
                {
                    error = $"Total quantity for {merged[idx].Item} is too large (at most {MaxQty:N0}).";
                    return false;
                }
                merged[idx] = merged[idx] with { Qty = (int)sum };
            }
            else
            {
                if (merged.Count >= MaxYieldsPerItem)
                {
                    error = $"Can't list more than {MaxYieldsPerItem} scrap materials for one item.";
                    return false;
                }
                byName[matClean] = merged.Count;
                merged.Add(new ScrapYield(matClean, qty));
            }
        }

        yields = merged;
        error = null;
        return true;
    }
}
