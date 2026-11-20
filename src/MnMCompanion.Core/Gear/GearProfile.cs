using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MnMCompanion.Loot;

namespace MnMCompanion.Gear;

/// <summary>A current/maximum pair such as Health 555 / 555 or Mana 5 / 5.</summary>
public sealed record VitalStat(int Current, int Max)
{
    public string Show() => $"{Current:N0} / {Max:N0}";
}

/// <summary>Character identity, vitals, attributes, resistances, and devotion read from the Persona pane.</summary>
public sealed record PersonaSnapshot
{
    public static readonly string[] StatOrder = ["STR", "STA", "AGI", "DEX", "INT", "WIS", "CHA"];
    public static readonly string[] ResistOrder = ["COLD", "CORR", "DIS", "ELEC", "FIRE", "HOLY", "MAG", "POI"];

    public string? Name { get; init; }
    public int? Level { get; init; }
    public string? Race { get; init; }
    public string? Class { get; init; }
    public string? Origin { get; init; }
    public VitalStat? Health { get; init; }
    public VitalStat? Mana { get; init; }
    public VitalStat? Endurance { get; init; }
    public int? ArmorClass { get; init; }
    public Dictionary<string, int> Stats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Resistances { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Devotion { get; init; }

    [JsonIgnore]
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Name) ||
        Level.HasValue ||
        !string.IsNullOrWhiteSpace(Race) ||
        !string.IsNullOrWhiteSpace(Class) ||
        !string.IsNullOrWhiteSpace(Origin) ||
        Health is not null ||
        Mana is not null ||
        Endurance is not null ||
        ArmorClass.HasValue ||
        Stats.Count > 0 ||
        Resistances.Count > 0 ||
        !string.IsNullOrWhiteSpace(Devotion);

    /// <summary>e.g. "Level 20 Goblin Archer" (leaving out parts that were not present).</summary>
    public string IdentityLine()
    {
        var parts = new List<string>();
        if (Level is { } lv) parts.Add($"Level {lv}");
        if (!string.IsNullOrWhiteSpace(Race)) parts.Add(Race);
        if (!string.IsNullOrWhiteSpace(Class)) parts.Add(Class);
        return string.Join(" ", parts);
    }

    /// <summary>e.g. "AC 69 · HP 555/555 · Mana 5/5 · End 100/100".</summary>
    public string VitalsLine()
    {
        var parts = new List<string>();
        if (ArmorClass is { } ac) parts.Add($"AC {ac:N0}");
        if (Health is { } hp) parts.Add($"HP {hp.Current:N0}/{hp.Max:N0}");
        if (Mana is { } mp) parts.Add($"Mana {mp.Current:N0}/{mp.Max:N0}");
        if (Endurance is { } en) parts.Add($"End {en.Current:N0}/{en.Max:N0}");
        return string.Join(" · ", parts);
    }
}

/// <summary>An item assigned to a single paperdoll slot, either from OCR or manual edit.</summary>
public sealed record EquippedItem(
    GearSlot Slot,
    string Item,
    string? RawOcr = null,
    double Confidence = 1.0,
    bool Manual = false);

/// <summary>Summary of changes produced when applying a screenshot scan over a previous gear profile.</summary>
public sealed record GearDiff(int Equipped, int Added, int Changed, int Cleared, IReadOnlyList<string> Changes)
{
    public string Summary()
    {
        int totalUpdates = Added + Changed + Cleared;
        var baseText = $"Read {Equipped}/{GearSlots.All.Count} equipped slots";
        if (totalUpdates == 0) return baseText;
        var details = new List<string>();
        if (Added > 0) details.Add($"{Added} added");
        if (Changed > 0) details.Add($"{Changed} changed");
        if (Cleared > 0) details.Add($"{Cleared} cleared");
        return $"{baseText} ({string.Join(", ", details)})";
    }
}

/// <summary>Persisted gear snapshot and Persona profile for one character.</summary>
public sealed class GearProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public PersonaSnapshot? Persona { get; set; }
    public Dictionary<GearSlot, EquippedItem> Slots { get; set; } = [];

    [JsonIgnore]
    public int EquippedCount => Slots.Count;

    public EquippedItem? Get(GearSlot slot) => Slots.TryGetValue(slot, out var item) ? item : null;

    /// <summary>
    /// Replaces equipped slots with <paramref name="scannedSlots"/>, updates <see cref="Persona"/> when the scan captured one
    /// (keeping any earlier Persona when a tight paperdoll-only crop was scanned), and returns a diff against the previous state.
    /// </summary>
    public GearDiff ApplyScan(IReadOnlyDictionary<GearSlot, EquippedItem> scannedSlots, PersonaSnapshot? persona, DateTime nowUtc)
    {
        int added = 0, changed = 0, cleared = 0;
        var changes = new List<string>();

        foreach (var spec in GearSlots.All)
        {
            var slot = spec.Slot;
            Slots.TryGetValue(slot, out var prev);
            scannedSlots.TryGetValue(slot, out var next);

            if (prev is null && next is not null)
            {
                added++;
                changes.Add($"+ {spec.Label}: {next.Item}");
            }
            else if (prev is not null && next is null)
            {
                cleared++;
                changes.Add($"- {spec.Label}: {prev.Item}");
            }
            else if (prev is not null && next is not null && !string.Equals(prev.Item, next.Item, StringComparison.OrdinalIgnoreCase))
            {
                changed++;
                changes.Add($"{spec.Label}: {prev.Item} -> {next.Item}");
            }
        }

        Slots = new Dictionary<GearSlot, EquippedItem>();
        foreach (var (slot, item) in scannedSlots)
            if (CustomPrices.CleanName(item.Item) is { } clean)
                Slots[slot] = item with { Slot = slot, Item = clean };

        if (persona is { HasAny: true }) Persona = persona;
        UpdatedUtc = nowUtc;
        return new GearDiff(Slots.Count, added, changed, cleared, changes);
    }

    /// <summary>Sets or clears a single slot by hand. Returns true if the slot state changed.</summary>
    public bool SetSlot(GearSlot slot, string? itemName, DateTime? nowUtc = null)
    {
        var clean = CustomPrices.CleanName(itemName);
        if (clean is null)
        {
            if (!Slots.Remove(slot)) return false;
            UpdatedUtc = nowUtc ?? DateTime.UtcNow;
            return true;
        }

        if (Slots.TryGetValue(slot, out var existing) && string.Equals(existing.Item, clean, StringComparison.Ordinal) && existing.Manual)
            return false;

        Slots[slot] = new EquippedItem(slot, clean, RawOcr: null, Confidence: 1.0, Manual: true);
        UpdatedUtc = nowUtc ?? DateTime.UtcNow;
        return true;
    }

    /// <summary>Formats equipped items and Persona details as plain text for pasting into chat or notes.</summary>
    public string ToClipboardText(string? characterFallback = null, Func<string, string?>? itemSummary = null, Func<string, string?>? itemPrice = null)
    {
        var sb = new StringBuilder();
        var name = !string.IsNullOrWhiteSpace(Persona?.Name) ? Persona.Name : characterFallback;
        var identity = Persona?.IdentityLine() ?? "";
        if (!string.IsNullOrWhiteSpace(name) || identity.Length > 0)
        {
            sb.Append(string.IsNullOrWhiteSpace(name) ? "Equipped Gear" : name);
            if (identity.Length > 0) sb.Append(" — ").Append(identity);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Equipped Gear");
        }

        if (Persona?.VitalsLine() is { Length: > 0 } vitals)
            sb.AppendLine(vitals);

        foreach (var slot in GearSlots.DisplayOrder)
        {
            if (!Slots.TryGetValue(slot, out var eq)) continue;
            var line = new StringBuilder();
            line.Append('[').Append(GearSlots.Label(slot)).Append("] ").Append(eq.Item);
            if (itemPrice?.Invoke(eq.Item) is { Length: > 0 } price) line.Append(" — ").Append(price);
            if (itemSummary?.Invoke(eq.Item) is { Length: > 0 } summary) line.Append(" (").Append(summary).Append(')');
            sb.AppendLine(line.ToString());
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Persists per-character equipped gear in <see cref="FileName"/> inside a character's state folder and notifies open windows when updated.
/// </summary>
public sealed class GearStore
{
    public const string FileName = "gear.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private string? _folder;
    private GearProfile _profile = new();
    private DateTime _loadedWrite;
    private long _version;

    public GearStore(string? folder = null)
    {
        if (!string.IsNullOrWhiteSpace(folder)) PointTo(folder);
    }

    public long Version => Volatile.Read(ref _version);

    public string? Folder
    {
        get { lock (_gate) return _folder; }
    }

    public string? FilePath
    {
        get { lock (_gate) return _folder is null ? null : Path.Combine(_folder, FileName); }
    }

    public GearProfile Current
    {
        get { lock (_gate) return _profile; }
    }

    public event Action? Changed;

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public static GearProfile? Load(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<GearProfile>(File.ReadAllText(filePath), Json);
            if (loaded is null || loaded.Version > GearProfile.CurrentVersion) return null;
            loaded.Slots ??= [];
            return Normalize(loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool Save(GearProfile profile, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, Json));
            File.Move(temp, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Switches the active character state folder and loads its saved <c>gear.json</c> (or an empty profile if none exists).</summary>
    public bool PointTo(string? folder)
    {
        bool raised = false;
        lock (_gate)
        {
            if (string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)) return false;
            _folder = folder;
            var path = folder is null ? null : PathIn(folder);
            _profile = Load(path) ?? new GearProfile();
            _loadedWrite = Stamp(path);
            Interlocked.Increment(ref _version);
            raised = true;
        }
        if (raised) Changed?.Invoke();
        return true;
    }

    /// <summary>Reloads from disk if another window or external edit updated <c>gear.json</c>.</summary>
    public bool Reload()
    {
        bool updated = false;
        lock (_gate)
        {
            var path = _folder is null ? null : PathIn(_folder);
            var stamp = Stamp(path);
            if (stamp == _loadedWrite) return false;
            _profile = Load(path) ?? new GearProfile();
            _loadedWrite = stamp;
            Interlocked.Increment(ref _version);
            updated = true;
        }
        if (updated) Changed?.Invoke();
        return true;
    }

    public GearDiff ApplyScan(IReadOnlyDictionary<GearSlot, EquippedItem> scannedSlots, PersonaSnapshot? persona, DateTime nowUtc, bool persist = true)
    {
        GearDiff diff;
        lock (_gate)
        {
            diff = _profile.ApplyScan(scannedSlots, persona, nowUtc);
            if (persist && _folder is not null)
            {
                var path = PathIn(_folder);
                if (Save(_profile, path)) _loadedWrite = Stamp(path);
            }
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
        return diff;
    }

    public bool SetSlot(GearSlot slot, string? itemName, bool persist = true)
    {
        bool changed;
        lock (_gate)
        {
            changed = _profile.SetSlot(slot, itemName);
            if (!changed) return false;
            if (persist && _folder is not null)
            {
                var path = PathIn(_folder);
                if (Save(_profile, path)) _loadedWrite = Stamp(path);
            }
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces the active profile directly (e.g. from a snapshot JSON file) without touching disk.</summary>
    public void ReplaceInMemory(GearProfile profile)
    {
        lock (_gate)
        {
            _profile = Normalize(profile);
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
    }

    private static DateTime Stamp(string? path)
    {
        if (path is null) return DateTime.MinValue;
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (IOException) { return DateTime.MinValue; }
    }

    private static GearProfile Normalize(GearProfile raw)
    {
        var cleanSlots = new Dictionary<GearSlot, EquippedItem>();
        foreach (var (slot, eq) in raw.Slots)
        {
            if (eq is null) continue;
            if (CustomPrices.CleanName(eq.Item) is { } clean)
                cleanSlots[slot] = eq with { Slot = slot, Item = clean };
        }
        raw.Slots = cleanSlots;
        if (raw.Persona is { } p)
        {
            raw.Persona = p with
            {
                Stats = new Dictionary<string, int>(p.Stats ?? [], StringComparer.OrdinalIgnoreCase),
                Resistances = new Dictionary<string, int>(p.Resistances ?? [], StringComparer.OrdinalIgnoreCase),
            };
        }
        return raw;
    }
}
