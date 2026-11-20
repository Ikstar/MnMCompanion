using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MnMCompanion.Gear;
using MnMCompanion.Ledger;
using MnMCompanion.Loot;
using MnMCompanion.Market;
using MnMCompanion.Recipes;

namespace MnMCompanion;

/// <summary>Caches the locally stored auction price book (<see cref="MarketBook.DefaultPath"/>) in memory without touching the network.</summary>
internal static class MarketBookCache
{
    private static readonly object Gate = new();
    private static DateTime _loadedWrite = DateTime.MinValue;
    private static MarketBook? _book;
    private static string? _snapshotServer;
    private static MarketSnapshot? _snapshot;

    public static MarketBook? Book()
    {
        var path = MarketBook.DefaultPath;
        lock (Gate)
        {
            DateTime stamp;
            try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return _book; }
            if (stamp == _loadedWrite) return _book;
            _loadedWrite = stamp;
            _book = stamp == DateTime.MinValue ? null : MarketBook.Load(path);
            _snapshot = null;
            _snapshotServer = null;
            return _book;
        }
    }

    public static MarketSnapshot? Snapshot(string? server)
    {
        var book = Book();
        if (book is null) return null;
        lock (Gate)
        {
            if (_snapshot is not null && string.Equals(_snapshotServer, server, StringComparison.OrdinalIgnoreCase))
                return _snapshot;
            var picked = !string.IsNullOrWhiteSpace(server) && book.Servers.Any(s => s.Equals(server, StringComparison.OrdinalIgnoreCase))
                ? server
                : book.Servers.FirstOrDefault();
            _snapshotServer = server;
            _snapshot = MarketSnapshot.From(book, picked, book.GeneratedAt ?? DateTime.UtcNow);
            return _snapshot;
        }
    }
}

/// <summary>
/// The Gear page: identifies a character's 21 equipped paperdoll items and Persona stats from a game screenshot
/// (via clipboard paste, drag-and-drop, or file picker), enriches them with cached item cards and prices, and lets
/// the player inspect or edit any slot by hand.
/// </summary>
internal sealed class GearPanel : Border, IMiniPage, IDisposable
{
    private static readonly string[] Tabs = ["Equipped", "Persona"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"];

    private readonly LootStore _store;
    private readonly GearStore _gear;
    private readonly Settings _settings;
    private readonly TextBlock _status;
    private readonly Action<LedgerSource>? _followCharacter;
    private readonly string? _gearFile;
    private readonly WindowsGearOcr _ocr = new();

    private readonly TextBlock _sub = new() { FontSize = 11, Foreground = Palette.Muted, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _paste = new() { Style = (Style)Ui.Theme["TextButton"], Content = "Paste", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0), Focusable = false };
    private readonly Button _open = new() { Style = (Style)Ui.Theme["TextButton"], Content = "Open…", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
    private readonly Button _copy = new() { Style = (Style)Ui.Theme["TextButton"], Content = "Copy", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false, Visibility = Visibility.Collapsed };

    private readonly TextBlock _countBig = Figure();
    private readonly TextBlock _countNote = Note();
    private readonly TextBlock _worthBig = Figure();
    private readonly TextBlock _worthNote = Note();

    private readonly TextBlock _compactCount = Figure();
    private readonly TextBlock _compactWorth = Figure();
    private readonly TextBlock _compactSub = new() { FontSize = 11, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly StackPanel _chrome = new();
    private readonly DockPanel _compactHeader = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
    private readonly Dictionary<string, RadioButton> _tabButtons = [];
    private readonly StackPanel _body = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly ScrollViewer _scroll;

    private GearProfile? _snapshotProfile;
    private bool _snapshotLoaded;
    private bool _compact;
    private bool _scanning;
    private string _tab = "Equipped";
    private string _seen = "";

    public GearPanel(
        LootStore store,
        GearStore gear,
        Settings settings,
        TextBlock status,
        Action<LedgerSource>? followCharacter = null,
        string? gearFile = null)
    {
        _store = store;
        _gear = gear;
        _settings = settings;
        _status = status;
        _followCharacter = followCharacter;
        _gearFile = gearFile;

        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
        Padding = new Thickness(12, 10, 12, 8);
        AllowDrop = true;

        _paste.ToolTip = "Read equipped gear from a screenshot on the clipboard (Ctrl+V). Works with Print Screen, Win+Shift+S snips, or a copied image file.";
        _paste.Click += (_, _) => PasteFromClipboard();

        _open.ToolTip = "Pick a screenshot image file (.png, .jpg, .bmp) to read equipped gear from";
        _open.Click += (_, _) => OpenImageDialog();

        _copy.ToolTip = "Copy equipped items and Persona stats to the clipboard as text";
        _copy.Click += (_, _) => CopySummary();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_paste);
        actions.Children.Add(_open);
        actions.Children.Add(_copy);

        var title = new TextBlock { Text = "Gear", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Palette.Text };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(title);
        titles.Children.Add(_sub);

        var head = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(titles);

        var figures = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        figures.Children.Add(StatBlock("EQUIPPED", _countBig, _countNote));
        figures.Children.Add(StatBlock("EST. WORTH", _worthBig, _worthNote));

        var tabGrid = new UniformGrid { Columns = Tabs.Length };
        foreach (var t in Tabs)
        {
            var tabName = t;
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = t },
                Style = (Style)Ui.Theme["Segment"],
                GroupName = "MiniGear",
                IsChecked = t == _tab,
                Focusable = false,
            };
            rb.Checked += (_, _) =>
            {
                if (_tab != tabName)
                {
                    _tab = tabName;
                    _scroll?.ScrollToTop();
                    Draw();
                }
            };
            _tabButtons[t] = rb;
            tabGrid.Children.Add(rb);
        }
        var tabBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Ui.Theme["Subtle"],
            Padding = new Thickness(2),
            Margin = new Thickness(0, 8, 0, 0),
            Child = tabGrid,
        };

        _chrome.Children.Add(head);
        _chrome.Children.Add(figures);
        _chrome.Children.Add(tabBar);

        var compactRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _compactCount.Margin = new Thickness(0, 0, 10, 0);
        compactRight.Children.Add(_compactCount);
        compactRight.Children.Add(_compactWorth);
        DockPanel.SetDock(compactRight, Dock.Right);
        _compactHeader.Children.Add(compactRight);
        _compactHeader.Children.Add(_compactSub);

        var top = new StackPanel();
        top.Children.Add(_chrome);
        top.Children.Add(_compactHeader);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_scroll);
        Child = root;

        PreviewDragOver += OnDragOver;
        Drop += OnDrop;
        _gear.Changed += OnGearChanged;
    }

    public FrameworkElement Element => this;

    public void Dispose() => _gear.Changed -= OnGearChanged;

    public bool NeedsHistory => false;

    public bool SupportsCompact => true;

    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        _chrome.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _compactHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Draw();
    }

    public void Say(string text) => _status.Text = text;

    public void Refresh()
    {
        LoadSnapshotIfNeeded(synchronous: false);
        var sig = Signature();
        if (_body.Children.Count == 0 || sig != _seen) Draw();
    }

    public void RefreshNow()
    {
        LoadSnapshotIfNeeded(synchronous: true);
        Draw();
    }

    public void Tick()
    {
        if (!IsVisible) return;
        _gear.Reload();
        var sig = Signature();
        if (sig != _seen) Draw();
    }

    /// <summary>Selects a tab for snapshot rendering without mouse input.</summary>
    public void Show(string? tab)
    {
        if (!string.IsNullOrWhiteSpace(tab) &&
            Tabs.FirstOrDefault(t => t.Equals(tab, StringComparison.OrdinalIgnoreCase)) is { } found &&
            _tab != found)
        {
            _tab = found;
            _tabButtons[found].IsChecked = true;
        }
        RefreshNow();
    }

    /// <summary>Reads an Inventory/Character screenshot from the clipboard if one is present.</summary>
    public void PasteFromClipboard()
    {
        if (_scanning) return;

        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } source)
            {
                var rgb = ScreenshotLoader.FromBitmapSource(source);
                _ = ScanImageAsync(rgb, "clipboard screenshot");
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (string? file in files)
                {
                    if (file is not null && IsImageFile(file))
                    {
                        var rgb = ScreenshotLoader.FromFile(file);
                        _ = ScanImageAsync(rgb, Path.GetFileName(file));
                        return;
                    }
                }
            }

            Say("Clipboard has no screenshot image. Press Print Screen or Win+Shift+S first.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("Couldn't read screenshot from clipboard: " + ex.Message);
        }
    }

    private void OnGearChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (Signature() != _seen) Draw();
    });

    private GearProfile ActiveProfile => _snapshotProfile ?? _gear.Current;

    private string Signature() =>
        $"{_gear.Version}|{_store.SelectionKey}|{_store.Values.GetHashCode()}|{_store.CustomPrices.Version}|{_store.ScrapItems.Version}|{_tab}|{_compact}|{(_snapshotProfile?.UpdatedUtc.Ticks ?? 0)}";

    private void LoadSnapshotIfNeeded(bool synchronous)
    {
        if (_snapshotLoaded || string.IsNullOrWhiteSpace(_gearFile)) return;
        _snapshotLoaded = true;
        if (_gearFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _snapshotProfile = GearStore.Load(_gearFile) ?? new GearProfile();
            return;
        }
        if (IsImageFile(_gearFile) && File.Exists(_gearFile))
        {
            var rgb = ScreenshotLoader.FromFile(_gearFile);
            if (synchronous)
            {
                var catalog = BuildCatalog();
                var result = GearScreenshotScanner.ScanAsync(rgb, _ocr, catalog).GetAwaiter().GetResult();
                var profile = new GearProfile();
                if (result.Found && result.LabelsVisible)
                    profile.ApplyScan(result.Slots, result.Persona, DateTime.UtcNow);
                _snapshotProfile = profile;
            }
            else
            {
                _ = ScanImageAsync(rgb, Path.GetFileName(_gearFile));
            }
        }
    }

    private void OpenImageDialog()
    {
        if (_scanning) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a character Inventory screenshot",
            Filter = "Screenshots (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var rgb = ScreenshotLoader.FromFile(dialog.FileName);
            _ = ScanImageAsync(rgb, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("Couldn't open image: " + ex.Message);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(IsImageFile))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_scanning) return;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.FirstOrDefault(IsImageFile) is { } path)
            {
                var rgb = ScreenshotLoader.FromFile(path);
                _ = ScanImageAsync(rgb, Path.GetFileName(path));
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                var rgb = ScreenshotLoader.FromBitmapSource(bmp);
                _ = ScanImageAsync(rgb, "dropped image");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("Couldn't read dropped image: " + ex.Message);
        }
    }

    private async Task ScanImageAsync(RgbImage rgb, string sourceLabel)
    {
        if (!_ocr.IsAvailable)
        {
            Say(_ocr.UnavailableReason ?? "Windows OCR is unavailable on this system.");
            return;
        }

        _scanning = true;
        _paste.IsEnabled = false;
        _open.IsEnabled = false;
        Say($"Reading {sourceLabel}…");

        try
        {
            var catalog = BuildCatalog();
            var result = await Task.Run(() => GearScreenshotScanner.ScanAsync(rgb, _ocr, catalog)).ConfigureAwait(true);

            if (!result.Found || !result.LabelsVisible)
            {
                Say(result.Message);
                return;
            }

            if (result.Slots.Count == 0 && result.Persona is not { HasAny: true })
            {
                Say("Found the paperdoll, but every slot appeared empty.");
                return;
            }

            var switchedTo = MaybeFollowScannedCharacter(result.Persona);
            var diff = _gear.ApplyScan(result.Slots, result.Persona, DateTime.UtcNow, persist: !_settings.ReadOnly);
            Draw();

            var note = diff.Summary();
            if (switchedTo is not null) note += $" · saved for {switchedTo.Character}";
            Say(note);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Say("Couldn't scan screenshot: " + ex.Message);
        }
        finally
        {
            _scanning = false;
            _paste.IsEnabled = true;
            _open.IsEnabled = true;
        }
    }

    private LedgerSource? MaybeFollowScannedCharacter(PersonaSnapshot? persona)
    {
        if (string.IsNullOrWhiteSpace(persona?.Name) || _settings.ReadOnly) return null;
        if (_store.Selected is { } current && current.Character.Equals(persona.Name, StringComparison.OrdinalIgnoreCase))
            return null;

        var match = _store.Sources.FirstOrDefault(s => s.Character.Equals(persona.Name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        _followCharacter?.Invoke(match);
        return match;
    }

    private GearCatalog BuildCatalog()
    {
        var market = MarketBookCache.Book();
        var recipes = RecipeWikiCache.Book();
        var owned = _store.Days()
            .Take(14)
            .SelectMany(d => _store.Events(d))
            .Where(e => e.Kind == LootKind.Item && e.Item.Length > 0)
            .Select(e => e.Item)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return GearCatalog.Build(market, recipes, _store.CustomPrices, _store.ScrapItems, _store.PublicPrices, owned);
    }

    private void Draw()
    {
        _seen = Signature();
        var profile = ActiveProfile;
        var market = MarketBookCache.Book();
        var marketSnap = MarketBookCache.Snapshot(_store.Selected?.Server);

        UpdateHeader(profile, market, marketSnap);
        _body.Children.Clear();

        if (_compact)
        {
            RenderCompact(profile, market, marketSnap);
            return;
        }

        if (_tab == "Persona")
            RenderPersona(profile, market);
        else
            RenderEquipped(profile, market, marketSnap);
    }

    private void UpdateHeader(GearProfile profile, MarketBook? market, MarketSnapshot? marketSnap)
    {
        var who = !string.IsNullOrWhiteSpace(profile.Persona?.Name)
            ? profile.Persona.Name
            : _store.Selected?.Character;
        var identity = profile.Persona?.IdentityLine() ?? "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(who)) parts.Add(who);
        if (identity.Length > 0) parts.Add(identity);
        if (profile.EquippedCount > 0 || profile.Persona is { HasAny: true })
        {
            var local = profile.UpdatedUtc.ToLocalTime();
            parts.Add(local.Date == DateTime.Today ? $"updated {local:HH:mm}" : $"updated {local:MMM d}");
        }
        _sub.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Paste or drop a character Inventory screenshot";

        _countBig.Text = $"{profile.EquippedCount} / {GearSlots.All.Count}";
        _countBig.Foreground = profile.EquippedCount > 0 ? Palette.Text : Palette.Muted;

        if (profile.Persona?.VitalsLine() is { Length: > 0 } vitals)
        {
            _countNote.Text = vitals;
        }
        else if (market is not null && profile.EquippedCount > 0)
        {
            double ac = 0, hp = 0;
            foreach (var eq in profile.Slots.Values)
            {
                if (market.Card(eq.Item) is { } card)
                {
                    ac += card.Ac ?? 0;
                    hp += card.Hp ?? 0;
                }
            }
            var cardNotes = new List<string>();
            if (ac > 0) cardNotes.Add($"+{ac:0.#} AC");
            if (hp > 0) cardNotes.Add($"+{hp:0.#} HP");
            _countNote.Text = cardNotes.Count > 0 ? string.Join(" · ", cardNotes) : "slots filled";
        }
        else
        {
            _countNote.Text = profile.EquippedCount == 1 ? "slot filled" : "slots filled";
        }

        long totalCopper = 0;
        int marketCount = 0, vendorCount = 0, unpricedCount = 0;
        foreach (var eq in profile.Slots.Values)
        {
            var price = PriceOf(eq.Item, marketSnap);
            if (price.Known)
            {
                totalCopper += price.Copper;
                if (price.FromMarket) marketCount++;
                else vendorCount++;
            }
            else
            {
                unpricedCount++;
            }
        }

        _worthBig.Text = totalCopper > 0
            ? LootSummary.Coin(totalCopper) + (unpricedCount > 0 ? "+" : "")
            : "—";
        _worthBig.Foreground = totalCopper > 0 ? Palette.Gold : Palette.Muted;

        var worthParts = new List<string>();
        if (marketCount > 0) worthParts.Add($"{marketCount} market");
        if (vendorCount > 0) worthParts.Add($"{vendorCount} vendor");
        if (unpricedCount > 0) worthParts.Add($"{unpricedCount} unpriced");
        _worthNote.Text = worthParts.Count > 0 ? string.Join(" · ", worthParts) : "no priced gear";

        _copy.Visibility = profile.EquippedCount > 0 || profile.Persona is { HasAny: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        _compactCount.Text = _countBig.Text;
        _compactCount.Foreground = _countBig.Foreground;
        _compactWorth.Text = totalCopper > 0 ? _worthBig.Text : "";
        _compactWorth.Foreground = Palette.Gold;
        _compactSub.Text = _sub.Text;
    }

    private void RenderEquipped(GearProfile profile, MarketBook? market, MarketSnapshot? marketSnap)
    {
        if (profile.EquippedCount == 0 && profile.Persona is not { HasAny: true })
        {
            var guide = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Ui.Theme["Subtle"],
                BorderBrush = (Brush)Ui.Theme["Stroke"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 2, 0, 6),
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "Open your character's Inventory window with slot item names shown, take a screenshot or Win+Shift+S snip, then press Paste (Ctrl+V) or drop the image here.",
                FontSize = 11.5,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = "You can also click any slot below to enter or adjust items by hand.",
                FontSize = 11,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            guide.Child = stack;
            _body.Children.Add(guide);
        }

        GearSection? currentSection = null;
        foreach (var slot in GearSlots.DisplayOrder)
        {
            var spec = GearSlots.Spec(slot);
            if (spec.Section != currentSection)
            {
                currentSection = spec.Section;
                _body.Children.Add(new TextBlock
                {
                    Text = GearSlots.SectionTitle(spec.Section),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Palette.Muted,
                    Margin = new Thickness(2, currentSection == GearSection.Weapons ? 2 : 8, 0, 2),
                });
            }
            _body.Children.Add(BuildSlotRow(spec, profile.Get(slot), market, marketSnap));
        }
    }

    private FrameworkElement BuildSlotRow(GearSlotSpec spec, EquippedItem? eq, MarketBook? market, MarketSnapshot? marketSnap)
    {
        var card = eq is null ? null : market?.Card(eq.Item);
        var price = eq is null ? default : PriceOf(eq.Item, marketSnap);
        var summary = card?.Summary() ?? "";

        var row = new ClickRow
        {
            Margin = new Thickness(0, 1.5, 0, 1.5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Activate = () => EditSlot(spec.Slot, eq),
        };
        System.Windows.Automation.AutomationProperties.SetName(row, eq is null ? $"{spec.Label}: empty" : $"{spec.Label}: {eq.Item}");

        var back = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = (Brush)Ui.Theme["Subtle"],
            Padding = new Thickness(9, 5, 9, 5),
        };
        row.Children.Add(back);

        var dock = new DockPanel();
        var slotLabel = new TextBlock
        {
            Text = spec.Label,
            Width = 66,
            FontSize = 11,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(slotLabel, Dock.Left);
        dock.Children.Add(slotLabel);

        var rightText = price.Known
            ? (price.Estimated ? "~" : "") + LootSummary.Coin(price.Copper)
            : eq is { Manual: false, Confidence: < 0.85 } ? "?" : "";
        if (rightText.Length > 0)
        {
            var right = new TextBlock
            {
                Text = rightText,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = price.Known ? (price.FromMarket ? Palette.Gold : Palette.Heal) : Palette.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextAlignment = TextAlignment.Right,
            };
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
        }

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = eq?.Item ?? "—",
            FontSize = 12,
            FontWeight = eq is null ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = eq is null ? Palette.Muted : Palette.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (summary.Length > 0)
        {
            center.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 10.5,
                Foreground = Palette.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        else if (eq is { Manual: false, Confidence: < 0.85, RawOcr.Length: > 0 })
        {
            center.Children.Add(new TextBlock
            {
                Text = $"OCR: {GearTextPostProcessor.CleanAndJoin(eq.RawOcr)} (click to confirm)",
                FontSize = 10,
                Foreground = Palette.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        dock.Children.Add(center);
        back.Child = dock;

        row.ToolTip = new ToolTip
        {
            Style = (Style)Ui.Theme["Tip"],
            Content = new TextBlock
            {
                Text = BuildSlotTooltip(spec, eq, card, price),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            },
        };
        row.MouseEnter += (_, _) => back.Background = (Brush)Ui.Theme["Hover"];
        row.MouseLeave += (_, _) => back.Background = (Brush)Ui.Theme["Subtle"];
        row.MouseLeftButtonUp += (_, _) => EditSlot(spec.Slot, eq);
        PriceMenu.Attach(row, () => BuildSlotContextMenu(spec, eq));
        return row;
    }

    private ContextMenu BuildSlotContextMenu(GearSlotSpec spec, EquippedItem? eq)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = eq is null ? spec.Label : $"{spec.Label} · {eq.Item}", IsEnabled = false });
        menu.Items.Add(new Separator());

        var edit = new MenuItem { Header = eq is null ? $"Set {spec.Label}…" : $"Edit {spec.Label}…" };
        edit.Click += (_, _) => EditSlot(spec.Slot, eq);
        menu.Items.Add(edit);

        if (eq is not null)
        {
            var clear = new MenuItem { Header = $"Clear {spec.Label}" };
            clear.Click += (_, _) =>
            {
                if (_gear.SetSlot(spec.Slot, null, persist: !_settings.ReadOnly))
                    Say($"Cleared {spec.Label}.");
            };
            menu.Items.Add(clear);

            var copy = new MenuItem { Header = "Copy item name" };
            copy.Click += (_, _) => ClipboardText.Copy(eq.Item);
            menu.Items.Add(copy);

            if (PriceMenu.For(this, _store, eq.Item, Draw) is { Items.Count: > 2 } priceMenu)
            {
                menu.Items.Add(new Separator());
                // Skip header + first separator from PriceMenu
                foreach (var item in priceMenu.Items.Cast<object>().Skip(2).ToList())
                {
                    priceMenu.Items.Remove(item);
                    menu.Items.Add(item);
                }
            }
        }
        return menu;
    }

    private void EditSlot(GearSlot slot, EquippedItem? eq)
    {
        if (_snapshotProfile is not null) return;
        GearSlotDialog.Open(Window.GetWindow(this), slot, eq, BuildCatalog(), chosen =>
        {
            if (_gear.SetSlot(slot, chosen, persist: !_settings.ReadOnly))
                Say(chosen is null ? $"Cleared {GearSlots.Label(slot)}." : $"Set {GearSlots.Label(slot)} to {chosen}.");
        });
    }

    private void RenderPersona(GearProfile profile, MarketBook? market)
    {
        var p = profile.Persona;
        if (p is not { HasAny: true })
        {
            _body.Children.Add(LootPanel.Line(
                "No Persona stats read yet. Paste a screenshot that includes the Persona column on the left side of the Inventory window.",
                Palette.Muted));
            return;
        }

        // Identity & Devotion
        var idRows = new List<(string Label, string Value)>();
        if (!string.IsNullOrWhiteSpace(p.Name)) idRows.Add(("Name", p.Name));
        if (p.Level is { } lv) idRows.Add(("Level", lv.ToString()));
        if (!string.IsNullOrWhiteSpace(p.Race)) idRows.Add(("Race", p.Race));
        if (!string.IsNullOrWhiteSpace(p.Class)) idRows.Add(("Class", p.Class));
        if (!string.IsNullOrWhiteSpace(p.Origin)) idRows.Add(("Origin", p.Origin));
        if (!string.IsNullOrWhiteSpace(p.Devotion)) idRows.Add(("Devotion", p.Devotion));
        if (idRows.Count > 0) _body.Children.Add(PersonaCard("Identity", idRows, columns: 2));

        // Vitals
        var vitalRows = new List<(string Label, string Value)>();
        if (p.Health is { } hp) vitalRows.Add(("Health", hp.Show()));
        if (p.Mana is { } mp) vitalRows.Add(("Mana", mp.Show()));
        if (p.Endurance is { } en) vitalRows.Add(("Endurance", en.Show()));
        if (p.ArmorClass is { } ac) vitalRows.Add(("Armor Class", ac.ToString("N0")));
        if (vitalRows.Count > 0) _body.Children.Add(PersonaCard("Vitals", vitalRows, columns: 2));

        // Attributes
        var statRows = PersonaSnapshot.StatOrder
            .Where(k => p.Stats.ContainsKey(k))
            .Select(k => (k, p.Stats[k].ToString("N0")))
            .ToList();
        if (statRows.Count > 0) _body.Children.Add(PersonaCard("Statistics", statRows, columns: 2));

        // Resistances
        var resistRows = PersonaSnapshot.ResistOrder
            .Where(k => p.Resistances.ContainsKey(k))
            .Select(k => (k, p.Resistances[k].ToString("N0")))
            .ToList();
        if (resistRows.Count > 0) _body.Children.Add(PersonaCard("Resistances", resistRows, columns: 2));

        // Item card bonus totals (when market item cards exist)
        if (market is not null && profile.EquippedCount > 0)
        {
            double totalAc = 0, totalHp = 0, totalMana = 0;
            var statSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var eq in profile.Slots.Values)
            {
                if (market.Card(eq.Item) is not { } card) continue;
                totalAc += card.Ac ?? 0;
                totalHp += card.Hp ?? 0;
                totalMana += card.Mana ?? 0;
                foreach (var (k, v) in card.Stats)
                    statSums[k] = statSums.GetValueOrDefault(k) + v;
            }
            var cardRows = new List<(string Label, string Value)>();
            if (totalAc > 0) cardRows.Add(("Item AC", $"+{totalAc:0.#}"));
            if (totalHp != 0) cardRows.Add(("Item HP", $"{totalHp:+0;-0}"));
            if (totalMana != 0) cardRows.Add(("Item Mana", $"{totalMana:+0;-0}"));
            foreach (var k in PersonaSnapshot.StatOrder)
                if (statSums.TryGetValue(k, out var sv) && sv != 0)
                    cardRows.Add(($"Item {k}", $"{sv:+0.#;-0.#}"));
            if (cardRows.Count > 0) _body.Children.Add(PersonaCard("From Known Item Cards", cardRows, columns: 2));
        }
    }

    private void RenderCompact(GearProfile profile, MarketBook? market, MarketSnapshot? marketSnap)
    {
        if (profile.EquippedCount == 0)
        {
            _body.Children.Add(LootPanel.Line("No gear scanned yet — Ctrl+V to paste a screenshot.", Palette.Muted));
            return;
        }

        GearSlot[] priority =
        [
            GearSlot.Primary, GearSlot.Secondary, GearSlot.Range, GearSlot.Chest,
            GearSlot.Head, GearSlot.Legs, GearSlot.Hands, GearSlot.Feet,
        ];
        var shown = new List<GearSlot>();
        foreach (var s in priority)
            if (profile.Slots.ContainsKey(s) && shown.Count < 4)
                shown.Add(s);
        foreach (var s in GearSlots.DisplayOrder)
            if (profile.Slots.ContainsKey(s) && !shown.Contains(s) && shown.Count < 4)
                shown.Add(s);

        foreach (var slot in shown)
            _body.Children.Add(BuildSlotRow(GearSlots.Spec(slot), profile.Get(slot), market, marketSnap));
    }

    private static FrameworkElement PersonaCard(string title, IReadOnlyList<(string Label, string Value)> pairs, int columns)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Ui.Theme["Subtle"],
            BorderBrush = (Brush)Ui.Theme["Stroke"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Muted,
            Margin = new Thickness(0, 0, 0, 5),
        });

        var grid = new UniformGrid { Columns = columns };
        foreach (var (label, value) in pairs)
        {
            var cell = new DockPanel { Margin = new Thickness(0, 2, 12, 2) };
            var val = new TextBlock
            {
                Text = value,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Text,
                TextAlignment = TextAlignment.Right,
            };
            DockPanel.SetDock(val, Dock.Right);
            cell.Children.Add(val);
            cell.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = Palette.Muted,
                Margin = new Thickness(0, 0, 6, 0),
            });
            grid.Children.Add(cell);
        }
        stack.Children.Add(grid);
        card.Child = stack;
        return card;
    }

    private readonly record struct GearPriceInfo(bool Known, long Copper, bool Estimated, bool FromMarket, string Note);

    private GearPriceInfo PriceOf(string itemName, MarketSnapshot? marketSnap)
    {
        var v = _store.Values.Of(itemName);
        if (v.Known && (v.Custom || v.Scrap))
            return new GearPriceInfo(true, v.Each, v.Estimated, FromMarket: false, v.Custom ? "custom price" : "scrap value");

        if (marketSnap?.Named(itemName) is { Headline: { } ask and > 0 } mi)
        {
            var note = mi.Median.HasValue ? $"market median ({mi.Sellers} sellers)" : "market best bid";
            return new GearPriceInfo(true, ask, Estimated: false, FromMarket: true, note);
        }

        if (v.Known)
            return new GearPriceInfo(true, v.Each, v.Estimated, FromMarket: false, v.Estimated ? "vendor estimate" : "vendor price");

        return default;
    }

    private static string BuildSlotTooltip(GearSlotSpec spec, EquippedItem? eq, ItemCard? card, GearPriceInfo price)
    {
        if (eq is null) return $"{spec.Label}: empty\nClick to set an item by hand.";
        var lines = new List<string> { $"{spec.Label}: {eq.Item}" };
        if (card?.Summary() is { Length: > 0 } s) lines.Add(s);
        if (price.Known) lines.Add($"{LootSummary.Coin(price.Copper)} ({price.Note})");
        if (eq.Manual) lines.Add("Set by hand — click to change or clear.");
        else if (!string.IsNullOrWhiteSpace(eq.RawOcr)) lines.Add($"Read from screenshot ({eq.Confidence:P0}): {GearTextPostProcessor.CleanAndJoin(eq.RawOcr)}\nClick to edit.");
        return string.Join("\n", lines);
    }

    private void CopySummary()
    {
        var profile = ActiveProfile;
        var market = MarketBookCache.Book();
        var marketSnap = MarketBookCache.Snapshot(_store.Selected?.Server);
        var text = profile.ToClipboardText(
            _store.Selected?.Character,
            itemSummary: name => market?.Card(name)?.Summary() is { Length: > 0 } s ? s : null,
            itemPrice: name =>
            {
                var p = PriceOf(name, marketSnap);
                return p.Known ? (p.Estimated ? "~" : "") + LootSummary.Coin(p.Copper) : null;
            });

        if (ClipboardText.Copy(text))
            Say($"Copied {profile.EquippedCount} equipped {(profile.EquippedCount == 1 ? "slot" : "slots")} to the clipboard.");
    }

    private static FrameworkElement StatBlock(string label, TextBlock figure, TextBlock note)
    {
        var stack = new StackPanel { Margin = new Thickness(4, 1, 4, 2) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Palette.Muted });
        stack.Children.Add(figure);
        stack.Children.Add(note);
        return stack;
    }

    private static TextBlock Figure() => new TextBlock
    {
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Foreground = Palette.Text,
        TextTrimming = TextTrimming.CharacterEllipsis,
    }.UseDisplay();

    private static TextBlock Note() => new()
    {
        FontSize = 10,
        Foreground = Palette.Muted,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, -1, 0, 0),
    };

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Modal-style small dialog for assigning, correcting, or clearing a single equipped slot.</summary>
internal sealed class GearSlotDialog : Window
{
    private static GearSlotDialog? _open;

    private readonly GearSlot _slot;
    private readonly GearCatalog _catalog;
    private readonly Action<string?> _onSave;
    private readonly TextBox _input;
    private readonly ListBox _suggestions;
    private bool _suppressPick;

    public static void Open(Window? owner, GearSlot slot, EquippedItem? existing, GearCatalog catalog, Action<string?> onSave)
    {
        _open?.Close();
        var window = new GearSlotDialog(owner, slot, existing, catalog, onSave);
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        _open = window;
        window.Show();
        window.Activate();
    }

    private GearSlotDialog(Window? owner, GearSlot slot, EquippedItem? existing, GearCatalog catalog, Action<string?> onSave)
    {
        _slot = slot;
        _catalog = catalog;
        _onSave = onSave;
        Dialogs.Dress(this, $"Edit {GearSlots.Label(slot)}", 430);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title(GearSlots.Label(slot)));

        if (existing is { RawOcr.Length: > 0 } && !existing.Manual)
            stack.Children.Add(Dialogs.Text($"Read from screenshot: {GearTextPostProcessor.CleanAndJoin(existing.RawOcr)}", 6, Palette.Muted, 11.5));

        stack.Children.Add(new TextBlock { Text = "Item name", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 12, 0, 4) });
        _input = new TextBox
        {
            Style = (Style)Ui.Theme["Field"],
            FontSize = 13.5,
            Padding = new Thickness(8, 6, 8, 6),
            Text = existing?.Item ?? "",
        };
        _input.TextChanged += (_, _) => RefreshSuggestions();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Save(_input.Text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        stack.Children.Add(_input);

        stack.Children.Add(new TextBlock { Text = "Suggestions from local catalog", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 12, 0, 4) });
        _suggestions = new ListBox
        {
            Style = (Style)Ui.Theme["NavList"],
            MaxHeight = 200,
            Focusable = false,
        };
        _suggestions.SelectionChanged += (_, _) =>
        {
            if (_suppressPick || _suggestions.SelectedItem is not ListBoxItem { Tag: string picked }) return;
            _input.Text = picked;
            _input.CaretIndex = picked.Length;
        };
        _suggestions.MouseDoubleClick += (_, _) =>
        {
            if (_suggestions.SelectedItem is ListBoxItem { Tag: string picked }) Save(picked);
        };
        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Ui.Theme["Stroke"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = _suggestions,
        });

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (existing is not null)
        {
            var clear = Dialogs.Pill("Clear slot", 12, click: () => Save(null));
            DockPanel.SetDock(clear, Dock.Left);
            buttons.Children.Add(clear);
        }

        var save = Dialogs.Pill("Save", 16, bold: true, click: () => Save(_input.Text));
        var cancel = Dialogs.Pill("Cancel", 14, click: Close);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        Content = stack;
        RefreshSuggestions();
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void RefreshSuggestions()
    {
        _suppressPick = true;
        _suggestions.Items.Clear();
        var matches = _catalog.Suggest(_slot, _input.Text, 8);
        foreach (var entry in matches)
        {
            var row = new DockPanel();
            if (!string.IsNullOrWhiteSpace(entry.Slot))
            {
                var slotText = new TextBlock
                {
                    Text = entry.Slot,
                    FontSize = 10.5,
                    Foreground = GearItemMatcher.IsSlotCompatible(entry.Slot, _slot) ? Palette.Heal : Palette.Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(slotText, Dock.Right);
                row.Children.Add(slotText);
            }
            row.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 12,
                Foreground = Palette.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _suggestions.Items.Add(new ListBoxItem { Content = row, Tag = entry.Name });
        }
        if (matches.Count == 0)
        {
            _suggestions.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = "No catalog matches — press Save to keep what you typed.", FontSize = 11.5, Foreground = Palette.Muted },
                IsEnabled = false,
            });
        }
        _suppressPick = false;
    }

    private void Save(string? value)
    {
        _onSave(CustomPrices.CleanName(value));
        Close();
    }
}
