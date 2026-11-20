using System.Text.RegularExpressions;

namespace MnMCompanion.Gear;

/// <summary>The 21 equipped paperdoll slots on the character Inventory screen.</summary>
public enum GearSlot
{
    Ear1,
    Neck,
    Head,
    Face,
    Ear2,
    Chest,
    Shirt,
    Wrist1,
    Waist,
    Finger1,
    Legs,
    Back,
    Shoulders,
    Wrist2,
    Hands,
    Finger2,
    Feet,
    Primary,
    Secondary,
    Range,
    Ammo,
}

/// <summary>Logical section for grouping equipped slots in the Gear view.</summary>
public enum GearSection
{
    Weapons,
    Armor,
    Jewelry,
}

/// <summary>
/// Geometry and metadata for a single equipped slot. <see cref="RelX"/> and <see cref="RelY"/> are in units of the uniform
/// paperdoll slot pitch <c>P</c>, anchored at the left armor column (<c>X_L</c>) and top jewelry/head row (<c>Y_0</c>).
/// </summary>
public sealed record GearSlotSpec(
    GearSlot Slot,
    string Label,
    GearSection Section,
    double RelX,
    double RelY,
    string[] WikiTokens);

/// <summary>Definitions, coordinates, display order, and empty-label checks for the 21 paperdoll equipment slots.</summary>
public static class GearSlots
{
    private static readonly Regex NonLetters = new("[^a-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> PlaceholderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ear", "neck", "head", "face",
        "chest", "shirt", "arms", "wrist", "waist", "belt", "finger", "ring", "legs",
        "back", "shoulders", "shoulder", "hands", "feet",
        "primary", "secondary", "range", "ranged", "ammo", "item", "empty",
    };

    /// <summary>All 21 paperdoll slots in layout order (top row, left column, right column, bottom weapons row).</summary>
    public static IReadOnlyList<GearSlotSpec> All { get; } =
    [
        // Top row (RelY = 0.0)
        new(GearSlot.Ear1, "Ear 1", GearSection.Jewelry, 1.02, 0.0, ["EAR", "EARS"]),
        new(GearSlot.Neck, "Neck", GearSection.Jewelry, 2.02, 0.0, ["NECK"]),
        new(GearSlot.Head, "Head", GearSection.Armor, 3.02, 0.0, ["HEAD"]),
        new(GearSlot.Face, "Face", GearSection.Armor, 4.02, 0.0, ["FACE"]),
        new(GearSlot.Ear2, "Ear 2", GearSection.Jewelry, 5.02, 0.0, ["EAR", "EARS"]),

        // Left column (RelX = 0.0, RelY = 1.0 .. 6.0)
        new(GearSlot.Chest, "Chest", GearSection.Armor, 0.0, 1.0, ["CHEST", "ROBE"]),
        new(GearSlot.Shirt, "Shirt", GearSection.Armor, 0.0, 2.0, ["SHIRT", "ARMS"]),
        new(GearSlot.Wrist1, "Wrist 1", GearSection.Armor, 0.0, 3.0, ["WRIST"]),
        new(GearSlot.Waist, "Waist", GearSection.Armor, 0.0, 4.0, ["WAIST", "BELT"]),
        new(GearSlot.Finger1, "Finger 1", GearSection.Jewelry, 0.0, 5.0, ["FINGER", "FINGERS", "RING"]),
        new(GearSlot.Legs, "Legs", GearSection.Armor, 0.0, 6.0, ["LEGS"]),

        // Right column (RelX = 6.00, RelY = 1.0 .. 6.0)
        new(GearSlot.Back, "Back", GearSection.Armor, 6.00, 1.0, ["BACK"]),
        new(GearSlot.Shoulders, "Shoulders", GearSection.Armor, 6.00, 2.0, ["SHOULDERS", "SHOULDER"]),
        new(GearSlot.Wrist2, "Wrist 2", GearSection.Armor, 6.00, 3.0, ["WRIST"]),
        new(GearSlot.Hands, "Hands", GearSection.Armor, 6.00, 4.0, ["HANDS"]),
        new(GearSlot.Finger2, "Finger 2", GearSection.Jewelry, 6.00, 5.0, ["FINGER", "FINGERS", "RING"]),
        new(GearSlot.Feet, "Feet", GearSection.Armor, 6.00, 6.0, ["FEET"]),

        // Bottom weapon / ammo row (RelY = 6.0)
        new(GearSlot.Primary, "Primary", GearSection.Weapons, 1.45, 6.0, ["PRIMARY"]),
        new(GearSlot.Secondary, "Secondary", GearSection.Weapons, 2.45, 6.0, ["SECONDARY"]),
        new(GearSlot.Range, "Range", GearSection.Weapons, 3.45, 6.0, ["RANGE", "RANGED"]),
        new(GearSlot.Ammo, "Ammo", GearSection.Weapons, 4.45, 6.0, ["AMMO"]),
    ];

    private static readonly Dictionary<GearSlot, GearSlotSpec> BySlot = All.ToDictionary(s => s.Slot);

    /// <summary>Display order grouped cleanly by Weapons, Armor, and Jewelry.</summary>
    public static IReadOnlyList<GearSlot> DisplayOrder { get; } =
    [
        // Weapons
        GearSlot.Primary, GearSlot.Secondary, GearSlot.Range, GearSlot.Ammo,
        // Armor
        GearSlot.Head, GearSlot.Face, GearSlot.Shoulders, GearSlot.Back, GearSlot.Chest, GearSlot.Shirt,
        GearSlot.Wrist1, GearSlot.Wrist2, GearSlot.Hands, GearSlot.Waist, GearSlot.Legs, GearSlot.Feet,
        // Jewelry
        GearSlot.Neck, GearSlot.Ear1, GearSlot.Ear2, GearSlot.Finger1, GearSlot.Finger2,
    ];

    public static GearSlotSpec Spec(GearSlot slot) => BySlot[slot];

    public static string Label(GearSlot slot) => BySlot[slot].Label;

    public static string SectionTitle(GearSection section) => section switch
    {
        GearSection.Weapons => "Weapons",
        GearSection.Armor => "Armor",
        GearSection.Jewelry => "Jewelry",
        _ => section.ToString(),
    };

    /// <summary>True when OCR text matches one of the empty-slot UI watermark labels ("Ear", "Range", "Item", etc.).</summary>
    public static bool IsPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var clean = NonLetters.Replace(text.Trim().ToLowerInvariant(), "");
        return clean.Length == 0 || PlaceholderWords.Contains(clean);
    }
}
