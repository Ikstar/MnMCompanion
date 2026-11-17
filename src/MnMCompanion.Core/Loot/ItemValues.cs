using MnMCompanion.Ledger;

namespace MnMCompanion.Loot;

/// <summary>Where an item's value came from.</summary>
public enum ValueSource
{
    /// <summary>No price known and no sibling to guess from.</summary>
    None,
    /// <summary>What a vendor actually paid for it, from the ledger's sales: the median per item.</summary>
    Sold,
    /// <summary>Never sold here, but the public price list has what a vendor paid someone for it.</summary>
    Public,
    /// <summary>
    /// Nobody was seen paying for it, but an ordinary vendor sells it, and vendors pay about a quarter of what they charge
    /// (<see cref="ItemValues.ChargePerPay"/>): the public list's lowest charge divided by that.
    /// </summary>
    Charged,
    /// <summary>Never sold: the median of its family (same first two or three words) that were.</summary>
    Estimated,
    /// <summary>A price the player set by hand for this item.</summary>
    Custom,
    /// <summary>Set to scrap instead of sell: valued from the exact prices of the materials it scraps into.</summary>
    Scrap,
    /// <summary>Set to scrap instead of sell: valued from the materials it scraps into, at least one of which is estimated.</summary>
    ScrapEstimated,
    /// <summary>Set to scrap instead of sell, but at least one material it scraps into has no known price yet.</summary>
    ScrapUnpriced,
}

/// <summary>What one of an item is worth to a vendor (or by custom price or scrap yield), in copper, and how that is known.</summary>
public readonly record struct ItemValue(long Each, ValueSource Source)
{
    public bool Known => Source is not (ValueSource.None or ValueSource.ScrapUnpriced);

    /// <summary>Worked out, not seen (from a vendor's stock price, the item's family, or estimated scrap materials): shown with "~".</summary>
    public bool Estimated => Source is ValueSource.Estimated or ValueSource.Charged or ValueSource.ScrapEstimated;

    /// <summary>Set by hand by the player via <see cref="CustomPrices"/>.</summary>
    public bool Custom => Source == ValueSource.Custom;

    /// <summary>Set to scrap instead of sell via <see cref="ScrapItems"/>.</summary>
    public bool Scrap => Source is ValueSource.Scrap or ValueSource.ScrapEstimated or ValueSource.ScrapUnpriced;
}

/// <summary>
/// What items are worth to a vendor, worked out from the ledger alone: each <c>act_24</c> row is an item sold (<c>d04</c>, the
/// count in <c>d01</c>, the coin for the whole stack as words in <c>d03</c>). The game's items carry no value of their own
/// and the price is per vendor, so an item nobody sold has no price of its own, only an estimate from its family: gear of
/// one material and weight sells for one price, so an unsold item takes the median of those that share its first two or three
/// words. Anything else is <see cref="ValueSource.None"/> and counted as unpriced, never guessed.
/// <para>
/// With a <see cref="PublicPrices"/> list (downloaded from the public data set) it knows more, best first: this PC's own sales,
/// what vendors paid for the item according to the list (<see cref="ValueSource.Public"/>), the list's vendor stock price
/// divided by <see cref="ChargePerPay"/> (<see cref="ValueSource.Charged"/>), then the family estimate, which draws on
/// prices that were seen (this PC's and the list's), never on a worked-out one.
/// </para>
/// </summary>
public sealed class ItemValues
{
    public static ItemValues Empty { get; } = new([]);

    /// <summary>
    /// What an ordinary vendor charges for an item, divided by what it pays for it. Measured on the vendors' own prices:
    /// 3.68 to 3.72 for every one of twelve vendors, so a quarter (4) is a little too low.
    /// </summary>
    public const double ChargePerPay = 3.7;

    private readonly Dictionary<string, ItemValue> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _charged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<long>> _family = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _custom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ScrapYield>> _scrap = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _ownCount;

    /// <summary>Items with a price of their own (vendor sales, public price list, custom prices, or priced scrap yields).</summary>
    public int Count => _ownCount;

    public ItemValues(IEnumerable<(string Name, long Each)> sold, PublicPrices? list = null, CustomPrices? custom = null, ScrapItems? scrap = null)
    {
        var perItem = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, each) in sold)
            if (each > 0 && name.Length > 0) (perItem.TryGetValue(name, out var l) ? l : perItem[name] = []).Add(each);
        foreach (var (name, prices) in perItem)
        {
            long median = prices.Order().ElementAt(prices.Count / 2);
            _known[name] = new ItemValue(median, ValueSource.Sold);
            foreach (var f in Families(name))
                (_family.TryGetValue(f, out var fl) ? fl : _family[f] = []).Add(median);
        }
        if (list is not null)
        {
            foreach (var (name, price) in list.Items)
            {
                if (_known.ContainsKey(name)) continue;            // a sale on this PC beats the list
                if (price.Pays > 0)
                {
                    _known[name] = new ItemValue(price.Pays, ValueSource.Public);
                    foreach (var f in Families(name))
                        (_family.TryGetValue(f, out var fl) ? fl : _family[f] = []).Add(price.Pays);
                }
                else if (price.ChargeLow > 0) _charged[name] = Math.Max(1, (long)Math.Round(price.ChargeLow / ChargePerPay));
            }
        }
        if (custom is not null)
            foreach (var p in custom.All)
                _custom[p.Item] = p.Copper;
        if (scrap is not null)
            foreach (var r in scrap.All)
                _scrap[r.Item] = r.Yields;
        _ownCount = CountOwn();
    }

    private ItemValues(
        Dictionary<string, ItemValue> known,
        Dictionary<string, long> charged,
        Dictionary<string, List<long>> family,
        Dictionary<string, long> custom,
        Dictionary<string, IReadOnlyList<ScrapYield>> scrap)
    {
        _known = known;
        _charged = charged;
        _family = family;
        _custom = custom;
        _scrap = scrap;
        _ownCount = CountOwn();
    }

    private static Dictionary<string, long> CopyCustom(CustomPrices? custom)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (custom is not null)
            foreach (var p in custom.All)
                map[p.Item] = p.Copper;
        return map;
    }

    private static Dictionary<string, IReadOnlyList<ScrapYield>> CopyScrap(ScrapItems? scrap)
    {
        var map = new Dictionary<string, IReadOnlyList<ScrapYield>>(StringComparer.OrdinalIgnoreCase);
        if (scrap is not null)
            foreach (var r in scrap.All)
                map[r.Item] = r.Yields;
        return map;
    }

    private int CountOwn()
    {
        int count = _known.Count;
        foreach (var k in _custom.Keys)
            if (!_known.ContainsKey(k) && !_scrap.ContainsKey(k))
                count++;
        foreach (var k in _scrap.Keys)
            if (!_known.ContainsKey(k) && Of(k).Source == ValueSource.Scrap)
                count++;
        return count;
    }

    /// <summary>
    /// The families a name belongs to, closest first: its first three words when it has four or more, then its first two
    /// ("Corroded Bronze Plate Boots" → "corroded bronze plate", "corroded bronze"). A name of fewer than three words has none:
    /// it is the item's own name, and a sibling would be a different thing.
    /// </summary>
    public static IReadOnlyList<string> Families(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>();
        for (int n = Math.Min(3, words.Length - 1); n >= 2; n--) list.Add(string.Join(' ', words[..n]).ToLowerInvariant());
        return list;
    }

    /// <summary>The direct scrap yields configured for <paramref name="name"/>, or null when not set to scrap.</summary>
    public IReadOnlyList<ScrapYield>? ScrapOf(string name) => _scrap.TryGetValue(name, out var yields) ? yields : null;

    /// <summary>Expands any nested scrap rules for <paramref name="name"/> up to 4 hops without cycling, merging duplicate materials.</summary>
    public IReadOnlyList<ScrapYield> ExpandScrap(string name)
    {
        if (!_scrap.ContainsKey(name)) return [];
        var result = new List<ScrapYield>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        ExpandInto(name, multiplier: 1, depth: 0, visited, result, index);
        return result;
    }

    private void ExpandInto(string item, int multiplier, int depth, HashSet<string> visited, List<ScrapYield> result, Dictionary<string, int> index)
    {
        if (!_scrap.TryGetValue(item, out var yields)) return;
        foreach (var y in yields)
        {
            int scaled = (int)Math.Min((long)multiplier * y.Qty, 1_000_000L);
            if (depth + 1 < 4 && _scrap.TryGetValue(y.Item, out var sub) && sub.Count > 0 && visited.Add(y.Item))
            {
                ExpandInto(y.Item, scaled, depth + 1, visited, result, index);
                visited.Remove(y.Item);
            }
            else if (index.TryGetValue(y.Item, out var at))
            {
                int sum = (int)Math.Min((long)result[at].Qty + scaled, 1_000_000L);
                result[at] = result[at] with { Qty = sum };
            }
            else
            {
                index[y.Item] = result.Count;
                result.Add(new ScrapYield(y.Item, scaled));
            }
        }
    }

    /// <summary>The item's value: its scrap value if set to scrap, else custom price, else vendor/public price, else worked out from stock or family, else <see cref="ValueSource.None"/>.</summary>
    public ItemValue Of(string name) => Resolve(name, depth: 0, visited: null);

    private ItemValue Resolve(string name, int depth, HashSet<string>? visited)
    {
        if (_scrap.TryGetValue(name, out var yields))
        {
            if (depth >= 4 || yields.Count == 0 || (visited is not null && visited.Contains(name)))
                return new ItemValue(0, ValueSource.ScrapUnpriced);

            visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visited.Add(name);
            decimal total = 0m;
            bool anyEstimated = false;
            try
            {
                foreach (var y in yields)
                {
                    var mv = Resolve(y.Item, depth + 1, visited);
                    if (!mv.Known) return new ItemValue(0, ValueSource.ScrapUnpriced);
                    total += (decimal)mv.Each * y.Qty;
                    anyEstimated |= mv.Estimated;
                }
            }
            finally
            {
                visited.Remove(name);
            }
            long copper = (long)Math.Min(total, CustomPrices.MaxCopper);
            return new ItemValue(copper, anyEstimated ? ValueSource.ScrapEstimated : ValueSource.Scrap);
        }
        if (_custom.TryGetValue(name, out var custom)) return new ItemValue(custom, ValueSource.Custom);
        return NaturalOf(name);
    }

    /// <summary>The item's underlying vendor/public/estimated value ignoring any custom price or scrap override.</summary>
    public ItemValue NaturalOf(string name)
    {
        if (_known.TryGetValue(name, out var own)) return own;
        if (_charged.TryGetValue(name, out var charged)) return new ItemValue(charged, ValueSource.Charged);
        foreach (var f in Families(name))
            if (_family.TryGetValue(f, out var siblings) && siblings.Count > 0)
            {
                var sorted = siblings.Order().ToList();
                return new ItemValue(sorted[sorted.Count / 2], ValueSource.Estimated);
            }
        return default;
    }

    /// <summary>Returns an <see cref="ItemValues"/> sharing the same vendor/family data with <paramref name="custom"/> applied (or <c>this</c> when nothing changes).</summary>
    public ItemValues WithCustom(CustomPrices? custom)
    {
        if (_custom.Count == 0 && (custom is null || custom.Count == 0)) return this;
        return new ItemValues(_known, _charged, _family, CopyCustom(custom), _scrap);
    }

    /// <summary>Returns an <see cref="ItemValues"/> sharing the same vendor/family data with <paramref name="scrap"/> applied (or <c>this</c> when nothing changes).</summary>
    public ItemValues WithScrap(ScrapItems? scrap)
    {
        if (_scrap.Count == 0 && (scrap is null || scrap.Count == 0)) return this;
        return new ItemValues(_known, _charged, _family, _custom, CopyScrap(scrap));
    }

    /// <summary>Returns an <see cref="ItemValues"/> sharing the same vendor/family data with both <paramref name="custom"/> and <paramref name="scrap"/> applied.</summary>
    public ItemValues WithOverrides(CustomPrices? custom, ScrapItems? scrap)
    {
        if (_custom.Count == 0 && _scrap.Count == 0 && (custom is null || custom.Count == 0) && (scrap is null || scrap.Count == 0)) return this;
        return new ItemValues(_known, _charged, _family, CopyCustom(custom), CopyScrap(scrap));
    }

    /// <summary>The sales in some ledger rows: item and what one fetched (the stack's coin divided by its count).</summary>
    public static IEnumerable<(string Name, long Each)> Sales(IEnumerable<LedgerRow> rows)
    {
        foreach (var r in rows)
        {
            if (r.Act != "act_24") continue;
            var item = r.Get("d04").Trim();
            if (item.Length == 0) item = r.Name;
            long total = LedgerText.Coin(r.Text("d03"));
            if (item.Length > 0 && total > 0) yield return (item, total / Math.Max(1, r.Int("d01")));
        }
    }

    public static ItemValues FromLedger(IEnumerable<LedgerRow> rows, PublicPrices? list = null, CustomPrices? custom = null, ScrapItems? scrap = null) =>
        new(Sales(rows), list, custom, scrap);
}
