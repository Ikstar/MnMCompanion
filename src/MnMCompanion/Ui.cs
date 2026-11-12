using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MnMCompanion.Loot;

namespace MnMCompanion;

/// <summary>The shared styles and brushes (Theme.xaml and Menus.xaml), merged into each window.</summary>
public static class Ui
{
    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const uint IMAGE_ICON = 1;
    private const uint LR_SHARED = 0x8000;
    private const int IDI_APPLICATION = 32512;

    private static ResourceDictionary? _theme;
    private static ResourceDictionary? _menus;
    private static ImageSource? _appIcon;
    private static bool _appIconLoaded;
    private static IntPtr _smallHIcon;
    private static IntPtr _bigHIcon;
    private static bool _nativeIconsLoaded;

    /// <summary>The program's own name as the resource addresses spell it (it is the file name, so it follows a rename).</summary>
    private static readonly string AssemblyName = typeof(Ui).Assembly.GetName().Name!;

    public static ResourceDictionary Theme => _theme ??= new ResourceDictionary
    {
        Source = new Uri($"pack://application:,,,/{AssemblyName};component/Theme.xaml", UriKind.Absolute),
    };

    public static ResourceDictionary Menus => _menus ??= new ResourceDictionary
    {
        Source = new Uri($"pack://application:,,,/{AssemblyName};component/Menus.xaml", UriKind.Absolute),
    };

    /// <summary>The program's icon (app.ico), with every size from 16 to 256 px for the taskbar and Alt+Tab.</summary>
    public static ImageSource? AppIcon
    {
        get
        {
            if (!_appIconLoaded)
            {
                _appIconLoaded = true;
                try
                {
                    var decoder = new IconBitmapDecoder(
                        new Uri($"pack://application:,,,/{AssemblyName};component/app.ico", UriKind.Absolute),
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    _appIcon = decoder.Frames.FirstOrDefault(f => f.PixelWidth == 32) ?? decoder.Frames[0];
                }
                catch (Exception ex) when (ex is UriFormatException or IOException or NotSupportedException) { }
            }
            return _appIcon;
        }
    }

    /// <summary>
    /// Gives a window the program's icon both in WPF and on its Win32 handle (WM_SETICON), and restores WS_SYSMENU on borderless taskbar
    /// windows after WindowChrome has taken over their frame so Windows 10/11 shows the icon on the taskbar button.
    /// </summary>
    public static void ApplyWindowIcon(Window window)
    {
        window.Icon = AppIcon;
        window.SourceInitialized += (_, _) => ApplyNativeIcon(window);
    }

    private static void ApplyNativeIcon(Window window)
    {
        if (WindowScale.GetOffScreenSnapshot(window)) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Borderless WPF windows (WindowStyle.None) lose WS_SYSMENU, which makes the Windows taskbar omit their icon and ignore taskbar clicks.
        // Restoring WS_SYSMENU while WindowChrome owns the frame brings back the taskbar icon, Alt+Tab icon and click-to-minimise.
        if (window.ShowInTaskbar && window.WindowStyle == WindowStyle.None)
        {
            int style = GetWindowLongW(hwnd, GWL_STYLE);
            int want = WS_SYSMENU | (window.ResizeMode != ResizeMode.NoResize ? WS_MINIMIZEBOX : 0);
            if ((style & want) != want)
            {
                SetWindowLongW(hwnd, GWL_STYLE, style | want);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        EnsureNativeIcons();
        if (_smallHIcon != IntPtr.Zero) SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _smallHIcon);
        if (_bigHIcon != IntPtr.Zero) SendMessageW(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _bigHIcon);
    }

    private static void EnsureNativeIcons()
    {
        if (_nativeIconsLoaded) return;
        _nativeIconsLoaded = true;
        try
        {
            int smCx = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
            int smCy = Math.Max(16, GetSystemMetrics(SM_CYSMICON));
            int lgCx = Math.Max(32, GetSystemMetrics(SM_CXICON));
            int lgCy = Math.Max(32, GetSystemMetrics(SM_CYICON));

            var uri = new Uri($"pack://application:,,,/{AssemblyName};component/app.ico", UriKind.Absolute);
            if (Application.GetResourceStream(uri)?.Stream is { } stream)
            {
                using (stream)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    _smallHIcon = CreateHIconFromIco(bytes, smCx, smCy);
                    _bigHIcon = CreateHIconFromIco(bytes, lgCx, lgCy);
                }
            }

            if (_smallHIcon == IntPtr.Zero || _bigHIcon == IntPtr.Zero)
            {
                var module = GetModuleHandleW(null);
                if (module != IntPtr.Zero)
                {
                    if (_smallHIcon == IntPtr.Zero) _smallHIcon = LoadImageW(module, (IntPtr)IDI_APPLICATION, IMAGE_ICON, smCx, smCy, LR_SHARED);
                    if (_bigHIcon == IntPtr.Zero) _bigHIcon = LoadImageW(module, (IntPtr)IDI_APPLICATION, IMAGE_ICON, lgCx, lgCy, LR_SHARED);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static IntPtr CreateHIconFromIco(byte[] ico, int cx, int cy)
    {
        if (ico.Length < 6) return IntPtr.Zero;
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0, 2));
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2, 2));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2));
        if (reserved != 0 || type != 1 || count == 0 || ico.Length < 6 + count * 16) return IntPtr.Zero;

        int bestOffset = -1, bestSize = 0, bestW = 0, bestH = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int w = ico[entry] == 0 ? 256 : ico[entry];
            int h = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            int size = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 8, 4));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 12, 4));
            if (size <= 0 || offset < 0 || offset > ico.Length - size) continue;

            if (bestOffset < 0)
            {
                bestOffset = offset; bestSize = size; bestW = w; bestH = h;
                continue;
            }

            bool currentFits = bestW >= cx && bestH >= cy;
            bool candidateFits = w >= cx && h >= cy;
            if (candidateFits && (!currentFits || w < bestW || (w == bestW && h < bestH)))
            {
                bestOffset = offset; bestSize = size; bestW = w; bestH = h;
            }
            else if (!currentFits && (w > bestW || (w == bestW && h > bestH)))
            {
                bestOffset = offset; bestSize = size; bestW = w; bestH = h;
            }
        }

        if (bestOffset < 0) return IntPtr.Zero;
        var slice = ico.AsSpan(bestOffset, bestSize).ToArray();
        return CreateIconFromResourceEx(slice, (uint)slice.Length, true, 0x00030000u, cx, cy, 0u);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint Flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImageW(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}

/// <summary>Colours used for people and things, the same everywhere.</summary>
public static class Palette
{
    public static readonly Color AccentColor = Color.FromRgb(0x5B, 0x9D, 0xFF);
    public static readonly Color DangerColor = Color.FromRgb(0xFF, 0x6B, 0x6B);
    public static readonly Color HealColor = Color.FromRgb(0x3D, 0xD6, 0x8C);
    public static readonly Color MutedColor = Color.FromRgb(0x8B, 0x93, 0xA1);

    public static readonly Brush You = Freeze(AccentColor);
    public static readonly Brush Taken = Freeze(DangerColor);
    public static readonly Brush Heal = Freeze(HealColor);
    public static readonly Brush Gold = Freeze(Color.FromRgb(0xF5, 0xC8, 0x6B));
    public static readonly Brush Text = Freeze(Color.FromRgb(0xF2, 0xF4, 0xF7));
    public static readonly Brush Muted = Freeze(MutedColor);

    private static readonly Brush[] Others =
    [
        Freeze(Color.FromRgb(0xB3, 0x8B, 0xFA)), Freeze(Color.FromRgb(0xF5, 0xA6, 0x23)), Freeze(Color.FromRgb(0x2D, 0xD4, 0xBF)),
        Freeze(Color.FromRgb(0xF4, 0x72, 0xB6)), Freeze(Color.FromRgb(0xA3, 0xE6, 0x35)), Freeze(Color.FromRgb(0x94, 0xA3, 0xB8)),
    ];

    /// <summary>A colour per class.</summary>
    private static readonly Dictionary<string, Brush> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Archer"] = Freeze(Color.FromRgb(0xA3, 0xE6, 0x35)),
        ["Bard"] = Freeze(Color.FromRgb(0xF4, 0x72, 0xB6)),
        ["Beastmaster"] = Freeze(Color.FromRgb(0xC8, 0xA2, 0x7A)),
        ["Cleric"] = Freeze(Color.FromRgb(0xF5, 0xF5, 0xF4)),
        ["Druid"] = Freeze(Color.FromRgb(0xFB, 0x92, 0x3C)),
        ["Elementalist"] = Freeze(Color.FromRgb(0x38, 0xBD, 0xF8)),
        ["Enchanter"] = Freeze(Color.FromRgb(0xC0, 0x84, 0xFC)),
        ["Fighter"] = Freeze(Color.FromRgb(0xB4, 0x53, 0x09)),
        ["Inquisitor"] = Freeze(Color.FromRgb(0xEA, 0xB3, 0x08)),
        ["Monk"] = Freeze(Color.FromRgb(0x2D, 0xD4, 0xBF)),
        ["Necromancer"] = Freeze(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ["Paladin"] = Freeze(Color.FromRgb(0xFD, 0xE6, 0x8A)),
        ["Ranger"] = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E)),
        ["Rogue"] = Freeze(Color.FromRgb(0x94, 0xA3, 0xB8)),
        ["Shadow Knight"] = Freeze(Color.FromRgb(0xDC, 0x26, 0x26)),
        ["Shaman"] = Freeze(Color.FromRgb(0x3B, 0x82, 0xF6)),
        ["Spellblade"] = Freeze(Color.FromRgb(0x67, 0xE8, 0xF9)),
        ["Wizard"] = Freeze(Color.FromRgb(0x81, 0x8C, 0xF8)),
    };

    /// <summary>A stable colour per person: by class when known, else one from a fixed set picked by their name.</summary>
    public static Brush For(string name, string? cls = null)
    {
        if (cls is not null && Classes.TryGetValue(cls, out var byClass)) return byClass;
        uint h = 2166136261;
        foreach (char c in name) h = (h ^ char.ToLowerInvariant(c)) * 16777619;
        return Others[h % (uint)Others.Length];
    }

    public static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public static class Fmt
{
    public static string Pct(double f) => $"{f * 100:0.#}%";
}

/// <summary>A column of a <see cref="Table"/>. Width 0 = takes the remaining space.</summary>
public sealed record Col<T>(string Header, double Width, Func<T, string> Text, Func<T, IComparable>? Sort = null, bool Right = true,
    string? Tip = null);

/// <summary>
/// Builds a sortable table: click a header to sort by it (again to flip). The first column can carry a share bar
/// in the row's colour.
/// </summary>
public static class Table
{
    private static readonly Dictionary<string, (int Col, bool Desc)> SortState = [];

    public static FrameworkElement Build<T>(string id, IEnumerable<T> rows, IReadOnlyList<Col<T>> cols, Action rebuild,
        Func<T, double>? bar = null, Func<T, Brush>? color = null, Action<T>? click = null, int defaultSort = 1,
        Func<T, string?>? rowTip = null, Func<T, string>? copy = null)
    {
        var (sortCol, desc) = SortState.GetValueOrDefault(id, (defaultSort, true));
        var list = rows.ToList();
        if (cols[sortCol].Sort is { } key)
            list = desc ? list.OrderByDescending(key).ToList() : list.OrderBy(key).ToList();

        var panel = new StackPanel();
        panel.Children.Add(Row(cols.Select((c, i) =>
        {
            var arrow = i == sortCol ? (desc ? " ↓" : " ↑") : "";
            var b = new Button
            {
                Content = c.Header + arrow,
                Style = (Style)Ui.Theme["HeaderButton"],
                HorizontalContentAlignment = c.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                ToolTip = c.Tip,
                IsEnabled = c.Sort != null,
            };
            int ci = i;
            b.Click += (_, _) =>
            {
                SortState[id] = (ci, ci == sortCol ? !desc : c.Right);
                rebuild();
            };
            return (UIElement)b;
        }).ToList(), cols.Select(c => c.Width).ToList()));
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Ui.Theme["Stroke"] });

        if (list.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Nothing here.", Foreground = Palette.Muted, Margin = new Thickness(8, 10, 0, 6), FontSize = 12 });
            return panel;
        }

        double maxBar = bar is null ? 0 : list.Max(bar);
        foreach (var item in list)
        {
            var cells = cols.Select((c, i) => (UIElement)new TextBlock
            {
                Text = c.Text(item),
                Foreground = Palette.Text,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 12.5,
                TextAlignment = c.Right ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }).ToList();
            var grid = Row(cells, cols.Select(c => c.Width).ToList());
            grid.Height = 32;

            if (bar != null && maxBar > 0)
            {
                var brush = color?.Invoke(item) ?? Palette.You;
                double frac = bar(item) / maxBar;
                var fill = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
                fill.Children.Add(new Border { Background = brush, Opacity = 0.22, CornerRadius = new CornerRadius(5) });
                fill.Children.Add(new Border { Background = brush, Width = 3, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 3, 0, 3) });
                Grid.SetColumnSpan(fill, cols.Count);
                grid.Children.Insert(0, fill);
            }

            var rowBorder = new Border { Child = grid, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
            if (copy != null)
            {
                // A small copy button at the right end of the row, there while the pointer is on it
                var row = item;
                var cb = new Button { Content = "", ToolTip = "Copy this line to the clipboard", Style = (Style)Ui.Theme["IconButton"], Width = 24, Height = 24, Padding = new Thickness(0), FontSize = 11, Visibility = Visibility.Hidden, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                Grid.SetColumnSpan(cb, cols.Count);
                cb.Click += (_, e) =>
                {
                    e.Handled = true;
                    ClipboardText.Copy(copy(row));
                    cb.Content = "";
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                    t.Tick += (_, _) => { t.Stop(); cb.Content = ""; };
                    t.Start();
                };
                grid.Children.Add(cb);
                rowBorder.MouseEnter += (_, _) => cb.Visibility = Visibility.Visible;
                rowBorder.MouseLeave += (_, _) => cb.Visibility = Visibility.Hidden;
            }
            if (rowTip?.Invoke(item) is { Length: > 0 } tip)
            {
                rowBorder.ToolTip = new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, Style = (Style)Ui.Theme["Tip"] };
                ToolTipService.SetInitialShowDelay(rowBorder, 300);
            }
            rowBorder.MouseEnter += (_, _) => rowBorder.Background = (Brush)Ui.Theme["Subtle"];
            rowBorder.MouseLeave += (_, _) => rowBorder.Background = Brushes.Transparent;
            if (click != null)
            {
                rowBorder.Cursor = Cursors.Hand;
                rowBorder.MouseLeftButtonUp += (_, _) => click(item);
            }
            panel.Children.Add(rowBorder);
        }
        return panel;
    }

    /// <summary>
    /// A table with a search box above it: typing narrows the rows to those whose name has every word typed (<see cref="NameSearch"/>),
    /// after a short pause in the typing. The box stays where it is while the table under it is drawn again. The text is kept by the
    /// caller (<paramref name="get"/> and <paramref name="set"/>), so sorting, or leaving the tab and coming back, doesn't empty it;
    /// Esc clears it. With no rows at all there is nothing to search, so no box.
    /// </summary>
    public static FrameworkElement Searchable<T>(IReadOnlyList<T> rows, Func<T, string> name, string hint, Func<string> get, Action<string> set,
        Func<IReadOnlyList<T>, FrameworkElement> table)
    {
        if (rows.Count == 0) return table(rows);

        var body = new ContentControl();
        var count = new TextBlock { FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var field = new TextBox { Style = (Style)Ui.Theme["Field"], Text = get(), ToolTip = "Show only the ones with these words in the name (Esc clears)" };
        var placeholder = new TextBlock
        {
            Text = hint, Foreground = Palette.Muted, FontSize = 11.5, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        void Show()
        {
            var query = get().Trim();
            var shown = NameSearch.Filter(rows, name, query);
            count.Text = query.Length == 0 ? "" : $"{shown.Count:N0} of {rows.Count:N0}";
            body.Content = shown.Count > 0 ? table(shown)
                : new TextBlock { Text = $"Nothing matches “{query}”.", Foreground = Palette.Muted, Margin = new Thickness(8, 10, 0, 6), FontSize = 12 };
        }

        var typing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        typing.Tick += (_, _) => { typing.Stop(); Show(); };
        field.TextChanged += (_, _) =>
        {
            set(field.Text);
            placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            typing.Stop();
            typing.Start();
        };
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || field.Text.Length == 0) return;
            field.Clear();
            e.Handled = true;
        };

        var box = new Grid { Width = 300 };
        box.Children.Add(field);
        box.Children.Add(placeholder);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 10) };
        bar.Children.Add(box);
        bar.Children.Add(count);

        Show();
        var panel = new StackPanel();
        panel.Children.Add(bar);
        panel.Children.Add(body);
        return panel;
    }

    private static Grid Row(IReadOnlyList<UIElement> cells, IReadOnlyList<double> widths)
    {
        var g = new Grid();
        for (int i = 0; i < cells.Count; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = widths[i] <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]),
                MinWidth = widths[i] <= 0 ? 140 : 0,
            });
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return g;
    }
}

public static class ClipboardText
{
    /// <summary>Puts text on the clipboard; another program may have it open for a moment, in which case nothing is copied.</summary>
    public static bool Copy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }
}

/// <summary>
/// Small line drawings for title-bar and tab buttons, drawn as vectors in a 0..16 box so they never depend on a symbol font
/// (which Wine and Proton often lack) and stay sharp at every window scale.
/// </summary>
internal static class Icons
{
    private static readonly Geometry PopOutData = Parse("M 7.5,3.5 L 3.5,3.5 L 3.5,12.5 L 12.5,12.5 L 12.5,8.5 M 9.5,2.5 L 13.5,2.5 L 13.5,6.5 M 7.5,8.5 L 13.5,2.5");
    private static readonly Geometry CompactData = Parse("M 3.5,2.5 L 8,6.5 L 12.5,2.5 M 3.5,13.5 L 8,9.5 L 12.5,13.5");
    private static readonly Geometry ExpandData = Parse("M 3.5,6 L 8,2 L 12.5,6 M 3.5,10 L 8,14 L 12.5,10");
    private static readonly Geometry TextSizeData = Parse("M 1.5,13.3 L 4.85,2.7 L 8.2,13.3 M 2.6,9.8 L 7.1,9.8 M 14.5,10.7 A 2.2,2.6 0 0 0 10.1,10.7 A 2.2,2.6 0 0 0 14.5,10.7 M 14.5,8.1 L 14.5,13.3");
    private static readonly Geometry PinData = Parse("M 5,2.5 L 11,2.5 M 6,2.5 L 6,6.5 L 3.8,9.2 L 12.2,9.2 L 10,6.5 L 10,2.5 M 8,9.2 L 8,14");
    private static readonly Geometry CloseData = Parse("M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5");

    /// <summary>Open box with an outward arrow to the top right (take a tab out into its own window).</summary>
    public static UIElement PopOut() => Make(PopOutData);

    /// <summary>Chevrons pointing inward toward the middle (compact view).</summary>
    public static UIElement Compact() => Make(CompactData);

    /// <summary>Chevrons pointing outward to top and bottom (full view).</summary>
    public static UIElement Expand() => Make(ExpandData);

    /// <summary>Dual-height "Aa" strokes (text size).</summary>
    public static UIElement TextSize() => Make(TextSizeData);

    /// <summary>Thumbtack silhouette (keep on top).</summary>
    public static UIElement Pin() => Make(PinData);

    /// <summary>Diagonal cross strokes (close / return to main window).</summary>
    public static UIElement Close() => Make(CloseData);

    private static Geometry Parse(string path)
    {
        var g = Geometry.Parse(path);
        if (g.CanFreeze) g.Freeze();
        return g;
    }

    private static UIElement Make(Geometry data)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = data,
            Stretch = Stretch.Uniform,
            Width = 12,
            Height = 12,
            StrokeThickness = 1.4,
            SnapsToDevicePixels = true,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
            FallbackValue = Palette.Text,
            TargetNullValue = Palette.Text,
        });
        return path;
    }
}
