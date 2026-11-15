using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MnMCompanion.Loot;

namespace MnMCompanion;

/// <summary>
/// One page of the small window (Map, Loot, Vendor, Trades, Items, Harvest, Recipes or Market) in a window of its own, the way History is: moved and resized
/// wherever the player likes, kept on top of the game or not, drawn at its own size (Ctrl + wheel), and on supported pages switched to a compact mini view.
/// It is a second copy of the page, looking at the same ledger, history and session, and the small window keeps it up to date (<see cref="Refresh"/>,
/// <see cref="Tick"/>), so it shows the same thing as the page does there. Where it sits, whether it was open, and whether it is in mini view are remembered
/// (<see cref="Settings.PageWindows"/>), and it comes back the same way when the app starts.
/// </summary>
internal sealed class PageWindow : Window
{
    private const double BaseMinWidth = 300, BaseMinHeight = 240, BaseMinCompactHeight = 96, BaseMaxCompactHeight = 260;
    private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    private static readonly Geometry CompactGlyphData = FreezeGeometry("M 2.5,2 L 6.5,5.5 L 10.5,2 M 2.5,11 L 6.5,7.5 L 10.5,11");
    private static readonly Geometry ExpandGlyphData = FreezeGeometry("M 2.5,5 L 6.5,1.5 L 10.5,5 M 2.5,8 L 6.5,11.5 L 10.5,8");

    private readonly LootStore _store;
    private readonly SessionScope _session;
    private readonly Settings _settings;
    private readonly TextBlock _status;
    private readonly Border _bottom;
    private readonly TextBlock _who;
    private readonly TextBlock _scopeChipText;
    private readonly Button _scopeChip;
    private readonly Button _pin;
    private readonly Button? _compactButton;
    private readonly WindowScale _scale;
    private Bounds _expandedBounds;
    private bool _compact, _refitQueued, _measuring, _inSizeMove, _closing;
    private int _programmatic, _lastPercent;
    private HwndSource? _hwndSource;
    private DispatcherTimer? _saveSoon;

    /// <summary>The page shown here.</summary>
    public IMiniPage Page { get; }

    /// <summary>The name of the page: "Map", "Loot", ...</summary>
    public string Key { get; }

    /// <summary>The player pressed the ✕ (rather than the app closing): the window is not brought back next time.</summary>
    public bool ClosedByPlayer { get; private set; }

    /// <summary>Whether the window is currently showing its compact mini view.</summary>
    public bool IsCompact => _compact;

    /// <param name="status">The page's status line, which this window shows along its bottom.</param>
    /// <param name="session">Shared day and session scope, used for the compact title-bar scope chip.</param>
    /// <param name="owner">The small window: this one stays in front of it, and goes with it.</param>
    /// <param name="cascade">How many page windows are open already, so a new one that has no place yet doesn't land on top of them.</param>
    public PageWindow(string key, IMiniPage page, TextBlock status, LootStore store, SessionScope session, Settings settings, Window owner, int cascade)
    {
        Key = key;
        Page = page;
        _store = store;
        _session = session;
        _settings = settings;
        _status = status;
        Owner = owner;
        Title = "Companion · " + key;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        Ui.ApplyWindowIcon(this);
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1C));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x90, 0xA0, 0xB0));
        BorderThickness = new Thickness(1);
        Topmost = settings.OnTop;
        MinWidth = BaseMinWidth;
        MinHeight = BaseMinHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        this.UseText();

        var saved = settings.PageWindows.GetValueOrDefault(key);
        var scaleNow = Display.UiScale.Factor(settings.ScaleOf("Page " + key));
        bool placed = saved is not null && saved.Width >= MinWidth * scaleNow && saved.Height >= MinHeight * scaleNow;
        var b = placed ? saved! : new Bounds(double.NaN, double.NaN, 440, 580);
        Width = b.Width;
        Height = b.Height;
        Left = double.IsNaN(b.Left) ? owner.Left + 36 + 30 * cascade : b.Left;
        Top = double.IsNaN(b.Top) ? owner.Top + 60 + 30 * cascade : b.Top;
        KeepOnScreen();

        var caption = new TextBlock { Text = key, Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEE)), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        _who = new TextBlock { Foreground = Palette.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        _scopeChipText = new TextBlock { FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        _scopeChip = new Button
        {
            Content = _scopeChipText,
            Style = (Style)Ui.Theme["TextButton"],
            FontSize = 10.5,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            ToolTip = "Shows an earlier session or whole day. Click to switch.",
        };
        AutomationProperties.SetName(_scopeChip, "Session or day scope");
        _scopeChip.Click += (_, _) => _session.OpenMenu(_scopeChip);

        var close = TimersWindow.TitleButton("", "Close this window. The page is still in the small window.", () => { ClosedByPlayer = true; Close(); });
        _pin = TimersWindow.TitleButton("", "Keep this window on top of the game", () => { Topmost = !Topmost; ShowPin(); });
        var look = TimersWindow.TitleButton("", "Size and font. Ctrl and the mouse wheel over this window changes its size too.", () => LookWindow.Open(this, settings));
        if (page.SupportsCompact)
        {
            _compactButton = new Button
            {
                Style = (Style)Ui.Theme["IconButton"],
                Width = 30,
                Height = 24,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _compactButton.Click += (_, _) => SetCompactMode(!_compact);
        }

        var title = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)), Height = 32, Cursor = Cursors.SizeAll, LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_pin, Dock.Right);
        DockPanel.SetDock(look, Dock.Right);
        if (_compactButton is not null) DockPanel.SetDock(_compactButton, Dock.Right);
        DockPanel.SetDock(caption, Dock.Left);
        DockPanel.SetDock(_who, Dock.Left);
        DockPanel.SetDock(_scopeChip, Dock.Left);
        title.Children.Add(close);
        title.Children.Add(_pin);
        title.Children.Add(look);
        if (_compactButton is not null) title.Children.Add(_compactButton);
        title.Children.Add(caption);
        title.Children.Add(_who);
        title.Children.Add(_scopeChip);
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject, title)) return;
            if (_compact && e.ClickCount == 2)
            {
                SetCompactMode(false);
                e.Handled = true;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        status.Margin = new Thickness(0);
        _bottom = new Border
        {
            Background = (Brush)Ui.Theme["Panel"], BorderBrush = (Brush)Ui.Theme["Stroke"], BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 5, 12, 5), Child = status,
        };
        var root = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(_bottom, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(_bottom);
        root.Children.Add(page.Element);
        Content = root;

        _scale = WindowScale.Attach(this, settings, "Page " + key, key + " window", sizeIsSaved: placed);
        _lastPercent = _scale.Percent;
        _expandedBounds = new Bounds(Left, Top, Width, Height);
        _scale.Changed += OnScaleChanged;

        var statusText = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        statusText?.AddValueChanged(_status, OnStatusTextChanged);

        void OnSessionChanged()
        {
            UpdateScopeChip();
            if (_compact && !_settings.PageWindowsCompactHeight.ContainsKey(Key)) QueueAutoFit();
        }
        _session.Changed += OnSessionChanged;

        SourceInitialized += (_, _) =>
        {
            _hwndSource = HwndSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
        };
        LayoutUpdated += OnLayoutUpdated;
        Closed += (_, _) =>
        {
            _closing = true;
            _scale.Changed -= OnScaleChanged;
            _session.Changed -= OnSessionChanged;
            statusText?.RemoveValueChanged(_status, OnStatusTextChanged);
            LayoutUpdated -= OnLayoutUpdated;
            _hwndSource?.RemoveHook(WndProc);
            if (_saveSoon is { IsEnabled: true }) { _saveSoon.Stop(); _settings.Save(); }
        };

        ShowPin();
        ShowSelection();
        UpdateBottomVisibility();
        UpdateCompactButton();
        UpdateScopeChip();
        if (page.SupportsCompact && settings.PageWindowsCompact.Contains(key, StringComparer.OrdinalIgnoreCase))
            SetCompactMode(true, save: false);

        LocationChanged += (_, _) => OnLocationChanged();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) =>
        {
            page.Refresh();
            if (_compact && !_settings.PageWindowsCompactHeight.ContainsKey(Key)) QueueAutoFit();
        };
    }

    /// <summary>The character the page is about, in the title bar.</summary>
    public void ShowSelection() => _who.Text = _store.Selected is { } s ? $"{s.Character} · {s.ServerName}" : "No ledger";

    public bool NeedsHistory => Page.NeedsHistory;

    public void Refresh()
    {
        ShowSelection();
        UpdateScopeChip();
        Page.Refresh();
        if (_compact && !_settings.PageWindowsCompactHeight.ContainsKey(Key)) QueueAutoFit();
    }

    public void Tick() => Page.Tick();

    /// <summary>Switches between the full window and the compact mini view, keeping expanded and manual compact heights separate.</summary>
    public void SetCompactMode(bool compact, bool save = true)
    {
        if (compact && !Page.SupportsCompact) return;
        if (compact == _compact) return;
        if (compact)
        {
            if (IsLoaded && ActualHeight > 0 && WindowState == WindowState.Normal)
                _expandedBounds = new Bounds(Left, Top, Width, ActualHeight);
            _scale.SetMinHeight(BaseMinCompactHeight);
            _compact = true;
            Page.SetCompact(true);
            UpdateBottomVisibility();
            UpdateCompactButton();
            UpdateScopeChip();
            BeginProgrammatic();
            try
            {
                if (_settings.PageWindowsCompactHeight.TryGetValue(Key, out var manualH) && manualH > 0)
                {
                    Height = Math.Max(MinHeight, manualH);
                    KeepOnScreen();
                }
                else
                {
                    AutoFitCompactHeight();
                    QueueAutoFit();
                }
            }
            finally
            {
                EndProgrammatic();
            }
            if (save && !_settings.PageWindowsCompact.Contains(Key, StringComparer.OrdinalIgnoreCase))
            {
                _settings.PageWindowsCompact.Add(Key);
                ScheduleSave();
            }
        }
        else
        {
            _compact = false;
            Page.SetCompact(false);
            _scale.SetMinHeight(BaseMinHeight);
            UpdateBottomVisibility();
            UpdateCompactButton();
            UpdateScopeChip();
            BeginProgrammatic();
            try
            {
                Height = Math.Max(MinHeight, _expandedBounds.Height);
                KeepOnScreen();
            }
            finally
            {
                EndProgrammatic();
            }
            if (save)
            {
                _settings.PageWindowsCompact.RemoveAll(k => string.Equals(k, Key, StringComparison.OrdinalIgnoreCase));
                ScheduleSave();
            }
        }
    }

    private void ShowPin() => _pin.Foreground = Topmost ? new SolidColorBrush(Palette.AccentColor) : new SolidColorBrush(Color.FromRgb(0x70, 0x7A, 0x88));

    private void UpdateBottomVisibility() =>
        _bottom.Visibility = (_compact && string.IsNullOrEmpty(_status.Text)) ? Visibility.Collapsed : Visibility.Visible;

    private void OnStatusTextChanged(object? sender, EventArgs e)
    {
        UpdateBottomVisibility();
        if (_compact && !_settings.PageWindowsCompactHeight.ContainsKey(Key)) QueueAutoFit();
    }

    private void UpdateCompactButton()
    {
        if (_compactButton is null) return;
        _compactButton.Content = BuildCompactIcon(_compact);
        _compactButton.Foreground = _compact
            ? new SolidColorBrush(Palette.AccentColor)
            : new SolidColorBrush(Color.FromRgb(0x70, 0x7A, 0x88));
        _compactButton.ToolTip = _compact
            ? "Full window: show every row and control again (or double-click the title bar)."
            : "Mini window: show only the headline and top 3 rows.";
        AutomationProperties.SetName(_compactButton, _compact ? "Expand to full window" : "Switch to mini window");
    }

    private void UpdateScopeChip()
    {
        bool show = _compact && (_session.Day != SessionScope.Today || (_session.ActiveSession() is not { Open: true } && _store.Sessions.Marks.Count > 0));
        _scopeChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        double factor = Display.UiScale.Factor(_scale.Percent);
        double unscaledW = (ActualWidth > 0 ? ActualWidth : Width) / factor;
        if (show)
        {
            _scopeChipText.Text = (_session.Day != SessionScope.Today ? SessionHeader.DayTitle(_session.Day) : _session.ScopeLabel()) + "  ▾";
            _who.MaxWidth = Math.Max(40, unscaledW - 280);
        }
        else
        {
            _who.MaxWidth = double.PositiveInfinity;
        }
    }

    private static Geometry FreezeGeometry(string path)
    {
        var g = Geometry.Parse(path);
        if (g.CanFreeze) g.Freeze();
        return g;
    }

    private static UIElement BuildCompactIcon(bool expand)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = expand ? ExpandGlyphData : CompactGlyphData,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true,
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
        });
        var canvas = new Canvas { Width = 13, Height = 13, SnapsToDevicePixels = true };
        canvas.Children.Add(path);
        return canvas;
    }

    private static bool IsInsideButton(DependencyObject? node, DependencyObject stop)
    {
        for (var depth = 0; node is not null && depth < 64 && !ReferenceEquals(node, stop); depth++)
        {
            if (node is ButtonBase) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE) _inSizeMove = true;
        else if (msg == WM_EXITSIZEMOVE)
        {
            _inSizeMove = false;
            if (_compact && !_settings.PageWindowsCompactHeight.ContainsKey(Key)) QueueAutoFit();
        }
        return IntPtr.Zero;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_measuring || _closing || _inSizeMove || !_compact || _settings.PageWindowsCompactHeight.ContainsKey(Key)) return;
        QueueAutoFit();
    }

    private void QueueAutoFit()
    {
        if (_refitQueued || _closing || _inSizeMove || !_compact || _settings.PageWindowsCompactHeight.ContainsKey(Key)) return;
        _refitQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refitQueued = false;
            if (!_closing && !_inSizeMove) AutoFitCompactHeight();
        }, DispatcherPriority.Background);
    }

    private void AutoFitCompactHeight()
    {
        if (!_compact || _closing || _inSizeMove || _settings.PageWindowsCompactHeight.ContainsKey(Key)) return;
        _measuring = true;
        BeginProgrammatic();
        try
        {
            double factor = Display.UiScale.Factor(_scale.Percent);
            double clientW = Math.Max(100, (ActualWidth > 0 ? ActualWidth : Width) / factor - 2);
            Page.Element.Measure(new Size(clientW, double.PositiveInfinity));
            double bottomH = 0;
            if (_bottom.Visibility == Visibility.Visible)
            {
                _bottom.Measure(new Size(clientW, double.PositiveInfinity));
                bottomH = _bottom.DesiredSize.Height;
            }
            double unscaled = 32 + Page.Element.DesiredSize.Height + bottomH;
            double target = Math.Clamp(unscaled * factor + 2, BaseMinCompactHeight * factor, BaseMaxCompactHeight * factor);
            if (Math.Abs(Height - target) > 0.5)
            {
                Height = target;
                KeepOnScreen();
            }
            if (IsLoaded) UpdateLayout();
        }
        finally
        {
            EndProgrammatic();
            _measuring = false;
        }
    }

    private void BeginProgrammatic() => _programmatic++;

    private void EndProgrammatic()
    {
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_programmatic > 0) _programmatic--;
            }, DispatcherPriority.Loaded);
        }
        else if (_programmatic > 0)
        {
            _programmatic--;
        }
    }

    private void OnScaleChanged()
    {
        int to = _scale.Percent;
        int from = _lastPercent;
        if (to == from) return;
        _lastPercent = to;
        BeginProgrammatic();
        try
        {
            if (_compact)
            {
                _expandedBounds = new Bounds(Left, Top, Width, Display.UiScale.Resized(_expandedBounds.Height, from, to));
                _settings.PageWindows[Key] = _expandedBounds;
                if (_settings.PageWindowsCompactHeight.TryGetValue(Key, out var manualH) && manualH > 0)
                {
                    double nextH = Math.Max(MinHeight, Display.UiScale.Resized(manualH, from, to));
                    _settings.PageWindowsCompactHeight[Key] = nextH;
                    Height = nextH;
                    KeepOnScreen();
                }
                else
                {
                    AutoFitCompactHeight();
                    QueueAutoFit();
                }
                UpdateScopeChip();
                ScheduleSave();
            }
            else if (WindowState == WindowState.Normal)
            {
                _expandedBounds = new Bounds(Left, Top, Width, Height);
                _settings.PageWindows[Key] = _expandedBounds;
                ScheduleSave();
            }
        }
        finally
        {
            EndProgrammatic();
        }
    }

    private void OnLocationChanged()
    {
        if (WindowState != WindowState.Normal || _scale.IsScaling) return;
        _expandedBounds = _expandedBounds with { Left = Left, Top = Top };
        _settings.PageWindows[Key] = _expandedBounds;
        ScheduleSave();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal || _scale.IsScaling) return;
        if (_compact)
        {
            _expandedBounds = _expandedBounds with { Left = Left, Top = Top, Width = Width };
            _settings.PageWindows[Key] = _expandedBounds;
            if (e.WidthChanged) UpdateScopeChip();
            if (IsLoaded && _programmatic == 0 && e.PreviousSize.Height > 0 && e.HeightChanged && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1.0)
                _settings.PageWindowsCompactHeight[Key] = Height;
            else if (e.WidthChanged && !_settings.PageWindowsCompactHeight.ContainsKey(Key))
                QueueAutoFit();
            ScheduleSave();
        }
        else
        {
            _expandedBounds = new Bounds(Left, Top, Width, Height);
            _settings.PageWindows[Key] = _expandedBounds;
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        _saveSoon ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _saveSoon!.Stop(); _settings.Save(); }, Dispatcher);
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    /// <summary>Saved on another monitor layout: pull it back onto the desktop.</summary>
    private void KeepOnScreen()
    {
        if (Left <= -10000) return;
        double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
        double r = l + SystemParameters.VirtualScreenWidth, b = t + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, l, Math.Max(l, r - 120));
        Top = Math.Clamp(Top, t, Math.Max(t, b - 60));
    }
}
