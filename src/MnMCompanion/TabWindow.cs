using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace MnMCompanion;

/// <summary>
/// One page of the small window taken out into a window of its own: borderless and opaque like the small window, dragged by its title bar,
/// resized from any edge or the grip, with its own text size, its own pin and (on the pages that have one) a compact view of a headline and
/// its top few rows. The page itself moves here (there is only ever one of each page), so closing this window puts it back into the small
/// window; closing the app leaves it marked open, so it comes back out at the next start.
/// </summary>
internal sealed class TabWindow : Window
{
    private const double BaseMinWidth = 300;
    private const double BaseMinHeight = 240;
    private const double BaseMinCompactHeight = 96;
    private const double BaseMaxCompactHeight = 260;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 520;

    private static readonly Brush TitleForeground = Palette.Freeze(Color.FromRgb(0xC8, 0xD4, 0xE0));

    private readonly Settings _settings;
    private readonly DetachedTabState _state;
    private readonly SessionScope _session;
    private readonly Func<string> _characterLabel;
    private readonly Func<bool> _isShuttingDown;
    private readonly Action<string> _onRedock;
    private readonly DockPanel _root = new();
    private readonly WindowScale _windowScale;
    private readonly ContentControl _host = new() { Focusable = false, IsTabStop = false };
    private readonly TextBlock _status = new() { FontSize = 10, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusBar;
    private readonly TextBlock _who;
    private readonly TextBlock _scopeText = new() { FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _scopeBadge;
    private readonly Button _pin;
    private readonly Button? _compactButton;
    private readonly Action _sessionChangedHandler;

    // Above zero while the window moves or resizes itself (placement, compact toggle, auto-fit, scale), so those are not taken for the player's own resizing
    private int _programmatic;
    private bool _resetting;
    private bool _closing;
    private bool _refitQueued;
    private int _lastPercent;
    private ScrollViewer? _body;

    /// <param name="pageName">The page (Map, Loot, Vendor, Trades, Items, Harvest, Recipes or Market).</param>
    /// <param name="page">The page itself, taken out of the small window.</param>
    /// <param name="characterLabel">The character the small window follows, for the title bar.</param>
    /// <param name="isShuttingDown">True while the app is closing: the window then closes without putting the page back.</param>
    /// <param name="onRedock">Called with the page's name when the player closes this window, to put the page back into the small window.</param>
    /// <param name="anchorRect">The small window's place, for a first opening beside it; null to centre on the main monitor.</param>
    /// <param name="cascadeIndex">How many other tab windows are open, so new ones step down and across instead of covering each other.</param>
    internal TabWindow(string pageName, IMiniPage page, Settings settings, SessionScope session, Func<string> characterLabel, Func<bool> isShuttingDown,
        Action<string> onRedock, Rect? anchorRect = null, int cascadeIndex = 0)
    {
        PageName = TabDockPolicy.IsKnownPage(pageName, out var canonical) ? canonical : pageName;
        Page = page;
        _settings = settings;
        _session = session;
        _characterLabel = characterLabel;
        _isShuttingDown = isShuttingDown;
        _onRedock = onRedock;
        if (!_settings.DetachedTabs.TryGetValue(PageName, out var state) || state is null)
        {
            state = new DetachedTabState();
            _settings.DetachedTabs[PageName] = state;
        }
        _state = state;
        var compactAtStart = _state.Compact && page.SupportsCompact;
        _programmatic = 1;   // the window's own placement, until it has been laid out once, is not the player's

        Resources.MergedDictionaries.Add(Ui.Theme);
        Resources.MergedDictionaries.Add(Ui.Menus);
        Title = $"Companion — {PageName}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Owner = null;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Topmost = _state.OnTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The same frame as the small window: no system caption or glass, so the title bar runs to the very top and the edges still resize
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        Ui.ApplyWindowIcon(this);
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1C));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x90, 0xA0, 0xB0));
        BorderThickness = new Thickness(1);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        PlaceAtStart(compactAtStart, anchorRect, cascadeIndex);

        _windowScale = WindowScale.Attach(this, _root, baseMinWidth: BaseMinWidth, baseMinHeight: compactAtStart ? BaseMinCompactHeight : BaseMinHeight,
            initialPercent: _state.Scale, onChanged: OnScaleChanged);
        _lastPercent = _windowScale.Percent;

        // ---- title bar: the page, the character, the day or session when the compact view hides it, then the buttons
        var caption = new TextBlock
        {
            Text = PageName, FontWeight = FontWeights.SemiBold, Foreground = Palette.Text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _who = new TextBlock
        {
            Foreground = Palette.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _scopeBadge = new Button
        {
            Style = (Style)Ui.Theme["TextButton"], Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 4, 0), MaxWidth = 190,
            VerticalAlignment = VerticalAlignment.Center, Focusable = false, Content = _scopeText, Visibility = Visibility.Collapsed,
            ToolTip = "The day and session on show (the same on every page): click to pick another",
        };
        _scopeBadge.Click += (_, _) => _session.OpenMenu(_scopeBadge);

        var close = IconTitleButton(Icons.Close(), $"Return {PageName} to main window", Close);
        _pin = IconTitleButton(Icons.Pin(), "Keep on top of other windows", () => SetOnTop(!Topmost));
        if (page.SupportsCompact) _compactButton = IconTitleButton(Icons.Compact(), "Compact mini view", () => SetCompactMode(!IsCompact));
        var scaleButton = _windowScale.MakeTitleButton((Style)Ui.Theme["IconButton"]);
        scaleButton.Foreground = TitleForeground;
        scaleButton.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(scaleButton, "Text size");

        var title = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)), Height = 32, Cursor = Cursors.SizeAll, LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        DockPanel.SetDock(_pin, Dock.Right);
        title.Children.Add(_pin);
        if (_compactButton is not null)
        {
            DockPanel.SetDock(_compactButton, Dock.Right);
            title.Children.Add(_compactButton);
        }
        DockPanel.SetDock(scaleButton, Dock.Right);
        title.Children.Add(scaleButton);
        DockPanel.SetDock(caption, Dock.Left);
        title.Children.Add(caption);
        DockPanel.SetDock(_scopeBadge, Dock.Right);
        title.Children.Add(_scopeBadge);
        title.Children.Add(_who);   // takes what is left, trimmed
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject, title)) return;
            if (e.ClickCount == 2 && IsCompact)
            {
                SetCompactMode(false);
                e.Handled = true;
                return;
            }
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }   // the button was already up
        };

        // ---- status line at the bottom, and the page in the middle
        _statusBar = new Border
        {
            Background = (Brush)Ui.Theme["Panel"], BorderBrush = (Brush)Ui.Theme["Stroke"], BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 4, 12, 5), Child = _status,
        };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        _root.Children.Add(title);
        _root.Children.Add(_statusBar);
        _root.Children.Add(_host);

        page.BindStatus(SetStatus);
        _host.Content = page.Element;
        if (page is MapPanel map) map.SetHostScale(_windowScale.Factor);

        _sessionChangedHandler = () =>
        {
            UpdateHeader();
            QueueRefit();
        };
        _session.Changed += _sessionChangedHandler;

        if (compactAtStart) SetCompactMode(true);
        else
        {
            page.SetCompact(false);
            SetCompactButton();
            UpdateStatusBar();
        }
        UpdateHeader();
        ShowPin();

        LocationChanged += (_, _) => Remember(null);
        SizeChanged += (_, e) => Remember(e);
        _root.LayoutUpdated += OnLayoutUpdated;
        Loaded += (_, _) =>
        {
            EndProgrammatic();
            if (Page is MapPanel m) m.SetHostScale(_windowScale.Factor);
            // after the placement above has settled (it ends at Loaded priority), fit a compact window to what it shows
            Dispatcher.BeginInvoke(new Action(QueueRefit), DispatcherPriority.Background);
        };
        Closing += HandleClosing;
    }

    /// <summary>The page's name (Map, Loot, Vendor, Trades, Items, Harvest, Recipes or Market).</summary>
    public string PageName { get; }

    /// <summary>The page this window holds.</summary>
    public IMiniPage Page { get; }

    /// <summary>Whether the window shows the page's compact view.</summary>
    public bool IsCompact { get; private set; }

    /// <summary>This window's text size.</summary>
    public WindowScale Scale => _windowScale;

    // ------------------------------------------------------------------ title bar

    /// <summary>Redraws the title bar: the character the small window follows, and the day or session when the compact view hides the page's own header.</summary>
    public void UpdateHeader()
    {
        var who = _characterLabel();
        _who.Text = string.IsNullOrWhiteSpace(who) ? "" : " · " + who;
        _who.ToolTip = string.IsNullOrWhiteSpace(who) ? null : who;
        var badge = IsCompact && !_session.IsLiveDefault;
        _scopeBadge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        if (badge) _scopeText.Text = (_session.Day != SessionScope.Today ? SessionHeader.DayTitle(_session.Day) : _session.ScopeLabel()) + "  ▾";
    }

    private void SetOnTop(bool on)
    {
        Topmost = on;
        _state.OnTop = on;
        ShowPin();
        _settings.SaveSoon();
    }

    private void ShowPin()
    {
        _pin.Foreground = Topmost ? new SolidColorBrush(Palette.AccentColor) : new SolidColorBrush(Color.FromRgb(0x70, 0x7A, 0x88));
        _pin.Opacity = Topmost ? 1.0 : 0.8;
    }

    private void SetCompactButton()
    {
        if (_compactButton is null) return;
        _compactButton.Content = IsCompact ? Icons.Expand() : Icons.Compact();
        _compactButton.ToolTip = IsCompact ? "Expand full view (or double-click header)" : "Compact mini view";
    }

    /// <summary>A button of the title bar: a line drawing (no symbol font, which Wine often lacks), with the theme's hover highlight.</summary>
    private static Button IconTitleButton(UIElement icon, string tip, Action click)
    {
        var b = new Button
        {
            Content = icon, Style = (Style)Ui.Theme["IconButton"], Width = 28, Height = 26, Margin = new Thickness(1, 0, 1, 0),
            Foreground = TitleForeground, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Whether a click landed on a button (or something inside one) of the title bar, which must not start a drag.</summary>
    private static bool IsInsideButton(DependencyObject? node, DependencyObject stop)
    {
        for (var depth = 0; node is not null && depth < 64 && !ReferenceEquals(node, stop); depth++)
        {
            if (node is ButtonBase) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ------------------------------------------------------------------ status line

    private void SetStatus(string text)
    {
        _status.Text = text ?? "";
        UpdateStatusBar();
    }

    /// <summary>The status line takes no room in the compact view while it has nothing to say.</summary>
    private void UpdateStatusBar() =>
        _statusBar.Visibility = !IsCompact || !string.IsNullOrWhiteSpace(_status.Text) ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ compact view

    /// <summary>
    /// Switches between the page's full view and its compact one. Going compact keeps the full view's size to come back to and lets the window be
    /// much shorter; its height is the one the player last dragged it to, or else fitted to what the compact view shows.
    /// </summary>
    public void SetCompactMode(bool compact)
    {
        if (compact && !Page.SupportsCompact) compact = false;
        if (compact == IsCompact) return;
        BeginProgrammatic();
        try
        {
            if (compact)
            {
                if (IsLoaded && !WindowScale.GetOffScreenSnapshot(this) && WindowState == WindowState.Normal)
                    _state.Bounds = new Bounds(Left, Top, Width, Height);
                IsCompact = true;
                _state.Compact = true;
                SetCompactButton();
                Page.SetCompact(true);
                UpdateStatusBar();
                UpdateHeader();
                var target = _state.CompactHeight ?? AutoFitCompactHeight();
                _windowScale.SetBaseMinSize(BaseMinWidth, BaseMinCompactHeight, targetOuterHeight: target);
            }
            else
            {
                IsCompact = false;
                _state.Compact = false;
                SetCompactButton();
                Page.SetCompact(false);
                UpdateStatusBar();
                UpdateHeader();
                _windowScale.SetBaseMinSize(BaseMinWidth, BaseMinHeight, targetOuterHeight: _state.Bounds?.Height ?? (DefaultHeight * _windowScale.Factor));
            }
        }
        finally
        {
            EndProgrammatic();
        }
        _settings.SaveSoon();
    }

    /// <summary>The outer height that shows the whole compact view (measured now), held between the least and the most a compact window is given.</summary>
    private double AutoFitCompactHeight()
    {
        var f = _windowScale.Factor;
        var outerWidth = !double.IsNaN(Width) && Width > 0 ? Width : DefaultWidth * f;
        _root.Measure(new Size(Math.Max(BaseMinWidth, outerWidth / f), double.PositiveInfinity));
        var height = (_root.DesiredSize.Height * f) + 4;
        return Math.Clamp(height, BaseMinCompactHeight * f, BaseMaxCompactHeight * f);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => QueueRefit();

    /// <summary>
    /// After a layout (rows of a stats page arrive a moment after it is drawn), fits a compact window the player has not sized by hand to what it now shows.
    /// The work waits for the dispatcher to be idle, and does nothing while the window is resizing itself.
    /// </summary>
    private void QueueRefit()
    {
        if (_refitQueued || _closing || !IsCompact || _state.CompactHeight is not null || _programmatic > 0 || !IsLoaded) return;
        _refitQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refitQueued = false;
            RefitCompact();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Fits the compact window to its content without measuring it again: the page's list is short of (or past) its box by the difference between what
    /// the list holds and what its box shows. Only a change of more than a pixel and a half is made, so fitting settles.
    /// </summary>
    private void RefitCompact()
    {
        if (_closing || !IsCompact || _state.CompactHeight is not null || _programmatic > 0 || !IsLoaded || WindowState != WindowState.Normal) return;
        if (BodyScroll() is not { } body || _root.ActualHeight <= 0) return;
        var f = _windowScale.Factor;
        var fit = ((_root.ActualHeight - body.ViewportHeight + body.ExtentHeight) * f) + 4;
        var target = Math.Clamp(fit, BaseMinCompactHeight * f, BaseMaxCompactHeight * f);
        var current = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
        if (Math.Abs(target - current) <= 1.5) return;
        BeginProgrammatic();
        try { _windowScale.SetBaseMinSize(BaseMinWidth, BaseMinCompactHeight, targetOuterHeight: target); }
        finally { EndProgrammatic(); }
    }

    /// <summary>The page's own scrolling list (the shallowest scroll box in it that is not part of a text box).</summary>
    private ScrollViewer? BodyScroll()
    {
        if (_body is not null) return _body;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(Page.Element);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ScrollViewer sv && sv.TemplatedParent is not TextBoxBase) return _body = sv;
            if (node is not (Visual or System.Windows.Media.Media3D.Visual3D)) continue;
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    // ------------------------------------------------------------------ place, size and scale

    /// <summary>Where the window opens: where it was left, else beside the small window (stepped for each tab already out), else in the middle of the main monitor.</summary>
    private void PlaceAtStart(bool compactAtStart, Rect? anchorRect, int cascadeIndex)
    {
        var s = UiScale.Factor(_state.Scale);
        if (_state.Bounds is { } b && b.Width > 0 && b.Height > 0 && double.IsFinite(b.Left) && double.IsFinite(b.Top))
        {
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = compactAtStart && _state.CompactHeight is double ch && ch > 0 ? ch : b.Height;
            return;
        }

        var w = DefaultWidth * s;
        var h = DefaultHeight * s;
        if (anchorRect is { IsEmpty: false } a && double.IsFinite(a.Left) && double.IsFinite(a.Top) && double.IsFinite(a.Width) && double.IsFinite(a.Height))
        {
            var work = WindowScale.WorkAreaForRect(a.Left, a.Top, a.Width, a.Height, Application.Current?.MainWindow);
            var (left, top) = BesideAnchor(a, cascadeIndex, w, work);
            var box = UiScale.ClampToWorkArea(left, top, w, h, BaseMinWidth, compactAtStart ? BaseMinCompactHeight : BaseMinHeight, _state.Scale, work);
            (Left, Top, Width, Height) = (box.Left, box.Top, box.Width, box.Height);
            return;
        }

        var wa = SystemParameters.WorkArea;
        Width = Math.Min(w, wa.Width);
        Height = Math.Min(h, wa.Height);
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }

    /// <summary>At the right of the small window, stepped down and across per tab already out; at its left when there is no room at the right.</summary>
    private static (double Left, double Top) BesideAnchor(Rect anchor, int cascadeIndex, double width, WorkArea work)
    {
        var step = 28 * Math.Max(0, cascadeIndex);
        var left = anchor.Right + 12 + step;
        if (left + width > work.Right && anchor.Left - 12 - width - step >= work.Left) left = anchor.Left - 12 - width - step;
        return (left, anchor.Top + step);
    }

    private void OnScaleChanged(int percent)
    {
        var old = _lastPercent;
        _lastPercent = percent;
        if (Page is MapPanel map) map.SetHostScale(_windowScale.Factor);
        if (_resetting) return;
        _state.Scale = percent;
        BeginProgrammatic();
        try
        {
            var record = IsLoaded && !WindowScale.GetOffScreenSnapshot(this) && WindowState == WindowState.Normal;
            if (IsCompact)
            {
                // the full view's height grows or shrinks with the text as the window would have
                var ratio = UiScale.Factor(percent) / UiScale.Factor(old);
                if (record && _state.Bounds is { } b) _state.Bounds = new Bounds(Left, Top, Width, b.Height * ratio);
                if (_state.CompactHeight is not null)
                {
                    if (record) _state.CompactHeight = Height;
                }
                else _windowScale.SetBaseMinSize(BaseMinWidth, BaseMinCompactHeight, targetOuterHeight: AutoFitCompactHeight());
            }
            else if (record) _state.Bounds = new Bounds(Left, Top, Width, Height);
        }
        finally
        {
            EndProgrammatic();
        }
        _settings.SaveSoon();
    }

    /// <summary>Keeps where the player put the window and how big they made it (the compact height apart from the full view's).</summary>
    private void Remember(SizeChangedEventArgs? e)
    {
        if (!IsLoaded || _programmatic > 0 || _resetting || _closing || WindowState != WindowState.Normal || WindowScale.GetOffScreenSnapshot(this)) return;
        if (IsCompact)
        {
            if (e is not null && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) _state.CompactHeight = Height;
            _state.Bounds = new Bounds(Left, Top, Width, _state.Bounds?.Height ?? (DefaultHeight * _windowScale.Factor));
        }
        else _state.Bounds = new Bounds(Left, Top, Width, Height);
        _settings.SaveSoon();
    }

    /// <summary>
    /// Back to normal text size, forgetting where the window was and the compact height, placed beside the small window (stepped by
    /// <paramref name="cascadeIndex"/>) on the main monitor.
    /// </summary>
    public void ResetScaleAndBounds(Rect anchorRect, int cascadeIndex)
    {
        _resetting = true;
        BeginProgrammatic();
        try
        {
            _state.Scale = UiScale.Default;
            _state.Bounds = null;
            _state.CompactHeight = null;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _windowScale.SetScale(UiScale.Default, resizeOuter: false);
            _lastPercent = _windowScale.Percent;
            if (Page is MapPanel map) map.SetHostScale(_windowScale.Factor);

            var wa = SystemParameters.WorkArea;
            var work = new WorkArea(wa.Left, wa.Top, wa.Width, wa.Height);
            Width = Math.Min(DefaultWidth, work.Width);
            var height = IsCompact ? AutoFitCompactHeight() : DefaultHeight;
            var baseMinHeight = IsCompact ? BaseMinCompactHeight : BaseMinHeight;
            var (left, top) = BesideAnchor(anchorRect, cascadeIndex, DefaultWidth, work);
            var box = UiScale.ClampToWorkArea(left, top, DefaultWidth, height, BaseMinWidth, baseMinHeight, UiScale.Default, work);
            MaxWidth = work.Width;
            MaxHeight = work.Height;
            MinWidth = box.MinWidth;
            MinHeight = box.MinHeight;
            Width = box.Width;
            Height = box.Height;
            Left = box.Left;
            Top = box.Top;
            _state.Scale = UiScale.Default;
            _state.Bounds = null;
            _state.CompactHeight = null;
            _settings.SaveSoon();
        }
        finally
        {
            EndProgrammatic();
            if (IsLoaded && IsVisible) Dispatcher.BeginInvoke(new Action(() => _resetting = false), DispatcherPriority.Loaded);
            else _resetting = false;
        }
    }

    private void BeginProgrammatic() => _programmatic++;

    /// <summary>Ends a stretch of the window's own resizing once the layout it caused has run (the size-changed events come with that layout).</summary>
    private void EndProgrammatic()
    {
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_programmatic > 0) _programmatic--;
            }), DispatcherPriority.Loaded);
        }
        else if (_programmatic > 0) _programmatic--;
    }

    // ------------------------------------------------------------------ closing

    /// <summary>
    /// The page leaves this window before the window goes (so it can be put back into the small one). Closed by the player, the page goes back to the
    /// small window and the tab is marked closed (its place, size, text size, pin and compact choice are kept for next time); closed because the app is
    /// closing, the tab stays marked open so it comes back out at the next start.
    /// </summary>
    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _host.Content = null;
        _session.Changed -= _sessionChangedHandler;
        _root.LayoutUpdated -= OnLayoutUpdated;
        if (_isShuttingDown()) return;
        Page.SetCompact(false);
        _state.Open = false;
        _settings.SaveSoon();
        _onRedock(PageName);
    }
}
