using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MnMCompanion.Loot;
using MnMCompanion.Recipes;

namespace MnMCompanion;

/// <summary>Caches the locally stored wiki recipes (<see cref="RecipeCache.DefaultPath"/>) in memory without touching the network.</summary>
internal static class RecipeWikiCache
{
    private static readonly object Gate = new();
    private static DateTime _loadedWrite = DateTime.MinValue;
    private static RecipeBook? _book;

    public static RecipeBook? Book()
    {
        var path = RecipeCache.DefaultPath;
        lock (Gate)
        {
            DateTime stamp;
            try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return _book; }
            if (stamp == _loadedWrite) return _book;
            _loadedWrite = stamp;
            _book = stamp == DateTime.MinValue ? null : RecipeCache.Load(path)?.Book;
            return _book;
        }
    }
}

/// <summary>Right-click context menu for assigning custom prices or marking items to scrap instead of sell across Companion.</summary>
internal static class PriceMenu
{
    /// <summary>Attaches a dynamic right-click menu built by <paramref name="menu"/> to <paramref name="element"/>.</summary>
    public static void Attach(FrameworkElement element, Func<ContextMenu?> menu)
    {
        // A placeholder keeps WPF's ContextMenuOpening active on the element; replaced with the fresh menu on open.
        element.ContextMenu = new ContextMenu();
        element.ContextMenuOpening += (_, e) =>
        {
            if (menu() is { Items.Count: > 0 } cm) element.ContextMenu = cm;
            else e.Handled = true;
        };
    }

    public static void Attach(FrameworkElement element, LootStore store, string? itemName, Action? onChanged = null)
        => Attach(element, () => For(element, store, itemName, onChanged));

    /// <summary>Opens the item's active override dialog (scrap dialog if set to scrap, otherwise custom price dialog).</summary>
    public static void OpenDefault(Window? owner, LootStore store, string itemName, Action? onChanged = null)
    {
        if (CustomPrices.CleanName(itemName) is not { } clean) return;
        if (store.ScrapItems.TryGet(clean, out _))
            ScrapItemDialog.Open(owner, store, clean, onChanged);
        else
            CustomPriceDialog.Open(owner, store, clean, onChanged);
    }

    public static ContextMenu? For(DependencyObject ownerVisual, LootStore store, string? itemName, Action? onChanged = null)
    {
        if (CustomPrices.CleanName(itemName) is not { } clean) return null;
        var owner = Window.GetWindow(ownerVisual);
        bool hasCustom = store.CustomPrices.TryGet(clean, out var current);
        bool hasScrap = store.ScrapItems.TryGet(clean, out var scrapRule);
        var natural = store.Values.NaturalOf(clean);
        var back = natural.Known
            ? $" (back to {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)})"
            : " (back to unpriced)";

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = clean, IsEnabled = false });
        menu.Items.Add(new Separator());

        var edit = new MenuItem { Header = hasCustom ? $"Change custom price ({LootSummary.Coin(current)})…" : "Set custom price…" };
        edit.Click += (_, _) => CustomPriceDialog.Open(owner, store, clean, onChanged);
        menu.Items.Add(edit);

        if (!hasCustom || current != 0)
        {
            var zero = new MenuItem
            {
                Header = "Set to 0cp (worth nothing)",
                ToolTip = "Counts this item as 0cp so it stops marking totals with +",
            };
            zero.Click += (_, _) =>
            {
                if (store.SetCustomPrice(clean, 0)) onChanged?.Invoke();
            };
            menu.Items.Add(zero);
        }

        if (hasCustom)
        {
            var clear = new MenuItem { Header = "Clear custom price" + back };
            clear.Click += (_, _) =>
            {
                if (store.ClearCustomPrice(clean)) onChanged?.Invoke();
            };
            menu.Items.Add(clear);
        }

        menu.Items.Add(new Separator());
        if (hasScrap)
        {
            var editScrap = new MenuItem { Header = $"Change scrap yield ({ScrapItems.DescribeYields(scrapRule.Yields)})…" };
            editScrap.Click += (_, _) => ScrapItemDialog.Open(owner, store, clean, onChanged);
            menu.Items.Add(editScrap);

            var stopScrap = new MenuItem { Header = "Stop scrapping" + back };
            stopScrap.Click += (_, _) =>
            {
                if (store.ClearScrapItem(clean)) onChanged?.Invoke();
            };
            menu.Items.Add(stopScrap);
        }
        else if (ScrapSuggestions.FromRecipes(RecipeWikiCache.Book(), clean) is { } wiki)
        {
            var quickScrap = new MenuItem
            {
                Header = $"Scrap instead of sell ({ScrapItems.DescribeYields(wiki.Yields)})",
                ToolTip = $"Pre-filled from cached wiki recipes: {wiki.Source}",
            };
            quickScrap.Click += (_, _) =>
            {
                if (store.SetScrapItem(clean, wiki.Yields)) onChanged?.Invoke();
            };
            menu.Items.Add(quickScrap);

            var editScrap = new MenuItem { Header = "Scrap instead of sell (edit yield)…" };
            editScrap.Click += (_, _) => ScrapItemDialog.Open(owner, store, clean, onChanged);
            menu.Items.Add(editScrap);
        }
        else
        {
            var scrap = new MenuItem { Header = "Scrap instead of sell…" };
            scrap.Click += (_, _) => ScrapItemDialog.Open(owner, store, clean, onChanged);
            menu.Items.Add(scrap);
        }

        menu.Items.Add(new Separator());
        int count = store.CustomPrices.Count;
        var manage = new MenuItem { Header = count > 0 ? $"Manage custom prices ({count:N0})…" : "Manage custom prices…" };
        manage.Click += (_, _) => CustomPricesWindow.Open(owner, store, onChanged, scrapTab: false);
        menu.Items.Add(manage);

        int scrapCount = store.ScrapItems.Count;
        var manageScrap = new MenuItem { Header = scrapCount > 0 ? $"Manage scrapped items ({scrapCount:N0})…" : "Manage scrapped items…" };
        manageScrap.Click += (_, _) => CustomPricesWindow.Open(owner, store, onChanged, scrapTab: true);
        menu.Items.Add(manageScrap);
        return menu;
    }

    internal static void PrepareOwner(Window dialog, Window? owner)
    {
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            if (owner.Topmost) dialog.Topmost = true;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}

/// <summary>Small prompt to set, change, or clear a single item's custom price.</summary>
internal sealed class CustomPriceDialog : Window
{
    private static CustomPriceDialog? _open;

    private readonly Window? _owner;
    private readonly LootStore _store;
    private readonly string _item;
    private readonly Action? _onChanged;
    private readonly TextBox _input;
    private readonly TextBlock _preview;

    public static void Open(Window? owner, LootStore store, string itemName, Action? onChanged = null)
    {
        if (CustomPrices.CleanName(itemName) is not { } clean) return;
        _open?.Close();
        var window = new CustomPriceDialog(owner, store, clean, onChanged);
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        _open = window;
        window.Show();
        window.Activate();
    }

    private CustomPriceDialog(Window? owner, LootStore store, string item, Action? onChanged)
    {
        _owner = owner;
        _store = store;
        _item = item;
        _onChanged = onChanged;
        Dialogs.Dress(this, "Custom price", 430);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        bool hasCustom = store.CustomPrices.TryGet(item, out var existing);
        bool hasScrap = store.ScrapItems.TryGet(item, out var existingScrap);
        var natural = store.Values.NaturalOf(item);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title(item));
        stack.Children.Add(Dialogs.Text(DescribeNatural(natural), 6, Palette.Muted, 12));
        if (hasScrap)
            stack.Children.Add(Dialogs.Text($"Currently set to scrap into {ScrapItems.DescribeYields(existingScrap.Yields)} (saving a custom price replaces that).", 4, Palette.Gold, 11.5));
        stack.Children.Add(new TextBlock { Text = "Price for one", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 14, 0, 4) });

        _input = new TextBox
        {
            Style = (Style)Ui.Theme["Field"],
            FontSize = 13.5,
            Padding = new Thickness(8, 6, 8, 6),
            Text = hasCustom ? CustomPrices.FormatInput(existing) : natural.Known ? CustomPrices.FormatInput(natural.Each) : "",
        };
        _preview = new TextBlock
        {
            FontSize = 11.5,
            MinHeight = 18,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _input.TextChanged += (_, _) => UpdatePreview();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };

        stack.Children.Add(_input);
        stack.Children.Add(_preview);
        stack.Children.Add(Dialogs.Text("Examples: 25cp, 4sp 50cp, 1.5gp, 2pp, or 0cp for an item worth nothing.", 4, Palette.Muted, 11));

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (hasCustom)
        {
            var clear = Dialogs.Pill("Clear custom price", 12, tip: "Remove your custom price and revert to vendor/estimated prices", click: Clear);
            DockPanel.SetDock(clear, Dock.Left);
            buttons.Children.Add(clear);
        }
        var scrap = Dialogs.Pill("Scrap instead…", 12, tip: "Value this item by the crafting materials it breaks down into", click: SwitchToScrap);
        if (hasCustom) scrap.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(scrap, Dock.Left);
        buttons.Children.Add(scrap);

        var save = Dialogs.Pill("Save", 16, bold: true, click: Commit);
        var cancel = Dialogs.Pill("Cancel", 14, click: Close);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        Content = stack;
        UpdatePreview();
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    internal static string DescribeNatural(ItemValue natural) => natural.Source switch
    {
        Loot.ValueSource.Sold => $"Vendors paid you a middle price of {LootSummary.Coin(natural.Each)} for one.",
        Loot.ValueSource.Public => $"Public price list: vendors pay {LootSummary.Coin(natural.Each)} for one.",
        Loot.ValueSource.Charged => $"Vendor stock estimate: about {LootSummary.Coin(natural.Each)} for one.",
        Loot.ValueSource.Estimated => $"Family estimate: about {LootSummary.Coin(natural.Each)} for one.",
        _ => "No vendor price in your ledgers yet.",
    };

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_input.Text))
        {
            _preview.Text = "";
            return;
        }
        if (CustomPrices.TryParseCoin(_input.Text, out var copper, out var error))
        {
            _preview.Foreground = Palette.Heal;
            _preview.Text = copper == 0
                ? "Saves as 0cp each (worth nothing; won't mark totals with +)"
                : $"Saves as {LootSummary.Coin(copper)} each ({copper:N0}cp)";
        }
        else
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = error ?? "Type a valid coin amount.";
        }
    }

    private void Commit()
    {
        if (!CustomPrices.TryParseCoin(_input.Text, out var copper, out var error))
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = error ?? "Type a valid coin amount.";
            _input.Focus();
            _input.SelectAll();
            return;
        }
        if (!_store.SetCustomPrice(_item, copper))
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = $"Can't keep more than {CustomPrices.MaxItems:N0} custom prices.";
            return;
        }
        _onChanged?.Invoke();
        Close();
    }

    private void Clear()
    {
        if (_store.ClearCustomPrice(_item)) _onChanged?.Invoke();
        Close();
    }

    private void SwitchToScrap()
    {
        Close();
        ScrapItemDialog.Open(_owner, _store, _item, _onChanged);
    }
}

/// <summary>Small prompt to configure what materials an item scraps into instead of selling to a vendor.</summary>
internal sealed class ScrapItemDialog : Window
{
    private static ScrapItemDialog? _open;

    private readonly Window? _owner;
    private readonly LootStore _store;
    private readonly string _item;
    private readonly ItemValue _natural;
    private readonly Action? _onChanged;
    private readonly TextBox _input;
    private readonly TextBlock _preview;

    public static void Open(Window? owner, LootStore store, string itemName, Action? onChanged = null)
    {
        if (CustomPrices.CleanName(itemName) is not { } clean) return;
        _open?.Close();
        var window = new ScrapItemDialog(owner, store, clean, onChanged);
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        _open = window;
        window.Show();
        window.Activate();
    }

    private ScrapItemDialog(Window? owner, LootStore store, string item, Action? onChanged)
    {
        _owner = owner;
        _store = store;
        _item = item;
        _onChanged = onChanged;
        _natural = store.Values.NaturalOf(item);
        Dialogs.Dress(this, "Scrap item", 460);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        bool hasScrap = store.ScrapItems.TryGet(item, out var existing);
        bool hasCustom = store.CustomPrices.TryGet(item, out var existingCustom);
        var suggestion = !hasScrap ? ScrapSuggestions.FromRecipes(RecipeWikiCache.Book(), item) : null;

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title(item));
        stack.Children.Add(Dialogs.Text(CustomPriceDialog.DescribeNatural(_natural), 6, Palette.Muted, 12));
        if (suggestion is not null)
            stack.Children.Add(Dialogs.Text($"Pre-filled from cached wiki recipes ({suggestion.Source}){(suggestion.RangeAdjusted ? "; range uses minimum count" : "")}. Edit if needed.", 4, Palette.You, 11.5));
        else if (hasCustom)
            stack.Children.Add(Dialogs.Text($"Currently has a custom price of {LootSummary.Coin(existingCustom)} (saving a scrap rule replaces that).", 4, Palette.Gold, 11.5));

        stack.Children.Add(new TextBlock { Text = "Scraps into (per 1 item)", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 14, 0, 4) });

        _input = new TextBox
        {
            Style = (Style)Ui.Theme["Field"],
            FontSize = 13.5,
            Padding = new Thickness(8, 6, 8, 6),
            Text = hasScrap ? ScrapItems.FormatInput(existing.Yields) : suggestion is not null ? ScrapItems.FormatInput(suggestion.Yields) : "",
        };
        _preview = new TextBlock
        {
            FontSize = 11.5,
            MinHeight = 18,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _input.TextChanged += (_, _) => UpdatePreview();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };

        stack.Children.Add(_input);
        stack.Children.Add(_preview);
        stack.Children.Add(Dialogs.Text("Examples: 4 Bronze Scraps, 2x Bronze Scraps + 2x Rawhide Scraps, or Arcane Powder ×4.", 4, Palette.Muted, 11));

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (hasScrap)
        {
            var clear = Dialogs.Pill("Stop scrapping", 12, tip: "Remove this scrap rule and revert to vendor/custom prices", click: Clear);
            DockPanel.SetDock(clear, Dock.Left);
            buttons.Children.Add(clear);
        }
        else
        {
            var custom = Dialogs.Pill("Coin price…", 12, tip: "Set a direct custom coin price instead", click: SwitchToCustom);
            DockPanel.SetDock(custom, Dock.Left);
            buttons.Children.Add(custom);
        }

        var save = Dialogs.Pill("Save", 16, bold: true, click: Commit);
        var cancel = Dialogs.Pill("Cancel", 14, click: Close);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        Content = stack;
        UpdatePreview();
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    internal static (string Text, Brush Color) PreviewYields(ItemValues values, string targetItem, IReadOnlyList<ScrapYield> yields, ItemValue natural)
    {
        decimal total = 0m;
        bool anyEstimated = false;
        var missing = new List<string>();
        foreach (var y in yields)
        {
            var mv = y.Item.Equals(targetItem, StringComparison.OrdinalIgnoreCase) ? default : values.Of(y.Item);
            if (!mv.Known) missing.Add(y.Item);
            else { total += (decimal)mv.Each * y.Qty; anyEstimated |= mv.Estimated; }
        }
        var vendorCmp = natural.Known ? $" (vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)})" : "";
        if (missing.Count == 0)
        {
            long copper = (long)Math.Min(total, CustomPrices.MaxCopper);
            return ($"Saves as {ScrapItems.DescribeYields(yields)} = {(anyEstimated ? "~" : "")}{LootSummary.Coin(copper)} each{vendorCmp}", Palette.Heal);
        }
        return ($"Saves as {ScrapItems.DescribeYields(yields)} (unpriced: {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? "…" : "")}){vendorCmp}", Palette.Gold);
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_input.Text))
        {
            _preview.Text = "";
            return;
        }
        if (ScrapItems.TryParseYields(_input.Text, _item, out var yields, out var error))
        {
            var (text, brush) = PreviewYields(_store.Values, _item, yields, _natural);
            _preview.Foreground = brush;
            _preview.Text = text;
        }
        else
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = error ?? "Enter scrap yields such as 4 Bronze Scraps.";
        }
    }

    private void Commit()
    {
        if (!ScrapItems.TryParseYields(_input.Text, _item, out var yields, out var error))
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = error ?? "Enter scrap yields such as 4 Bronze Scraps.";
            _input.Focus();
            _input.SelectAll();
            return;
        }
        if (!_store.SetScrapItem(_item, yields))
        {
            _preview.Foreground = Palette.Taken;
            _preview.Text = $"Can't keep more than {ScrapItems.MaxItems:N0} scrapped items.";
            return;
        }
        _onChanged?.Invoke();
        Close();
    }

    private void Clear()
    {
        if (_store.ClearScrapItem(_item)) _onChanged?.Invoke();
        Close();
    }

    private void SwitchToCustom()
    {
        Close();
        CustomPriceDialog.Open(_owner, _store, _item, _onChanged);
    }
}

/// <summary>Manager window listing custom loot prices and scrapped items with search, add/edit, remove, and clear all.</summary>
internal sealed class CustomPricesWindow : Window
{
    private static CustomPricesWindow? _open;

    private readonly LootStore _store;
    private readonly Action? _onChanged;
    private readonly TextBlock _description;
    private readonly TextBox _itemBox;
    private readonly TextBox _valueBox;
    private readonly TextBlock _valuePlaceholder;
    private readonly ColumnDefinition _valueCol;
    private readonly Button _useWikiBtn;
    private readonly TextBlock _addStatus;
    private readonly Border _searchHost;
    private readonly TextBox _searchBox;
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new() { FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clearAll;
    private readonly DispatcherTimer _confirmReset;
    private bool _scrapTab;
    private bool _confirmingClear;
    private bool _closing;
    private string? _wikiYieldInput;

    public static void Open(Window? owner, LootStore store, Action? onChanged = null, bool scrapTab = false)
    {
        _open?.Close();
        var window = new CustomPricesWindow(owner, store, onChanged, scrapTab);
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        _open = window;
        window.Show();
        window.Activate();
    }

    private CustomPricesWindow(Window? owner, LootStore store, Action? onChanged, bool scrapTab)
    {
        _store = store;
        _onChanged = onChanged;
        _scrapTab = scrapTab;
        Dialogs.Dress(this, "Custom prices & scrapped items", 580);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title("Custom prices & scrapped items"));

        var segBar = new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Ui.Theme["Subtle"], Padding = new Thickness(2), Margin = new Thickness(0, 10, 0, 0) };
        var segGrid = new UniformGrid { Columns = 2 };
        var customRb = new RadioButton { Content = new TextBlock { Text = "Custom prices" }, Style = (Style)Ui.Theme["Segment"], GroupName = "ManageOverrideTab", IsChecked = !scrapTab, Focusable = false };
        var scrapRb = new RadioButton { Content = new TextBlock { Text = "Scrapped items" }, Style = (Style)Ui.Theme["Segment"], GroupName = "ManageOverrideTab", IsChecked = scrapTab, Focusable = false };
        customRb.Checked += (_, _) => SwitchTab(scrap: false);
        scrapRb.Checked += (_, _) => SwitchTab(scrap: true);
        segGrid.Children.Add(customRb);
        segGrid.Children.Add(scrapRb);
        segBar.Child = segGrid;
        stack.Children.Add(segBar);

        _description = Dialogs.Text("", 8, Palette.Muted, 11.5);
        stack.Children.Add(_description);

        stack.Children.Add(new TextBlock { Text = "Add or update an item", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 12, 0, 4) });
        _itemBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 12, MaxLength = CustomPrices.MaxName };
        _valueBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 12 };
        var addButton = new Button { Content = "Save", Style = (Style)Ui.Theme["TextButton"], Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Save" };

        var addRow = new Grid();
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _valueCol = new ColumnDefinition { Width = new GridLength(scrapTab ? 210 : 136) };
        addRow.ColumnDefinitions.Add(_valueCol);
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var itemWrap = WithPlaceholder(_itemBox, "Item name", out _);
        var priceWrap = WithPlaceholder(_valueBox, scrapTab ? "e.g. 4 Bronze Scraps" : "e.g. 2gp 50sp", out _valuePlaceholder);
        priceWrap.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(itemWrap, 0);
        Grid.SetColumn(priceWrap, 1);
        Grid.SetColumn(addButton, 2);
        addRow.Children.Add(itemWrap);
        addRow.Children.Add(priceWrap);
        addRow.Children.Add(addButton);
        stack.Children.Add(addRow);

        _useWikiBtn = new Button
        {
            Style = (Style)Ui.Theme["TextButton"],
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        _useWikiBtn.Click += (_, _) =>
        {
            if (_wikiYieldInput is null) return;
            _valueBox.Text = _wikiYieldInput;
            _valueBox.Focus();
            _valueBox.CaretIndex = _valueBox.Text.Length;
            UpdateWikiHint();
        };
        stack.Children.Add(_useWikiBtn);

        _addStatus = new TextBlock { FontSize = 11, Foreground = Palette.Muted, MinHeight = 16, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_addStatus);

        addButton.Click += (_, _) => AddOrUpdate();
        _itemBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _valueBox.Focus(); } };
        _itemBox.TextChanged += (_, _) => UpdateWikiHint();
        _valueBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddOrUpdate(); } };
        _valueBox.TextChanged += (_, _) => { UpdateAddPreview(); UpdateWikiHint(); };

        _searchBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 11.5, ToolTip = "Filter items by name (Esc clears)" };
        var searchWrap = WithPlaceholder(_searchBox, "Filter items", out _);
        _searchHost = new Border { Margin = new Thickness(0, 8, 0, 6), Child = searchWrap, Visibility = Visibility.Collapsed };
        _searchBox.TextChanged += (_, _) => RebuildList();
        _searchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _searchBox.Text.Length == 0) return;
            _searchBox.Clear();
            e.Handled = true;
        };
        stack.Children.Add(_searchHost);

        var listFrame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Ui.Theme["Stroke"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new ScrollViewer
            {
                MaxHeight = 290,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _list,
            },
        };
        stack.Children.Add(listFrame);

        _confirmReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmReset.Tick += (_, _) => ResetClearConfirm();
        _clearAll = Dialogs.Pill("Clear all", 12, click: OnClearAll);
        _clearAll.Margin = new Thickness(12, 0, 0, 0);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_count);
        left.Children.Add(_clearAll);
        DockPanel.SetDock(left, Dock.Left);
        var close = Dialogs.Pill("Close", 16, click: Close);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(left);
        footer.Children.Add(close);
        stack.Children.Add(footer);

        Content = stack;
        ApplyTabLabels();
        RebuildList();

        _store.CustomPrices.Changed += OnOverridesChanged;
        _store.ScrapItems.Changed += OnOverridesChanged;
        Closed += (_, _) =>
        {
            _closing = true;
            _confirmReset.Stop();
            _store.CustomPrices.Changed -= OnOverridesChanged;
            _store.ScrapItems.Changed -= OnOverridesChanged;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _searchBox.IsKeyboardFocusWithin) return;
            e.Handled = true;
            Close();
        };
    }

    private static Grid WithPlaceholder(TextBox box, string hint, out TextBlock placeholder)
    {
        var label = new TextBlock
        {
            Text = hint,
            Foreground = Palette.Muted,
            FontSize = box.FontSize,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        placeholder = label;
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(label);
        return g;
    }

    private void SwitchTab(bool scrap)
    {
        if (_scrapTab == scrap) return;
        _scrapTab = scrap;
        ResetClearConfirm();
        _valueBox.Clear();
        _addStatus.Text = "";
        ApplyTabLabels();
        UpdateWikiHint();
        RebuildList();
    }

    private void ApplyTabLabels()
    {
        if (_scrapTab)
        {
            _description.Text = "Items here are valued by the materials they break down into instead of vendor sale prices, and tally up under Scrap yield. Saved only on this PC.";
            _valueCol.Width = new GridLength(210);
            _valuePlaceholder.Text = "e.g. 4 Bronze Scraps";
        }
        else
        {
            _description.Text = "Prices you set here override vendor and estimated prices everywhere in Companion. Use 0cp for quest or no-drop items worth nothing. Saved only on this PC.";
            _valueCol.Width = new GridLength(136);
            _valuePlaceholder.Text = "e.g. 2gp 50sp";
        }
    }

    private void OnOverridesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        _store.SyncOverrides();
        RebuildList();
    });

    private void UpdateWikiHint()
    {
        _wikiYieldInput = null;
        _useWikiBtn.Visibility = Visibility.Collapsed;
        if (!_scrapTab) return;
        if (CustomPrices.CleanName(_itemBox.Text) is not { } clean) return;
        if (_store.ScrapItems.TryGet(clean, out _)) return;
        if (ScrapSuggestions.FromRecipes(RecipeWikiCache.Book(), clean) is not { } wiki) return;
        var formatted = ScrapItems.FormatInput(wiki.Yields);
        if (string.Equals(_valueBox.Text.Trim(), formatted, StringComparison.OrdinalIgnoreCase)) return;
        _wikiYieldInput = formatted;
        _useWikiBtn.Content = $"Use wiki yield: {ScrapItems.DescribeYields(wiki.Yields)}";
        _useWikiBtn.ToolTip = $"Pre-fill from cached wiki recipes ({wiki.Source})";
        _useWikiBtn.Visibility = Visibility.Visible;
    }

    private void UpdateAddPreview()
    {
        if (string.IsNullOrWhiteSpace(_valueBox.Text))
        {
            _addStatus.Text = "";
            return;
        }
        if (_scrapTab)
        {
            var item = CustomPrices.CleanName(_itemBox.Text) ?? "";
            if (ScrapItems.TryParseYields(_valueBox.Text, item.Length > 0 ? item : null, out var yields, out var error))
            {
                var (text, brush) = ScrapItemDialog.PreviewYields(_store.Values, item, yields, item.Length > 0 ? _store.Values.NaturalOf(item) : default);
                _addStatus.Foreground = brush;
                _addStatus.Text = text;
            }
            else
            {
                _addStatus.Foreground = Palette.Taken;
                _addStatus.Text = error ?? "";
            }
            return;
        }
        if (CustomPrices.TryParseCoin(_valueBox.Text, out var copper, out var coinError))
        {
            _addStatus.Foreground = Palette.Heal;
            _addStatus.Text = copper == 0 ? "0cp (worth nothing)" : $"{LootSummary.Coin(copper)} ({copper:N0}cp)";
        }
        else
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = coinError ?? "";
        }
    }

    private void AddOrUpdate()
    {
        if (CustomPrices.CleanName(_itemBox.Text) is not { } clean)
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = "Type an item name first.";
            _itemBox.Focus();
            return;
        }

        if (_scrapTab)
        {
            if (!ScrapItems.TryParseYields(_valueBox.Text, clean, out var yields, out var scrapError))
            {
                _addStatus.Foreground = Palette.Taken;
                _addStatus.Text = scrapError ?? "Enter scrap yields such as 4 Bronze Scraps.";
                _valueBox.Focus();
                _valueBox.SelectAll();
                return;
            }
            if (!_store.SetScrapItem(clean, yields))
            {
                _addStatus.Foreground = Palette.Taken;
                _addStatus.Text = $"Can't keep more than {ScrapItems.MaxItems:N0} scrapped items.";
                return;
            }
            ResetClearConfirm();
            _itemBox.Clear();
            _valueBox.Clear();
            _addStatus.Foreground = Palette.Muted;
            _addStatus.Text = $"Saved {clean} → {ScrapItems.DescribeYields(yields)}.";
            _itemBox.Focus();
            _onChanged?.Invoke();
            return;
        }

        if (!CustomPrices.TryParseCoin(_valueBox.Text, out var copper, out var error))
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = error ?? "Type a valid coin amount.";
            _valueBox.Focus();
            _valueBox.SelectAll();
            return;
        }
        if (!_store.SetCustomPrice(clean, copper))
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = $"Can't keep more than {CustomPrices.MaxItems:N0} custom prices.";
            return;
        }
        ResetClearConfirm();
        _itemBox.Clear();
        _valueBox.Clear();
        _addStatus.Foreground = Palette.Muted;
        _addStatus.Text = $"Saved {clean} at {LootSummary.Coin(copper)}.";
        _itemBox.Focus();
        _onChanged?.Invoke();
    }

    private void RebuildList()
    {
        if (_scrapTab)
        {
            RebuildScrapList();
            return;
        }

        var all = _store.CustomPrices.All;
        _searchHost.Visibility = all.Count > 4 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count <= 4 && _searchBox.Text.Length > 0) _searchBox.Clear();

        _count.Text = all.Count == 1 ? "1 custom price" : $"{all.Count:N0} custom prices";
        _clearAll.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count == 0) ResetClearConfirm();

        _list.Children.Clear();
        if (all.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No custom prices set yet. Right-click any item in Loot, History, Vendors or Harvest, or add one above.",
                Foreground = Palette.Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 12, 10, 12),
            });
            return;
        }

        var query = _searchBox.Text.Trim();
        var shown = query.Length == 0 ? all : NameSearch.Filter(all, p => p.Item, query);
        if (shown.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = $"Nothing matches “{query}”.", Foreground = Palette.Muted, FontSize = 11.5, Margin = new Thickness(10, 12, 10, 12),
            });
            return;
        }

        foreach (var entry in shown)
        {
            var p = entry;
            var natural = _store.Values.NaturalOf(p.Item);
            var row = MakeRow(
                p.Item,
                natural.Known ? $"vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}" : null,
                LootSummary.Coin(p.Copper),
                p.Copper == 0 ? Palette.Muted : Palette.Gold,
                natural.Known
                    ? $"{p.Item}: custom {LootSummary.Coin(p.Copper)} (vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}). Click to change."
                    : $"{p.Item}: custom {LootSummary.Coin(p.Copper)}. Click to change.",
                $"Clear custom price for {p.Item}",
                () => CustomPriceDialog.Open(this, _store, p.Item, _onChanged),
                () => { if (_store.ClearCustomPrice(p.Item)) _onChanged?.Invoke(); });
            _list.Children.Add(row);
        }
    }

    private void RebuildScrapList()
    {
        var all = _store.ScrapItems.All;
        _searchHost.Visibility = all.Count > 4 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count <= 4 && _searchBox.Text.Length > 0) _searchBox.Clear();

        _count.Text = all.Count == 1 ? "1 scrapped item" : $"{all.Count:N0} scrapped items";
        _clearAll.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count == 0) ResetClearConfirm();

        _list.Children.Clear();
        if (all.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No items set to scrap yet. Right-click any item and choose Scrap instead of sell, or add one above.",
                Foreground = Palette.Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 12, 10, 12),
            });
            return;
        }

        var query = _searchBox.Text.Trim();
        var shown = query.Length == 0 ? all : NameSearch.Filter(all, r => r.Item + " " + ScrapItems.DescribeYields(r.Yields), query);
        if (shown.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = $"Nothing matches “{query}”.", Foreground = Palette.Muted, FontSize = 11.5, Margin = new Thickness(10, 12, 10, 12),
            });
            return;
        }

        foreach (var entry in shown)
        {
            var r = entry;
            var natural = _store.Values.NaturalOf(r.Item);
            var val = _store.Values.Of(r.Item);
            var yieldsText = ScrapItems.DescribeYields(r.Yields);
            var sub = "→ " + yieldsText + (natural.Known ? $" · vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}" : "");
            var worth = !val.Known ? "unpriced" : (val.Estimated ? "~" : "") + LootSummary.Coin(val.Each);
            var tip = $"{r.Item}: scraps into {yieldsText}"
                + (val.Known ? $" ({(val.Estimated ? "~" : "")}{LootSummary.Coin(val.Each)})" : " (material unpriced)")
                + (natural.Known ? $"; vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}" : "")
                + ". Click to change.";
            var row = MakeRow(
                r.Item,
                sub,
                worth,
                !val.Known ? Palette.Muted : Palette.Gold,
                tip,
                $"Stop scrapping {r.Item}",
                () => ScrapItemDialog.Open(this, _store, r.Item, _onChanged),
                () => { if (_store.ClearScrapItem(r.Item)) _onChanged?.Invoke(); });
            _list.Children.Add(row);
        }
    }

    private Border MakeRow(string title, string? sub, string rightText, Brush rightBrush, string tip, string removeTip, Action onClick, Action onRemove)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 6, 6, 6),
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        row.MouseEnter += (_, _) => row.Background = (Brush)Ui.Theme["Subtle"];
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => onClick();

        var dock = new DockPanel();
        var remove = new Button
        {
            Content = "",
            Style = (Style)Ui.Theme["IconButton"],
            Width = 24, Height = 24, FontSize = 10.5,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = removeTip,
        };
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            ResetClearConfirm();
            onRemove();
        };
        DockPanel.SetDock(remove, Dock.Right);
        dock.Children.Add(remove);

        var price = new TextBlock
        {
            Text = rightText,
            Foreground = rightBrush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(price, Dock.Right);
        dock.Children.Add(price);

        var leftPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        leftPanel.Children.Add(new TextBlock
        {
            Text = title, Foreground = Palette.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (sub is { Length: > 0 })
            leftPanel.Children.Add(new TextBlock
            {
                Text = sub, Foreground = Palette.Muted, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
        dock.Children.Add(leftPanel);

        row.Child = dock;
        return row;
    }

    private void OnClearAll()
    {
        if (!_confirmingClear)
        {
            _confirmingClear = true;
            _clearAll.Content = "Confirm clear all?";
            _clearAll.Foreground = Palette.Taken;
            _confirmReset.Stop();
            _confirmReset.Start();
            return;
        }
        ResetClearConfirm();
        int cleared = _scrapTab ? _store.ScrapItems.ClearAll() : _store.CustomPrices.ClearAll();
        if (cleared > 0)
        {
            _store.SyncOverrides();
            _onChanged?.Invoke();
        }
    }

    private void ResetClearConfirm()
    {
        _confirmReset.Stop();
        _confirmingClear = false;
        _clearAll.Content = "Clear all";
        _clearAll.Foreground = Palette.Text;
    }
}
