using System.Globalization;
using System.Text.Json;

namespace MnMCompanion.Loot;

/// <summary>A player-assigned price for one of an item, in copper (0cp is valid and marks an item as worth nothing rather than unpriced).</summary>
public sealed record CustomPrice(string Item, long Copper);

/// <summary>
/// Prices set by hand for individual items, keyed without regard to case. They take precedence over every vendor price and
/// family estimate, never feed sibling family estimates, and stay on this PC in <see cref="FileName"/> (never shared).
/// Thread-safe; raises <see cref="Changed"/> when modified or reloaded from disk.
/// </summary>
public sealed class CustomPrices
{
    public const string FileName = "custom-prices.json";
    public const int MaxName = 120;
    public const int MaxItems = 5_000;
    public const long MaxCopper = 1_000_000_000_000L;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static CustomPrices Empty { get; } = new();

    private readonly string? _path;
    private readonly Dictionary<string, CustomPrice> _items;
    private readonly object _gate = new();
    private DateTime _loadedWrite;
    private long _version;

    private sealed record Row(string Item, long Copper);

    /// <summary>An in-memory custom price book (not persisted).</summary>
    public CustomPrices()
    {
        _items = new Dictionary<string, CustomPrice>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An in-memory custom price book seeded with initial prices (not persisted).</summary>
    public CustomPrices(IEnumerable<(string Name, long Each)> items) : this()
    {
        foreach (var (name, each) in items)
        {
            if (CleanName(name) is not { } clean || each is < 0 or > MaxCopper) continue;
            if (!_items.ContainsKey(clean) && _items.Count >= MaxItems) break;
            _items[clean] = new CustomPrice(clean, each);
        }
    }

    /// <summary>A custom price book kept in <see cref="FileName"/> inside <paramref name="stateRoot"/>.</summary>
    public CustomPrices(string stateRoot)
    {
        _path = Path.Combine(stateRoot, FileName);
        _items = Load(_path);
        _loadedWrite = Stamp();
    }

    /// <summary>Bumps whenever prices are added, changed, cleared, or reloaded from disk.</summary>
    public long Version => Volatile.Read(ref _version);

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Every custom price, sorted by item name.</summary>
    public IReadOnlyList<CustomPrice> All
    {
        get { lock (_gate) return _items.Values.OrderBy(p => p.Item, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    /// <summary>Raised after any price changes (via <see cref="Set"/>, <see cref="Remove"/>, <see cref="ClearAll"/> or <see cref="Reload"/>).</summary>
    public event Action? Changed;

    /// <summary>A clean item name (trimmed, control characters stripped, at most <see cref="MaxName"/> characters); null when empty.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxName) name = name[..MaxName].TrimEnd();
        return name.Length == 0 ? null : name;
    }

    public bool TryGet(string? item, out long copper)
    {
        if (CleanName(item) is not { } clean) { copper = 0; return false; }
        lock (_gate)
        {
            if (_items.TryGetValue(clean, out var found))
            {
                copper = found.Copper;
                return true;
            }
            copper = 0;
            return false;
        }
    }

    public long? Of(string? item) => TryGet(item, out var copper) ? copper : null;

    /// <summary>Sets the custom price for <paramref name="item"/> in copper (0cp allowed). Returns false if invalid or at capacity.</summary>
    public bool Set(string? item, long copper)
    {
        if (CleanName(item) is not { } clean || copper is < 0 or > MaxCopper) return false;
        Action? notify = null;
        lock (_gate)
        {
            if (!_items.ContainsKey(clean) && _items.Count >= MaxItems) return false;
            if (_items.TryGetValue(clean, out var had) && had.Copper == copper && had.Item == clean) return true;
            _items[clean] = new CustomPrice(clean, copper);
            Interlocked.Increment(ref _version);
            Save();
            notify = Changed;
        }
        notify?.Invoke();
        return true;
    }

    /// <summary>Clears any custom price for <paramref name="item"/> so it falls back to its natural value. True if one was removed.</summary>
    public bool Remove(string? item)
    {
        if (CleanName(item) is not { } clean) return false;
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

    /// <summary>Removes every custom price. Returns how many were cleared.</summary>
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

    /// <summary>Reads <see cref="FileName"/> again if another instance updated it on disk. True when prices changed.</summary>
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

    private static bool Same(Dictionary<string, CustomPrice> a, Dictionary<string, CustomPrice> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value == other);

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var rows = _items.Values.OrderBy(p => p.Item, StringComparer.OrdinalIgnoreCase).Select(p => new Row(p.Item, p.Copper)).ToList();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rows, Json));
            File.Move(temp, _path, overwrite: true);
            _loadedWrite = Stamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, CustomPrice> Load(string path)
    {
        var map = new Dictionary<string, CustomPrice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return map;
            if (JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path), Json) is not { } rows) return map;
            foreach (var r in rows)
            {
                if (r is null || CleanName(r.Item) is not { } clean || r.Copper is < 0 or > MaxCopper) continue;
                if (!map.ContainsKey(clean) && map.Count >= MaxItems) break;
                map[clean] = new CustomPrice(clean, r.Copper);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return map;
    }

    /// <summary>Formats copper for display in an edit box using standard pp/gp/sp/cp words.</summary>
    public static string FormatInput(long copper) => LootSummary.Coin(Math.Clamp(copper, 0, MaxCopper));

    /// <summary>
    /// Parses coin text entered by the player: bare copper (<c>0</c>, <c>45</c>, <c>1,500</c>), coin units (<c>3sp 41cp</c>,
    /// <c>2gp</c>, <c>1pp 25sp</c>, <c>0cp</c>, <c>5s</c>), or whole-copper decimals (<c>1.5gp</c> → 15,000cp).
    /// </summary>
    public static bool TryParseCoin(string? text, out long copper, out string? error)
    {
        copper = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Type a price, such as 25cp, 4sp 50cp, 2gp, or 0cp.";
            return false;
        }
        var raw = text.Trim();
        if (raw.StartsWith('-'))
        {
            error = "A price can't be negative (use 0cp for an item worth nothing).";
            return false;
        }
        if (raw[0] is '~' or '+')
        {
            error = "Type a plain coin amount, such as 1sp 50cp.";
            return false;
        }

        // Strip grouping commas only when nestled between digits ("1,500")
        var stripped = new System.Text.StringBuilder(raw.Length);
        for (int k = 0; k < raw.Length; k++)
        {
            if (raw[k] == ',' && k > 0 && k + 1 < raw.Length && char.IsAsciiDigit(raw[k - 1]) && char.IsAsciiDigit(raw[k + 1])) continue;
            stripped.Append(raw[k]);
        }
        var s = stripped.ToString();

        var tokens = new List<(string Number, string Unit)>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n) break;

            int numStart = i;
            bool seenDigit = false, seenDot = false;
            while (i < n)
            {
                if (char.IsAsciiDigit(s[i])) { seenDigit = true; i++; }
                else if (s[i] == '.' && !seenDot) { seenDot = true; i++; }
                else break;
            }
            if (!seenDigit || s[i - 1] == '.')
            {
                error = "Type a coin amount using pp, gp, sp or cp (for example 2gp 50sp).";
                return false;
            }
            var num = s[numStart..i];

            while (i < n && char.IsWhiteSpace(s[i])) i++;
            int unitStart = i;
            while (i < n && char.IsAsciiLetter(s[i])) i++;
            var unit = s[unitStart..i];
            tokens.Add((num, unit));
        }

        if (tokens.Count == 0)
        {
            error = "Type a price, such as 25cp, 4sp 50cp, 2gp, or 0cp.";
            return false;
        }

        if (tokens.Count == 1 && tokens[0].Unit.Length == 0)
        {
            if (tokens[0].Number.Contains('.'))
            {
                error = "Copper has no fractions: round to whole copper (1cp).";
                return false;
            }
            if (!long.TryParse(tokens[0].Number, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) || whole > MaxCopper)
            {
                error = "Price is too large (at most 1,000,000pp).";
                return false;
            }
            copper = whole;
            error = null;
            return true;
        }

        var seenUnits = new HashSet<int>();
        decimal total = 0m;
        foreach (var (num, unit) in tokens)
        {
            if (unit.Length == 0)
            {
                error = "Give the unit for every part, such as 5sp 20cp.";
                return false;
            }
            (int slot, decimal scale) = unit.ToLowerInvariant() switch
            {
                "pp" or "plat" or "platinum" or "p" => (0, 1_000_000m),
                "gp" or "gold" or "g" => (1, 10_000m),
                "sp" or "silver" or "s" => (2, 100m),
                "cp" or "copper" or "c" => (3, 1m),
                _ => (-1, 0m),
            };
            if (slot < 0)
            {
                error = "Unknown coin unit: use pp, gp, sp or cp (for example 2gp 50sp).";
                return false;
            }
            if (!seenUnits.Add(slot))
            {
                error = "Each coin unit (pp, gp, sp, cp) can appear only once.";
                return false;
            }
            if (!decimal.TryParse(num, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value > MaxCopper)
            {
                error = "Price is too large (at most 1,000,000pp).";
                return false;
            }
            var part = value * scale;
            if (part != decimal.Truncate(part))
            {
                error = "Copper has no fractions: round to whole copper (1cp).";
                return false;
            }
            total += part;
            if (total > MaxCopper)
            {
                error = "Price is too large (at most 1,000,000pp).";
                return false;
            }
        }

        copper = (long)total;
        error = null;
        return true;
    }
}
