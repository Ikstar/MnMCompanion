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
}

/// <summary>What one of an item is worth to a vendor (or by custom price), in copper, and how that is known.</summary>
public readonly record struct ItemValue(long Each, ValueSource Source)
{
    public bool Known => Source != ValueSource.None;

    /// <summary>Worked out, not seen (from a vendor's stock price or the item's family): shown with "~".</summary>
    public bool Estimated => Source is ValueSource.Estimated or ValueSource.Charged;

    /// <summary>Set by hand by the player via <see cref="CustomPrices"/>.</summary>
    public bool Custom => Source == ValueSource.Custom;
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
    private readonly int _ownCount;

    /// <summary>Items with a price of their own (vendor sales, public price list, or custom prices).</summary>
    public int Count => _ownCount;

    public ItemValues(IEnumerable<(string Name, long Each)> sold, PublicPrices? list = null, CustomPrices? custom = null)
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
        _ownCount = CountOwn();
    }

    private ItemValues(Dictionary<string, ItemValue> known, Dictionary<string, long> charged, Dictionary<string, List<long>> family, CustomPrices? custom)
    {
        _known = known;
        _charged = charged;
        _family = family;
        if (custom is not null)
            foreach (var p in custom.All)
                _custom[p.Item] = p.Copper;
        _ownCount = CountOwn();
    }

    private int CountOwn() => _custom.Count == 0 ? _known.Count : _known.Count + _custom.Keys.Count(k => !_known.ContainsKey(k));

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

    /// <summary>The item's value: its custom price if set, else its vendor/public price, else worked out from stock or family, else <see cref="ValueSource.None"/>.</summary>
    public ItemValue Of(string name) =>
        _custom.TryGetValue(name, out var custom) ? new ItemValue(custom, ValueSource.Custom) : NaturalOf(name);

    /// <summary>The item's underlying vendor/public/estimated value ignoring any custom price override.</summary>
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
        return new ItemValues(_known, _charged, _family, custom);
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

    public static ItemValues FromLedger(IEnumerable<LedgerRow> rows, PublicPrices? list = null, CustomPrices? custom = null) => new(Sales(rows), list, custom);
}
