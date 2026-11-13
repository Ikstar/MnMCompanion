using System.IO;
using System.Text.Json;
using MnMCompanion.Community;

namespace MnMCompanion;

/// <summary>Where a window sits; NaN Left/Top = not placed yet.</summary>
public sealed record Bounds(double Left, double Top, double Width, double Height);

/// <summary>One tab's own window, as kept in <c>settings.json</c>: whether it is out, where it sits, its compact height, its text size, its pin, and whether it is compact.</summary>
public sealed record DetachedTabState
{
    public bool Open { get; set; }
    public Bounds? Bounds { get; set; }
    public double? CompactHeight { get; set; }
    public int Scale { get; set; } = UiScale.Default;
    public bool OnTop { get; set; } = true;
    public bool Compact { get; set; }
}

/// <summary>
/// What the app remembers between runs, in <c>%LOCALAPPDATA%\MnMCompanion\settings.json</c>: which character, the ledger folder
/// when it isn't in the default place, the panel's tab, and where the windows were. It holds nothing from the game.
/// </summary>
public sealed class Settings
{
    public string? Server { get; set; }
    public string? Character { get; set; }

    /// <summary>The game's per-character folder when it isn't in the default place under LocalLow.</summary>
    public string? LedgerRoot { get; set; }

    public string View { get; set; } = "Value";
    public bool OnTop { get; set; } = true;

    /// <summary>The page the small window was left on: Map, Loot, Vendor, Trades, Items, Harvest, Recipes or Market.</summary>
    public string MiniPage { get; set; } = "Loot";

    /// <summary>
    /// The player's choice to show the map of their zone live from the community atlas (mnmatlas.com). No until they say so: showing it makes the
    /// atlas's site see which zone they are in, and their address, like opening it in a browser.
    /// </summary>
    public bool MapsOnline { get; set; }

    /// <summary>
    /// Whether the Recipes page loads its list from the community wiki (monstersandmemories.wiki) by itself: the first time the page is shown, then at most once a
    /// day (when it is opened again). On by default (the page is of no use without the list); the player can switch it off in the ⓘ menu, and then the wiki is never asked. Asking the wiki
    /// makes that site see their internet address, like opening it in a browser; nothing from the game or the ledger is sent.
    /// </summary>
    public bool RecipesOnline { get; set; } = true;

    /// <summary>The skill the Recipes page was left on; null for all skills.</summary>
    public string? RecipeSkill { get; set; }

    /// <summary>
    /// Whether the Market page loads mnm-db.com's auction feed by itself: the first time the page is shown, then once per publish (the site
    /// publishes every six hours) when it is open. On by default, like the recipes (the page is of no use without the feed); the player can switch it
    /// off in the ⓘ menu, and then the site is never asked. Asking it makes that site see their internet address, like opening it in a browser;
    /// nothing from the game or the ledger is sent.
    /// </summary>
    public bool MarketOnline { get; set; } = true;

    /// <summary>The kind of item the Market page was left on (a <c>MarketKind</c> name); null for every kind.</summary>
    public string? MarketKind { get; set; }

    /// <summary>The order the Market page's list was left in ("Active", "Name", "PriceDown", "PriceUp" or "Newest").</summary>
    public string? MarketSort { get; set; }

    /// <summary>
    /// What the History window shows, once the player has chosen: a server and a character, a server alone (everyone on it), or neither
    /// (everyone). Until <see cref="HistoryScopeChosen"/> it shows the character the small window follows.
    /// </summary>
    public string? HistoryServer { get; set; }
    public string? HistoryCharacter { get; set; }
    public bool HistoryScopeChosen { get; set; }
    public Bounds? Panel { get; set; }
    public Bounds? Breakdown { get; set; }

    /// <summary>The timers window's place, and whether it was open when the app closed (it comes back the same way).</summary>
    public Bounds? Timers { get; set; }
    public bool TimersOpen { get; set; }

    /// <summary>Each window's text size as a percent of normal (<see cref="UiScale.Presets"/>), and any tabs taken out of the main window into their own windows.</summary>
    public int PanelScale { get; set; } = UiScale.Default;
    public int BreakdownScale { get; set; } = UiScale.Default;
    public int TimersScale { get; set; } = UiScale.Default;
    public Dictionary<string, DetachedTabState> DetachedTabs { get; set; } = [];

    /// <summary>The timers window's own pin (on top of the game or not; off until the player pins it) and how solid its background is, in percent (see <c>TimerLook</c>; 100 is no fading).</summary>
    public bool TimersOnTop { get; set; }
    public int TimersOpacity { get; set; } = 100;

    /// <summary>
    /// Whether clicks pass through the timers window to the game unless the player holds <see cref="TimersClickThroughKey"/> (off until they turn it on).
    /// While it is on the app checks, a few times a second, whether that one key is down.
    /// </summary>
    public bool TimersClickThrough { get; set; }
    public string TimersClickThroughKey { get; set; } = MnMCompanion.Timers.ClickThroughKey.Default;

    /// <summary>Whether the timers window shows each timer as a button across its top, to start and stop it with a click.</summary>
    public bool TimersButtons { get; set; }

    /// <summary>The page of the History window that was open last ("Loot", "Trades", "Vendors", "Changes" or "Harvest").</summary>
    public string? HistoryPage { get; set; }

    /// <summary>
    /// The player's answer to "share your data with the community?". No (false) until they say Yes. While it is No nothing is sent, they are
    /// asked again every time the app starts, and community data isn't used.
    /// </summary>
    public bool ShareWithCommunity { get; set; }

    /// <summary>
    /// The file layout (<see cref="ContributionBuilder.Schema"/>) the player's Yes was given to; 0 where it was never recorded, which is a Yes from
    /// layout 1. A Yes counts only for the layout the player was shown: when the shared file grows, everyone is asked again
    /// (<see cref="Sharing"/> is false until they say Yes to the new one).
    /// </summary>
    public int ShareConsentSchema { get; set; }

    /// <summary>Whether sharing is on: a Yes that covers the file as it is now. Everything that sends, asks or uses community data goes by this, not by the raw answer.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Sharing => CommunityPolicy.Consented(ShareWithCommunity, ShareConsentSchema, ContributionBuilder.Schema);

    /// <summary>A Yes given to an older file layout: sharing is paused and the question says what has been added.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ConsentNeedsRenewal => CommunityPolicy.NeedsRenewal(ShareWithCommunity, ShareConsentSchema, ContributionBuilder.Schema);

    /// <summary>Records the player's answer: a Yes is a Yes to the file as it is now.</summary>
    public void SetSharing(bool yes)
    {
        ShareWithCommunity = yes;
        if (yes) ShareConsentSchema = ContributionBuilder.Schema;
    }

    /// <summary>When the player's totals were last sent to the community service (UTC), a fingerprint of that file (so an unchanged one is not sent again), and a sentence about how the last try went.</summary>
    public DateTime? LastSharedUtc { get; set; }
    public string? SharedFingerprint { get; set; }

    /// <summary>The same two for the NPC dialogue, which is sent as a second, separate document.</summary>
    public DateTime? LastNpcSharedUtc { get; set; }
    public string? SharedNpcFingerprint { get; set; }

    /// <summary>The same two for the trades with other players, which are sent as a third, separate document.</summary>
    public DateTime? LastTradesSharedUtc { get; set; }
    public string? SharedTradesFingerprint { get; set; }
    public string ShareNote { get; set; } = "";

    /// <summary>
    /// A player joining the group (or you joining a group) starts a new session by itself: the loot so far is archived and every page starts again from the join.
    /// On by default; off, a session starts only when the player presses New session.
    /// </summary>
    public bool NewSessionOnJoin { get; set; } = true;

    /// <summary>Look for a newer version on GitHub when the app starts and every few hours. Off: the app never asks GitHub by itself.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Install a newer version without asking: download it, check it against the checksum published with it, and restart into it once no timer is running.
    /// On by default; off, a newer version is only offered and the player presses Update now. Needs <see cref="CheckForUpdates"/>.
    /// </summary>
    public bool InstallUpdates { get; set; } = true;

    /// <summary>The version this program last installed by itself. If the program that starts afterwards is still older than that, it is never installed unasked again (no restart loop).</summary>
    public string? AutoInstalledVersion { get; set; }

    /// <summary>A version the player chose "Skip this version" for: not offered again (a newer one still is).</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>The version whose "what's new" was shown when the program started: the window opens by itself once, the first run after an update (see <c>WhatsNewPolicy</c>).</summary>
    public string? NotesShownVersion { get; set; }

    /// <summary>There was no settings file when the program started: a first run (nothing is "new" to show yet).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FreshInstall { get; private set; }

    /// <summary>A picture-taking run: nothing is written.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; set; }

    /// <summary>What went wrong reading <c>settings.json</c> when the program started (shown by <c>--diagnose</c>); null when all was well.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadNote { get; private set; }

    private System.Windows.Threading.DispatcherTimer? _saveSoon;

    public static string Folder => AppFolder.Folder;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Brings scales, window bounds, and detached tabs into valid form after reading <c>settings.json</c>.</summary>
    public void Normalize()
    {
        PanelScale = UiScale.Nearest(PanelScale);
        BreakdownScale = UiScale.Nearest(BreakdownScale);
        TimersScale = UiScale.Nearest(TimersScale);

        Panel = ValidBounds(Panel);
        Breakdown = ValidBounds(Breakdown);
        Timers = ValidBounds(Timers);

        var rawSlots = new Dictionary<string, TabSlot>(StringComparer.Ordinal);
        var sources = new Dictionary<string, DetachedTabState>(StringComparer.OrdinalIgnoreCase);
        if (DetachedTabs is not null)
        {
            foreach (var kv in DetachedTabs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (kv.Key is null || kv.Value is null) continue;
                rawSlots[kv.Key] = new TabSlot(kv.Key, kv.Value.Open, kv.Value.Scale, kv.Value.OnTop, kv.Value.Compact);
                if (TabDockPolicy.IsKnownPage(kv.Key, out var canonical)
                    && (!sources.TryGetValue(canonical, out var kept) || (!kept.Open && kv.Value.Open)))
                {
                    sources[canonical] = kv.Value;
                }
            }
        }

        var norm = TabDockPolicy.Normalize(MiniPage, rawSlots);
        MiniPage = norm.ActiveDockedPage;

        var rebuilt = new Dictionary<string, DetachedTabState>(norm.Tabs.Count, StringComparer.Ordinal);
        foreach (var (canonical, slot) in norm.Tabs)
        {
            sources.TryGetValue(canonical, out var src);
            rebuilt[canonical] = new DetachedTabState
            {
                Open = slot.Open,
                Bounds = ValidBounds(src?.Bounds),
                CompactHeight = ValidDimension(src?.CompactHeight),
                Scale = slot.Scale,
                OnTop = slot.OnTop,
                Compact = slot.Compact,
            };
        }
        DetachedTabs = rebuilt;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) is { } loaded)
            {
                loaded.Normalize();
                return loaded;
            }
        }
        catch (JsonException)
        {
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var s = new Settings { LoadNote = "settings.json was unreadable (backed up to settings.json.corrupt)" };
            s.Normalize();
            return s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var fresh = new Settings { FreshInstall = !File.Exists(FilePath) };
        fresh.Normalize();
        return fresh;
    }

    /// <summary>Queues a write in a second on the UI thread, coalescing rapid window moves or scale changes into one write.</summary>
    public void SaveSoon()
    {
        if (ReadOnly) return;
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
        {
            Save();
            return;
        }
        if (_saveSoon is null)
        {
            _saveSoon = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _saveSoon.Tick += (_, _) =>
            {
                _saveSoon.Stop();
                Save();
            };
        }
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    /// <summary>Writes any queued <see cref="SaveSoon"/> change now; does nothing when nothing is waiting.</summary>
    public void FlushSave()
    {
        if (_saveSoon is not { IsEnabled: true }) return;
        _saveSoon.Stop();
        Save();
    }

    public void Save()
    {
        if (_saveSoon is { IsEnabled: true }) _saveSoon.Stop();
        if (ReadOnly) return;
        SanitizeGeometry();
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SanitizeGeometry()
    {
        Panel = ValidBounds(Panel);
        Breakdown = ValidBounds(Breakdown);
        Timers = ValidBounds(Timers);
        if (DetachedTabs is null) return;
        foreach (var state in DetachedTabs.Values)
        {
            if (state is null) continue;
            state.Bounds = ValidBounds(state.Bounds);
            state.CompactHeight = ValidDimension(state.CompactHeight);
        }
    }

    private static Bounds? ValidBounds(Bounds? b) =>
        b is not null && double.IsFinite(b.Left) && double.IsFinite(b.Top) && double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width > 0 && b.Height > 0
            ? b
            : null;

    private static double? ValidDimension(double? value) =>
        value is { } v && double.IsFinite(v) && v > 0 ? v : null;
}
