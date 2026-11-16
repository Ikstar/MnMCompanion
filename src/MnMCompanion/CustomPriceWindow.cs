using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MnMCompanion.Loot;

namespace MnMCompanion;

/// <summary>Right-click context menu for assigning, zeroing, or clearing a custom price on any item across Companion.</summary>
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

    public static ContextMenu? For(DependencyObject ownerVisual, LootStore store, string? itemName, Action? onChanged = null)
    {
        if (CustomPrices.CleanName(itemName) is not { } clean) return null;
        var owner = Window.GetWindow(ownerVisual);
        bool hasCustom = store.CustomPrices.TryGet(clean, out var current);
        var natural = store.Values.NaturalOf(clean);

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
            var back = natural.Known
                ? $" (back to {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)})"
                : " (back to unpriced)";
            var clear = new MenuItem { Header = "Clear custom price" + back };
            clear.Click += (_, _) =>
            {
                if (store.ClearCustomPrice(clean)) onChanged?.Invoke();
            };
            menu.Items.Add(clear);
        }

        menu.Items.Add(new Separator());
        int count = store.CustomPrices.Count;
        var manage = new MenuItem { Header = count > 0 ? $"Manage custom prices ({count:N0})…" : "Manage custom prices…" };
        manage.Click += (_, _) => CustomPricesWindow.Open(owner, store, onChanged);
        menu.Items.Add(manage);
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
        _store = store;
        _item = item;
        _onChanged = onChanged;
        Dialogs.Dress(this, "Custom price", 430);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        bool hasCustom = store.CustomPrices.TryGet(item, out var existing);
        var natural = store.Values.NaturalOf(item);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title(item));
        stack.Children.Add(Dialogs.Text(DescribeNatural(natural), 6, Palette.Muted, 12));
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

    private static string DescribeNatural(ItemValue natural) => natural.Source switch
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
}

/// <summary>Manager window listing all custom loot prices with search, add/edit, remove, and clear all.</summary>
internal sealed class CustomPricesWindow : Window
{
    private static CustomPricesWindow? _open;

    private readonly LootStore _store;
    private readonly Action? _onChanged;
    private readonly TextBox _itemBox;
    private readonly TextBox _priceBox;
    private readonly TextBlock _addStatus;
    private readonly Border _searchHost;
    private readonly TextBox _searchBox;
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new() { FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clearAll;
    private readonly DispatcherTimer _confirmReset;
    private bool _confirmingClear;
    private bool _closing;

    public static void Open(Window? owner, LootStore store, Action? onChanged = null)
    {
        _open?.Close();
        var window = new CustomPricesWindow(owner, store, onChanged);
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        _open = window;
        window.Show();
        window.Activate();
    }

    private CustomPricesWindow(Window? owner, LootStore store, Action? onChanged)
    {
        _store = store;
        _onChanged = onChanged;
        Dialogs.Dress(this, "Custom loot prices", 560);
        Resources.MergedDictionaries.Add(Ui.Menus);
        PriceMenu.PrepareOwner(this, owner);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Dialogs.Title("Custom loot prices"));
        stack.Children.Add(Dialogs.Text("Prices you set here override vendor and estimated prices everywhere in Companion. Use 0cp for quest or no-drop items worth nothing. Saved only on this PC.", 6, Palette.Muted, 11.5));

        stack.Children.Add(new TextBlock { Text = "Add or update an item", Style = (Style)Ui.Theme["Label"], Margin = new Thickness(0, 14, 0, 4) });
        _itemBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 12, MaxLength = CustomPrices.MaxName };
        _priceBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 12 };
        var addButton = new Button { Content = "Save", Style = (Style)Ui.Theme["TextButton"], Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Save this custom price" };

        var addRow = new Grid();
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var itemWrap = WithPlaceholder(_itemBox, "Item name");
        var priceWrap = WithPlaceholder(_priceBox, "e.g. 2gp 50sp");
        priceWrap.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(itemWrap, 0);
        Grid.SetColumn(priceWrap, 1);
        Grid.SetColumn(addButton, 2);
        addRow.Children.Add(itemWrap);
        addRow.Children.Add(priceWrap);
        addRow.Children.Add(addButton);
        stack.Children.Add(addRow);

        _addStatus = new TextBlock { FontSize = 11, Foreground = Palette.Muted, MinHeight = 16, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_addStatus);

        addButton.Click += (_, _) => AddOrUpdate();
        _itemBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _priceBox.Focus(); } };
        _priceBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddOrUpdate(); } };
        _priceBox.TextChanged += (_, _) => UpdateAddPreview();

        _searchBox = new TextBox { Style = (Style)Ui.Theme["Field"], FontSize = 11.5, ToolTip = "Filter custom prices by item name (Esc clears)" };
        var searchWrap = WithPlaceholder(_searchBox, "Filter items");
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
        RebuildList();

        _store.CustomPrices.Changed += OnPricesChanged;
        Closed += (_, _) =>
        {
            _closing = true;
            _confirmReset.Stop();
            _store.CustomPrices.Changed -= OnPricesChanged;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _searchBox.IsKeyboardFocusWithin) return;
            e.Handled = true;
            Close();
        };
    }

    private static Grid WithPlaceholder(TextBox box, string hint)
    {
        var placeholder = new TextBlock
        {
            Text = hint,
            Foreground = Palette.Muted,
            FontSize = box.FontSize,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(placeholder);
        return g;
    }

    private void OnPricesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        _store.SyncCustom();
        RebuildList();
    });

    private void UpdateAddPreview()
    {
        if (string.IsNullOrWhiteSpace(_priceBox.Text))
        {
            _addStatus.Text = "";
            return;
        }
        if (CustomPrices.TryParseCoin(_priceBox.Text, out var copper, out var error))
        {
            _addStatus.Foreground = Palette.Heal;
            _addStatus.Text = copper == 0 ? "0cp (worth nothing)" : $"{LootSummary.Coin(copper)} ({copper:N0}cp)";
        }
        else
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = error ?? "";
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
        if (!CustomPrices.TryParseCoin(_priceBox.Text, out var copper, out var error))
        {
            _addStatus.Foreground = Palette.Taken;
            _addStatus.Text = error ?? "Type a valid coin amount.";
            _priceBox.Focus();
            _priceBox.SelectAll();
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
        _priceBox.Clear();
        _addStatus.Foreground = Palette.Muted;
        _addStatus.Text = $"Saved {clean} at {LootSummary.Coin(copper)}.";
        _itemBox.Focus();
        _onChanged?.Invoke();
    }

    private void RebuildList()
    {
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
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 6, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = natural.Known
                    ? $"{p.Item}: custom {LootSummary.Coin(p.Copper)} (vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}). Click to change."
                    : $"{p.Item}: custom {LootSummary.Coin(p.Copper)}. Click to change.",
            };
            row.MouseEnter += (_, _) => row.Background = (Brush)Ui.Theme["Subtle"];
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => CustomPriceDialog.Open(this, _store, p.Item, _onChanged);

            var dock = new DockPanel();
            var remove = new Button
            {
                Content = "",
                Style = (Style)Ui.Theme["IconButton"],
                Width = 24, Height = 24, FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Clear custom price for {p.Item}",
            };
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                ResetClearConfirm();
                if (_store.ClearCustomPrice(p.Item)) _onChanged?.Invoke();
            };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);

            var price = new TextBlock
            {
                Text = LootSummary.Coin(p.Copper),
                Foreground = p.Copper == 0 ? Palette.Muted : Palette.Gold,
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
                Text = p.Item, Foreground = Palette.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (natural.Known)
                leftPanel.Children.Add(new TextBlock
                {
                    Text = $"vendor {(natural.Estimated ? "~" : "")}{LootSummary.Coin(natural.Each)}",
                    Foreground = Palette.Muted, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0),
                });
            dock.Children.Add(leftPanel);

            row.Child = dock;
            _list.Children.Add(row);
        }
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
        if (_store.CustomPrices.ClearAll() > 0)
        {
            _store.SyncCustom();
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
