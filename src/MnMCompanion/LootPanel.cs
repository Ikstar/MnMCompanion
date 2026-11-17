using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MnMCompanion.Loot;

namespace MnMCompanion;

/// <summary>
/// The loot panel, laid out like the DPS meter: a headline (the day's coin plus what its items are worth), tabs, and ranked
/// rows with a bar behind each (the looters by value, the items, the mobs) or the feed of loot. It shows one <em>session</em>
/// of a day (by default today's current one: "New session" archives what has been looted so far and starts again from
/// nothing, the ledger keeping everything) or the whole day, steps through earlier days, and a click on a name opens the full
/// breakdown on that player. <see cref="Refresh"/> rebuilds it (the window calls it when the ledger changed), which is cheap:
/// a day is a few thousand events at most.
/// </summary>
internal sealed class LootPanel : Border, IMiniPage
{
    private const int FeedRows = 200;
    private static readonly string[] Views = ["Value", "Items", "Mobs", "Feed"];

    private readonly LootStore _store;
    private readonly Action<string> _viewChanged;
    private readonly Action<DateOnly, string?, LootSession?> _openBreakdown;

    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Palette.Text, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 11, Foreground = Palette.Muted, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    // The two figures at the top: what reached the ledger's owner, and what the whole group looted
    private readonly TextBlock _ours = Figure(), _oursNote = Note(), _total = Figure(), _totalNote = Note();
    private readonly FrameworkElement _oursBlock, _totalBlock;
    // The coin at the top as one line to paste, copied by a click on either figure
    private string _coinLine = "";
    // The Value tab's copy buttons, in the session chip's row
    private readonly Border _copyHost = new();
    private readonly Button _scope = new() { Style = (Style)Ui.Theme["TextButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(9, 4, 9, 4) };
    private readonly TextBox _filter = new() { Style = (Style)Ui.Theme["Field"], Margin = new Thickness(0, 6, 0, 0), ToolTip = "Show only loot with this looter, item or corpse in it", Visibility = Visibility.Collapsed };
    private readonly StackPanel _body = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 10, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    // The full top (the session header, the tabs and the filter), and the compact headline shown instead of it
    private readonly StackPanel _headerStack = new();
    private readonly UniformGrid _compactBar = new() { Columns = 2, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBlock _compactOurs = Figure(), _compactOursNote = Note(), _compactTotal = Figure(), _compactTotalNote = Note();
    private readonly FrameworkElement _compactOursBlock, _compactTotalBlock;
    private bool _compact;

    private readonly SessionScope _session;
    private string _view;
    private string _note = "";

    public LootPanel(LootStore store, SessionScope session, string view, Action<string> viewChanged, Action<DateOnly, string?, LootSession?> openBreakdown)
    {
        _store = store;
        _session = session;
        _session.Changed += Refresh;
        _viewChanged = viewChanged;
        _openBreakdown = openBreakdown;
        _view = Views.Contains(view) ? view : Views[0];
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
        Padding = new Thickness(12, 10, 12, 8);

        var tabBar = new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Ui.Theme["Subtle"], Padding = new Thickness(2), Margin = new Thickness(0, 8, 0, 0) };
        var grid = new UniformGrid { Columns = Views.Length };
        foreach (var v in Views)
        {
            var view_ = v;
            var rb = new RadioButton { Content = new TextBlock { Text = v }, Style = (Style)Ui.Theme["Segment"], GroupName = "LootView", IsChecked = v == _view, Focusable = false };
            rb.Checked += (_, _) => { _view = view_; _viewChanged(view_); Refresh(); };
            grid.Children.Add(rb);
        }
        tabBar.Child = grid;

        _filter.TextChanged += (_, _) => Refresh();

        _oursBlock = Block("OURS", _ours, _oursNote);
        _totalBlock = Block("TOTAL", _total, _totalNote);
        var totalsRow = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        totalsRow.Children.Add(_oursBlock);
        totalsRow.Children.Add(_totalBlock);
        var header = SessionHeader.Build(_session, _title, _sub, null, null, _scope, totalsRow, _copyHost);

        _compactOursBlock = Block("OURS", _compactOurs, _compactOursNote);
        _compactTotalBlock = Block("TOTAL", _compactTotal, _compactTotalNote);
        _compactBar.Children.Add(_compactOursBlock);
        _compactBar.Children.Add(_compactTotalBlock);

        _headerStack.Children.Add(header);
        _headerStack.Children.Add(tabBar);
        _headerStack.Children.Add(_filter);
        var stack = new StackPanel();
        stack.Children.Add(_headerStack);
        stack.Children.Add(_compactBar);
        var root = new DockPanel();
        DockPanel.SetDock(stack, Dock.Top);
        root.Children.Add(stack);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _body });
        Child = root;
    }

    private static TextBlock Figure() => new TextBlock
    {
        FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Palette.Text, TextTrimming = TextTrimming.CharacterEllipsis,
    }.UseDisplay();

    private static TextBlock Note() => new() { FontSize = 10, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -1, 0, 0) };

    /// <summary>
    /// One of the two figures at the top. A click copies the coin as one line (<see cref="CopyCoinLine"/>), the same from either figure, so it is a
    /// <see cref="ClickRow"/>: it has a hand cursor, lights up under the pointer and can be invoked without a mouse.
    /// </summary>
    private FrameworkElement Block(string label, TextBlock figure, TextBlock note)
    {
        var stack = new StackPanel { Margin = new Thickness(4, 1, 4, 2) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Palette.Muted });
        stack.Children.Add(figure);
        stack.Children.Add(note);
        var back = new Border { CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        // The padding is taken back from the margin so the text stays where it was (the row is no taller than before)
        var block = new ClickRow { Margin = new Thickness(-4, -1, 4, -2), Background = Brushes.Transparent, Cursor = Cursors.Hand, Activate = CopyCoinLine };
        System.Windows.Automation.AutomationProperties.SetName(block, $"{label}: copy the coin as one line");
        block.Children.Add(back);
        block.Children.Add(stack);
        block.MouseEnter += (_, _) => back.Background = (Brush)Ui.Theme["Hover"];
        block.MouseLeave += (_, _) => back.Background = Brushes.Transparent;
        block.MouseLeftButtonUp += (_, _) => CopyCoinLine();
        return block;
    }

    /// <summary>Puts the coin line (the group's coin, your split and your share of it) on the clipboard and says so in the status line.</summary>
    private void CopyCoinLine()
    {
        if (_coinLine.Length == 0) return;
        Report(ClipboardText.Copy(_coinLine) ? "Copied the coin line to the clipboard." : "The clipboard is busy: try again.");
    }

    private void Report(string text) => _status.Text = text;

    /// <summary>An icon button whose tooltip, in the app's dark style, names the action in bold and says what it does under it.</summary>
    internal static Button Icon(string glyph, string title, string tip, Action click, double size = 26, double fontSize = 12)
    {
        var b = new Button { Content = glyph, Style = (Style)Ui.Theme["IconButton"], Width = size, Height = size, FontSize = fontSize };
        var text = new StackPanel { MaxWidth = 300 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Palette.Text });
        text.Children.Add(new TextBlock { Text = tip, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted, Margin = new Thickness(0, 3, 0, 0) });
        b.ToolTip = new ToolTip { Content = text, Style = (Style)Ui.Theme["Tip"] };
        ToolTipService.SetInitialShowDelay(b, 150);
        ToolTipService.SetShowDuration(b, 30000);
        b.Click += (_, _) => click();
        return b;
    }

    public FrameworkElement Element => this;

    public bool NeedsHistory => false;

    public void RefreshNow() => Refresh();

    public void Tick() { }

    public bool SupportsCompact => true;

    /// <summary>Switches between the full page and the compact one (your coin split, the group's total and the three items worth most).</summary>
    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        _headerStack.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _compactBar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact) _status.Text = "";
        Refresh();
    }

    /// <summary>The status line (what was copied, what's unpriced). The window puts it in its bottom bar.</summary>
    public TextBlock StatusLine => _status;

    private static DateOnly Today => SessionScope.Today;
    private DateOnly Day => _session.Day;

    /// <summary>Which tab is showing.</summary>
    public string View => _view;

    /// <summary>Shows a day (today follows the clock) and redraws.</summary>
    public void ShowDay(DateOnly day) => _session.ShowDay(day);

    /// <summary>Shows a line in the status bar until the next refresh.</summary>
    public void Say(string text)
    {
        _note = text;
        Refresh();
    }

    private LootSession? ActiveSession() => _session.ActiveSession();

    private IReadOnlyList<LootEvent> Events() => _store.Events(Day, ActiveSession());

    // ------------------------------------------------------------------ showing a day

    public void Refresh()
    {
        var day = Day;
        var session = ActiveSession();
        var all = _store.Events(day, session);
        var d = LootDay.Of(day, all, _store.Values);
        var totals = d.Totals;

        _title.Text = day == Today ? $"Today · {day.ToString("ddd d MMM", CultureInfo.CurrentCulture)}" : day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        _sub.Text = $"{Plural(totals.Mobs, "mob")} · {(d.Unrecorded > 0 ? $"{totals.Items:N0}+ items" : Plural(totals.Items, "item"))} · {Plural(d.Members.Count, "looter")}";
        var ours = d.Ours;
        bool mine = d.Members.Any(m => m.You);
        _coinLine = LootSummary.CoinLine(totals.Copper, ours, mine);
        const string Click = "Click to copy the coin as one line: ";
        _total.Text = LootSummary.Worth(d.Total, d.Approximate, d.Unpriced > 0 || d.Unrecorded > 0);
        _totalNote.Text = $"coin {LootSummary.Coin(totals.Copper)} + items {LootSummary.Worth(d.ItemValue, d.Approximate, d.Unrecorded > 0)}";
        _totalBlock.ToolTip = $"Everyone's: coin {LootSummary.Coin(totals.Copper)} (every purse counted once, whole) plus what a vendor would pay for the items: {LootSummary.Worth(d.ItemValue, d.Approximate, d.Unpriced > 0)}" + Environment.NewLine +
                              $"{d.VendorPriced:N0} items priced from vendor sales" + (d.Custom > 0 ? $", {d.Custom:N0} at your custom prices" : "") + (d.Scrapped > 0 ? $", {d.Scrapped:N0} scrapped" : "") + $", {d.FamilyEstimated:N0} valued by their family (~), {d.PlainUnpriced:N0} with no known price (not counted, +)" + Environment.NewLine + Click + _coinLine;
        _ours.Text = mine ? LootSummary.Worth(ours.Total, ours.Approximate, ours.Unpriced > 0 || ours.Unrecorded > 0) : "—";
        _oursNote.Text = mine ? $"coin {(ours.EstimatedShares > 0 ? "~" : "")}{LootSummary.Coin(ours.Copper)} + items {LootSummary.Worth(ours.ItemValue, ours.Estimated > 0, ours.Unrecorded > 0)}" : "nothing of yours yet";
        _oursBlock.ToolTip = mine
            ? $"What reached you: coin {(ours.EstimatedShares > 0 ? "~" : "")}{LootSummary.Coin(ours.Copper)} (in a group only your shares of the purses, your own included; the purses you looted alone, whole" +
              (totals.Share > 0 ? $"; {LootSummary.Coin(totals.Share)} of the purses reached you as splits" : "") + $") plus what a vendor would pay for the {Plural(ours.Items, "item")} you looted: {LootSummary.Worth(ours.ItemValue, ours.Estimated > 0, ours.Unpriced > 0)}" +
              (ours.EstimatedShares > 0 ? Environment.NewLine + $"The ledger doesn't say what you were handed of the {ours.EstimatedShares:N0} purses you looted in a group, so those shares are estimated (~) from what you got of other players' purses." : "")
            : "Nothing has reached you yet in this view: no coin and no items.";
        _oursBlock.ToolTip += Environment.NewLine + Click + _coinLine;
        const string Unrecorded = "The game's ledger can't record the loot of a monster with an apostrophe in its name (Skri'Var, Mur'Hua…): it writes no item rows for those corpses and doesn't name the monster, only the coin. Companion can't show their items.";
        if (d.Unrecorded > 0) _totalBlock.ToolTip += Environment.NewLine + Unrecorded;
        _compactOurs.Text = mine ? (ours.EstimatedShares > 0 ? "~" : "") + LootSummary.Coin(ours.Copper) : "—";
        _compactOursNote.Text = mine ? $"your coin · items {LootSummary.Worth(ours.ItemValue, ours.Estimated > 0, ours.Unrecorded > 0)}" : "nothing of yours yet";
        _compactOursBlock.ToolTip = _oursBlock.ToolTip;
        _compactTotal.Text = LootSummary.Coin(totals.Copper);
        _compactTotalNote.Text = $"coin · items worth {LootSummary.Worth(d.ItemValue, d.Approximate, d.Unpriced > 0 || d.Unrecorded > 0)}";
        _compactTotalBlock.ToolTip = _totalBlock.ToolTip;
        var first = all.Count > 0 ? all.Min(e => e.Time) : (DateTime?)null;
        // Trimmed rather than pushing the copy buttons off the row when the label is long
        _scope.Content = new TextBlock { Text = (session is null ? "Whole day" : session.Open ? "This session: " + session.Label(first) : "Session " + session.Label(first)) + "  ▾", TextTrimming = TextTrimming.CharacterEllipsis };
        _scope.ToolTip = "Which part of the day to show: this session, an earlier one, or the whole day. A new session archives what has been looted so far.";

        // The compact page keeps the bar quiet (so a mini window shows no status bar at all) unless something went wrong or the player just did something
        if (IsVisible) Report(_store.Selected is null ? ""
            : _store.Error is { } error ? $"Can't read the ledger: {error}"
            : _note.Length > 0 ? _note
            : _compact ? ""
            : d.Unrecorded > 0 ? $"{Plural(d.Unrecorded, "corpse")}: items missing from the ledger (names with an apostrophe)"
            : d.Unpriced > 0 ? $"{Plural(d.Unpriced, "item")} unpriced (+): right-click on Items to price, scrap, or sell one"
            : "");
        _note = "";
        _status.ToolTip = !_compact && d.Unrecorded > 0 ? Unrecorded : null;

        _filter.Visibility = _view == "Feed" ? Visibility.Visible : Visibility.Collapsed;
        _copyHost.Child = null;
        _body.Children.Clear();
        if (_store.Selected is null)
        {
            _body.Children.Add(Line("No Monsters and Memories ledger found.", Palette.Text));
            _body.Children.Add(Line($"Looked in {_store.LedgerRoot}. Use the ⓘ menu in the title bar to choose where the game keeps it.", Palette.Muted));
            if (Ledger.GameFolderFinder.RunningUnderWine())
                _body.Children.Add(Line("This looks like Wine or Proton: the game keeps its data in its own prefix, not this one, and Companion looked through the usual ones " +
                    "(Steam, UMU, Lutris) when it started. Choose it by hand from ⓘ → Choose the game's data folder…: it is the folder called “Niche Worlds Cult” inside the game's prefix, reached from here as " +
                    @"Z:\home\<you>\…\pfx\drive_c\users\steamuser\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories. " +
                    "In a terminal, find ~/.local/share/Steam/steamapps/compatdata -type d -name \"Niche Worlds Cult\" shows where.", Palette.Muted));
            return;
        }
        if (_store.Selected is { HasLedger: false } fresh && _store.FileCount == 0)
        {
            _body.Children.Add(Line($"No ledger yet for {fresh.Character} on {fresh.ServerName}.", Palette.Text));
            _body.Children.Add(Line("The game writes one as this character loots, sells, levels up and so on; this window picks it up by itself.", Palette.Muted));
            return;
        }
        if (_compact)
        {
            ShowCompact(d);
            return;
        }
        if (d.Events.Count == 0)
        {
            var earlier = _store.Events(day);
            _body.Children.Add(Line(session is { Open: true } ? "Nothing looted yet this session." : day == Today && session is null ? "Nothing looted yet today." : "Nothing looted then.", Palette.Muted));
            if (session is not null && earlier.Count > 0)
            {
                // A new session starts from nothing, which looks like the loot is gone: say where it is
                _body.Children.Add(Line($"Earlier {(day == Today ? "today" : "that day")} there were {Plural(LootSummary.Totals(earlier).Mobs, "mob")} looted. They are in an earlier session.", Palette.Muted));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
                actions.Children.Add(TextAction("Show the whole day", () => _session.ShowWholeDay()));
                if (_store.Sessions.Marks.Count > 0) actions.Children.Add(TextAction("Undo new session", () => _session.UndoSession()));
                _body.Children.Add(actions);
            }
            return;
        }
        switch (_view)
        {
            case "Items": ShowItems(d); break;
            case "Mobs": ShowMobs(d); break;
            case "Feed": ShowFeed(d); break;
            default: ShowValue(d); break;
        }
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";

    private static Brush BrushOf(LootMember m) => Palette.For(m.Name, m.Class.Length > 0 ? m.Class : null);

    private void ShowValue(LootDay d)
    {
        _copyHost.Child = CopyBars(
            ("Copy loot counts", "each looter's name and number of loots on one line", () => LootSummary.LootCounts(d.Members)),
            ("Copy summary", "the summary", () => LootSummary.Breakdown(Day, Events(), values: _store.Values)));
        // Only what each person looted: of other players the ledger never shows what an autosplit gave them, so their coin can't be compared with ours
        // (ours is in the OURS figure at the top)
        bool valueless = d.Members.All(m => m.ItemValue == 0);
        double top = Math.Max(1, valueless ? d.Members.Max(m => m.Mobs) : d.Members.Max(m => (double)m.ItemValue));
        int rank = 0;
        foreach (var m in d.Members)
        {
            var member = m;
            var tip = string.Join(Environment.NewLine, new[]
            {
                m.ClassLevel.Length > 0 ? m.ClassLevel : "", $"{Plural(m.Mobs, "mob")}, {LootSummary.ItemsOf(m)}",
                m.Unrecorded > 0 ? $"{Plural(m.Unrecorded, "corpse")} of theirs have no items in the ledger (the game can't record monsters with an apostrophe in their name)" : "",
                $"items worth {LootSummary.Worth(m.ItemValue, m.Estimated > 0, false)} to a vendor",
                m.Scrapped > 0 ? $"{Plural(m.Scrapped, "item")} scrapped into materials" : "",
                m.PlainUnpriced > 0 ? $"{Plural(m.PlainUnpriced, "item")} with no known price are not counted" : "",
                m.FamilyEstimated > 0 ? $"{Plural(m.FamilyEstimated, "item")} valued by their family (~)" : "",
                m.Mobs > 0 ? $"{m.ItemsPerMob:0.0} items a mob" : "",
                "Click for their items and mobs",
            }.Where(s => s.Length > 0));
            _body.Children.Add(Row(++rank, m.Name, $"{m.Mobs} mobs · {LootSummary.ItemsOf(m)}", LootSummary.ItemsWorthOf(m), BrushOf(m),
                (valueless ? m.Mobs : m.ItemValue) / top, tip, () => _openBreakdown(Day, member.Name, ActiveSession())));
        }
    }

    /// <summary>The compact page's rows: the three items worth most that anyone in the group looted (the filter doesn't apply).</summary>
    private void ShowCompact(LootDay d)
    {
        if (d.Events.Count == 0) { _body.Children.Add(Line("Nothing looted yet in this part of the day.", Palette.Muted)); return; }
        if (d.ItemsWorth.Count == 0) { _body.Children.Add(Line("Coin only so far: no items looted yet.", Palette.Muted)); return; }
        double top = Math.Max(1, d.ItemsWorth.Max(i => (double)i.Total));
        int rank = 0;
        foreach (var i in d.ItemsWorth.Take(3))
            _body.Children.Add(ItemRow(++rank, i, top));
    }

    private void ShowItems(LootDay d)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        int customCount = _store.CustomPrices.Count;
        int scrapCount = _store.ScrapItems.Count;
        string manageLabel = (customCount, scrapCount) switch
        {
            ( > 0, > 0) => $"Custom ({customCount:N0}) · Scrap ({scrapCount:N0})",
            (0, > 0) => $"Scrapped ({scrapCount:N0})",
            ( > 0, 0) => $"Custom ({customCount:N0})",
            _ => "Custom / scrap",
        };
        var manage = new Button
        {
            Content = manageLabel,
            Style = (Style)Ui.Theme["TextButton"],
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "View, add or change custom item prices and scrapped items (or right-click any item below)",
        };
        manage.Click += (_, _) => CustomPricesWindow.Open(Window.GetWindow(this), _store, scrapTab: scrapCount > 0 && customCount == 0);
        actions.Children.Add(manage);
        if (d.ItemsWorth.Count > 0)
        {
            var copy = (Button)CopyBar("Copy items", "the items", () => LootSummary.ItemsText(Day, d.ItemsWorth, _session.ScopeLabel(), _store.Values));
            copy.Margin = new Thickness(4, 0, 0, 0);
            actions.Children.Add(copy);
        }
        _copyHost.Child = actions;

        if (d.ItemsWorth.Count == 0) { _body.Children.Add(Line("No items looted.", Palette.Muted)); return; }
        if (d.ScrapMaterials.Count > 0 && d.ItemsWorth.Count > 10)
        {
            var summary = string.Join(", ", d.ScrapMaterials.Take(3).Select(m => m.Count > 1 ? $"{m.Material} ×{m.Count}" : m.Material))
                + (d.ScrapMaterials.Count > 3 ? $" +{d.ScrapMaterials.Count - 3}" : "");
            _body.Children.Add(new TextBlock
            {
                Text = $"Scrapping {Plural(d.Scrapped, "item")} → {summary}",
                Foreground = Palette.Muted,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 2, 4),
            });
        }

        double top = Math.Max(1, d.ItemsWorth.Max(i => (double)i.Total));
        int rank = 0;
        foreach (var i in d.ItemsWorth.Take(60))
            _body.Children.Add(ItemRow(++rank, i, top));
        if (d.ItemsWorth.Count > 60) _body.Children.Add(Line($"{d.ItemsWorth.Count - 60} more kinds are in the full breakdown.", Palette.Muted));

        if (d.ScrapMaterials.Count > 0)
        {
            _body.Children.Add(new TextBlock
            {
                Text = $"SCRAP YIELD · FROM {Plural(d.Scrapped, "ITEM").ToUpperInvariant()}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Muted,
                Margin = new Thickness(2, 10, 2, 3),
            });
            double scrapTop = Math.Max(1, d.ScrapMaterials.Max(m => (double)m.Total));
            int scrapRank = 0;
            foreach (var m in d.ScrapMaterials)
            {
                var mat = m;
                var fromShort = string.Join(", ", mat.From.Take(2).Select(f => f.ItemCount > 1 ? $"{f.Item} ×{f.ItemCount}" : f.Item))
                    + (mat.From.Count > 2 ? $" +{mat.From.Count - 2}" : "");
                var matWorth = !mat.Value.Known ? "—" : (mat.Value.Estimated ? "~" : "") + LootSummary.Coin(mat.Total);
                var matTip = (mat.Value.Known
                    ? $"{(mat.Value.Estimated ? "about " : "")}{LootSummary.Coin(mat.Value.Each)} each ({SourceWord(mat.Value.Source)})"
                    : "No known price for this material yet (right-click to set a custom price)")
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, mat.From.Select(f => $"from {f.Item}{(f.ItemCount > 1 ? $" ×{f.ItemCount}" : "")} → ×{f.MaterialCount:N0}"));
                _body.Children.Add(Row(++scrapRank, mat.Count > 1 ? $"{mat.Material} ×{mat.Count}" : mat.Material,
                    "from " + fromShort, matWorth, Palette.Gold, mat.Total / scrapTop, matTip,
                    () => _openBreakdown(Day, null, ActiveSession()),
                    copy: () => $"{mat.Material} ×{mat.Count:N0}  ·  {(mat.Value.Known ? (mat.Value.Estimated ? "~" : "") + LootSummary.Coin(mat.Total) : "no known price")}  ·  from {fromShort}",
                    menu: () => PriceMenu.For(this, _store, mat.Material)));
            }
        }
    }

    /// <summary>One item's row on the Items tab (and the compact page): who looted it, what it is worth, and a button that copies its summary.</summary>
    private FrameworkElement ItemRow(int rank, ItemWorth i, double top)
    {
        var takers = string.Join(", ", i.By.Take(2).Select(b => b.Count > 1 ? $"{b.Who} {b.Count}" : b.Who)) + (i.By.Count > 2 ? $" +{i.By.Count - 2}" : "");
        var sub = i.Value.Scrap
            ? (takers.Length > 0 ? $"scrap · {takers}" : "scrap")
            : i.Value.Custom ? (takers.Length > 0 ? $"custom · {takers}" : "custom") : takers;
        var value = !i.Value.Known ? "—" : (i.Value.Estimated ? "~" : "") + LootSummary.Coin(i.Total);
        string priceTip;
        if (i.Value.Scrap)
        {
            var yields = _store.Values.ScrapOf(i.Item);
            var yieldText = yields is { Count: > 0 } ? ScrapItems.DescribeYields(yields) : "materials";
            var natural = _store.Values.NaturalOf(i.Item);
            var natText = natural.Known ? $"; vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}" : "";
            priceTip = i.Value.Known
                ? $"Scraps into {yieldText}: {(i.Value.Estimated ? "about " : "")}{LootSummary.Coin(i.Value.Each)} each{natText}" + Environment.NewLine + "Right-click to change or stop scrapping." + Environment.NewLine
                : $"Scraps into {yieldText} (at least one material has no known price yet{natText})" + Environment.NewLine + "Right-click to change, stop scrapping, or price materials." + Environment.NewLine;
        }
        else if (i.Value.Custom)
        {
            var natural = _store.Values.NaturalOf(i.Item);
            var natText = natural.Known ? $"; vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}" : "";
            priceTip = $"{LootSummary.Coin(i.Value.Each)} each (your custom price{natText})" + Environment.NewLine + "Right-click to change or clear." + Environment.NewLine;
        }
        else if (i.Value.Known)
        {
            priceTip = $"{(i.Value.Estimated ? "about " : "")}{LootSummary.Coin(i.Value.Each)} each to a vendor ({SourceWord(i.Value.Source)})" + Environment.NewLine + "Right-click to set a custom price or scrap." + Environment.NewLine;
        }
        else
        {
            priceTip = "No known price: no vendor sale of it in your ledgers." + Environment.NewLine + "Right-click to set a custom price, scrap, or sell one to a vendor." + Environment.NewLine;
        }
        var tip = priceTip + string.Join(Environment.NewLine, i.By.Select(b => $"{b.Who}: {b.Count}"));
        var item = i;
        return Row(rank, i.Count > 1 ? $"{i.Item} ×{i.Count}" : i.Item, sub, value, Palette.You, i.Total / top, tip, () => _openBreakdown(Day, null, ActiveSession()),
            copy: () => LootSummary.ItemLine(item, _store.Values), menu: () => PriceMenu.For(this, _store, item.Item));
    }

    private static string SourceWord(Loot.ValueSource s) => s switch
    {
        Loot.ValueSource.Custom => "your custom price",
        Loot.ValueSource.Scrap => "from scrap yield",
        Loot.ValueSource.ScrapEstimated => "from estimated scrap yield",
        Loot.ValueSource.ScrapUnpriced => "scrap material unpriced",
        Loot.ValueSource.Sold => "what vendors paid you",
        Loot.ValueSource.Public => "public price list",
        Loot.ValueSource.Charged => "from vendor stock price",
        Loot.ValueSource.Estimated => "from items of the same family",
        _ => "unknown",
    };

    private void ShowMobs(LootDay d)
    {
        if (d.Mobs.Count == 0) { _body.Children.Add(Line("No mobs looted.", Palette.Muted)); return; }
        double top = Math.Max(1, d.Mobs.Max(m => m.Looted));
        int rank = 0;
        foreach (var m in d.Mobs.Take(60))
        {
            var tip = $"{m.ItemsEach:0.0} items each · looted most by {m.TopLooter}";
            _body.Children.Add(Row(++rank, m.Mob, m.Copper > 0 ? $"{LootSummary.Coin(m.Copper / m.Looted)} each" : "", m.Looted.ToString("N0"), Palette.Taken, m.Looted / top,
                tip, () => _openBreakdown(Day, null, ActiveSession())));
        }
        if (d.Mobs.Count > 60) _body.Children.Add(Line($"{d.Mobs.Count - 60} more kinds are in the full breakdown.", Palette.Muted));
    }

    private void ShowFeed(LootDay d)
    {
        var f = _filter.Text.Trim();
        bool Has(string s) => s.Contains(f, StringComparison.OrdinalIgnoreCase);
        var events = d.Events.Where(e => f.Length == 0 || Has(e.Who) || Has(e.Item) || Has(e.Source)).ToList();
        if (events.Count == 0) { _body.Children.Add(Line("Nothing matches the filter.", Palette.Muted)); return; }
        foreach (var e in Enumerable.Reverse(events).Take(FeedRows))
        {
            var row = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 2), ToolTip = Tip(e) };
            row.Inlines.Add(new Run(e.Time.ToString("HH:mm:ss") + "  ") { Foreground = Palette.Muted });
            row.Inlines.Add(new Run(e.Who + "  ") { Foreground = e.You ? Palette.You : Palette.Text, FontWeight = e.You ? FontWeights.SemiBold : FontWeights.Normal });
            if (e.Kind == LootKind.Item)
            {
                row.Inlines.Add(new Run($"[{e.Item}]" + (e.Count > 1 ? $" ×{e.Count}" : "")) { Foreground = Palette.Gold });
                PriceMenu.Attach(row, _store, e.Item);
            }
            else row.Inlines.Add(new Run(LootSummary.Coin(e.Copper)) { Foreground = Palette.Heal });
            row.Inlines.Add(new Run($"  ← {e.Source}") { Foreground = Palette.Muted });
            if (e.Kind == LootKind.Coin && e.Shared) row.Inlines.Add(new Run(e.Split > 0 ? $"  (split · your share {LootSummary.Coin(e.Split)})" : "  (split)") { Foreground = Palette.Muted });
            _body.Children.Add(row);
        }
        if (events.Count > FeedRows) _body.Children.Add(Line($"{events.Count - FeedRows} older lines are in the ledger.", Palette.Muted));
    }

    private static string Tip(LootEvent e) =>
        string.Join("\n", new[]
        {
            LootSummary.Describe(e), e.Zone.Length > 0 ? $"zone: {e.Zone}" : "",
            string.Join(" ", new[] { e.Class.Length > 0 ? GameClasses.NameOf(e.Class) : "", e.Level > 0 ? e.Level.ToString() : "" }.Where(s => s.Length > 0)) is { Length: > 0 } cl ? $"looter: {cl}" : "",
        }.Where(s => s.Length > 0));

    /// <summary>One ranked row in the meter's style: a bar behind it sized to its value, a stripe in the row's colour, rank, name, a muted note and the value.</summary>
    internal static FrameworkElement Row(int rank, string name, string sub, string value, Brush brush, double fraction, string tip, Action click, Func<string>? copy = null, Func<ContextMenu?>? menu = null)
    {
        var grid = new ClickRow { Height = 28, Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand, Activate = click };
        System.Windows.Automation.AutomationProperties.SetName(grid, name);
        var back = new Border { CornerRadius = new CornerRadius(7), Background = (Brush)Ui.Theme["Subtle"] };
        grid.Children.Add(back);
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star) });
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = brush, Opacity = 0.28 });
        grid.Children.Add(fill);
        grid.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = brush, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) });

        var dock = new DockPanel { Margin = new Thickness(11, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(new TextBlock { Text = sub, Foreground = Palette.Muted, FontSize = 11, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(new TextBlock { Text = value, Foreground = Palette.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        dock.Children.Add(right);
        if (copy != null)
        {
            // At the left of the row, before the rank
            var glyph = CopyGlyph("Copy this item's loot summary (who looted it and how many) to the clipboard", copy);
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
        }
        dock.Children.Add(new TextBlock { Text = rank.ToString(), Foreground = Palette.Muted, FontSize = 11, Width = 16, VerticalAlignment = VerticalAlignment.Center });
        dock.Children.Add(new TextBlock { Text = name, Foreground = Palette.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(dock);

        if (tip.Length > 0) grid.ToolTip = new ToolTip { Content = new TextBlock { Text = tip, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }, Style = (Style)Ui.Theme["Tip"] };
        grid.MouseEnter += (_, _) => back.Background = (Brush)Ui.Theme["Hover"];
        grid.MouseLeave += (_, _) => back.Background = (Brush)Ui.Theme["Subtle"];
        grid.MouseLeftButtonUp += (_, _) => click();
        if (menu != null) PriceMenu.Attach(grid, menu);
        return grid;
    }

    /// <summary>A small copy button for a row: copies <paramref name="text"/>() and shows a tick for a moment.</summary>
    private static Button CopyGlyph(string tip, Func<string> text)
    {
        var b = new Button { Content = "", ToolTip = tip, Style = (Style)Ui.Theme["IconButton"], Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            Copy(text());
            b.Content = "";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) => { t.Stop(); b.Content = ""; };
            t.Start();
        };
        return b;
    }

    /// <summary>A "Copy ..." button at the top of a tab: copies the whole tab as text and says so in the status line.</summary>
    private FrameworkElement CopyBar(string label, string what, Func<string> text)
    {
        var b = new Button { Content = label, Style = (Style)Ui.Theme["TextButton"], HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(9, 3, 9, 3), ToolTip = "Copy " + what + " to the clipboard as text" };
        b.Click += (_, _) => { Copy(text()); Report($"Copied {what} to the clipboard."); };
        return b;
    }

    /// <summary>Several <see cref="CopyBar"/> buttons side by side at the right.</summary>
    private FrameworkElement CopyBars(params (string Label, string What, Func<string> Text)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, what, text) in buttons)
        {
            var b = (Button)CopyBar(label, what, text);
            b.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(b);
        }
        return row;
    }

    internal static Button TextAction(string label, Action click)
    {
        var b = new Button { Content = label, Style = (Style)Ui.Theme["TextButton"], Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(11, 6, 11, 6), Focusable = false };
        b.Click += (_, _) => click();
        return b;
    }

    internal static TextBlock Line(string text, Brush brush) =>
        new() { Text = text, Foreground = brush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 2), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };

    // ------------------------------------------------------------------ buttons

    internal static void Copy(string text) => ClipboardText.Copy(text);

    /// <summary>Opens the folder the game keeps this character's ledger files in.</summary>
    public void OpenFolder()
    {
        try
        {
            if (_store.LedgerPath is { } path && Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
}
