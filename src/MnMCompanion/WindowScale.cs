using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MnMCompanion;

/// <summary>
/// How big one window draws its contents (<see cref="UiScale"/>), and keeping that window on its monitor when the size changes.
/// <see cref="Attach"/> is for the app's own resizable windows (with Ctrl + / Ctrl - / Ctrl 0 and a title-bar menu); <see cref="AttachDialog"/> is for
/// the small dialogs that take their size from the window that opened them. Menus and tooltips opened over a scaled window are scaled with it.
/// </summary>
internal sealed class WindowScale
{
    private const int MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>The <see cref="WindowScale"/> attached to a window by <see cref="Attach"/>.</summary>
    private static readonly DependencyProperty InstanceProperty =
        DependencyProperty.RegisterAttached("Instance", typeof(WindowScale), typeof(WindowScale), new PropertyMetadata(null));

    /// <summary>The scale given to a dialog by <see cref="AttachDialog"/> (0 when none was).</summary>
    private static readonly DependencyProperty DialogScaleProperty =
        DependencyProperty.RegisterAttached("DialogScale", typeof(int), typeof(WindowScale), new PropertyMetadata(0));

    /// <summary>The box <see cref="AttachDialog"/> put a dialog's content in, so a second call does not wrap it twice.</summary>
    private static readonly DependencyProperty DialogDecoratorProperty =
        DependencyProperty.RegisterAttached("DialogDecorator", typeof(Decorator), typeof(WindowScale), new PropertyMetadata(null));

    /// <summary>The scale an open context menu was drawn at, so the tooltips of its items can match it.</summary>
    private static readonly DependencyProperty PopupScaleProperty =
        DependencyProperty.RegisterAttached("PopupScale", typeof(int), typeof(WindowScale), new PropertyMetadata(0));

    /// <summary>The window is being drawn off-screen for a picture: it is scaled, but never moved onto the visible desktop.</summary>
    private static readonly DependencyProperty OffScreenSnapshotProperty =
        DependencyProperty.RegisterAttached("OffScreenSnapshot", typeof(bool), typeof(WindowScale), new PropertyMetadata(false));

    private readonly Window _window;
    private readonly Decorator _decorator;
    private readonly Action<int>? _onChanged;
    private double _baseMinWidth;
    private double _baseMinHeight;
    private bool _adjusting;
    private Button? _titleButton;

    /// <summary>The window's scale, as a percent of normal size (one of <see cref="UiScale.Presets"/>).</summary>
    public int Percent { get; private set; }

    /// <summary>The multiplier for <see cref="Percent"/> (1.0 at normal size).</summary>
    public double Factor => UiScale.Factor(Percent);

    static WindowScale()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnToolTipOpened));
    }

    private WindowScale(Window window, Decorator decorator, double baseMinWidth, double baseMinHeight, Action<int>? onChanged)
    {
        _window = window;
        _decorator = decorator;
        _baseMinWidth = baseMinWidth;
        _baseMinHeight = baseMinHeight;
        _onChanged = onChanged;
        Percent = UiScale.Default;
    }

    // ------------------------------------------------------------------ snapshots

    /// <summary>Marks a window as drawn off-screen for a picture (before it is shown): it is still scaled, but never pulled onto the visible desktop.</summary>
    public static void SetOffScreenSnapshot(Window window, bool enabled = true) => window.SetValue(OffScreenSnapshotProperty, enabled);

    /// <summary>Whether <see cref="SetOffScreenSnapshot"/> marked the window.</summary>
    public static bool GetOffScreenSnapshot(Window window) => (bool)window.GetValue(OffScreenSnapshotProperty);

    // ------------------------------------------------------------------ lookup

    /// <summary>The scale a window draws at: the one <see cref="Attach"/> or <see cref="AttachDialog"/> gave it, else normal size.</summary>
    public static int ScaleOf(Window? window)
    {
        if (window is null) return UiScale.Default;
        if (window.GetValue(InstanceProperty) is WindowScale scale) return scale.Percent;
        var dialog = (int)window.GetValue(DialogScaleProperty);
        return dialog > 0 ? dialog : UiScale.Default;
    }

    // ------------------------------------------------------------------ attaching

    /// <summary>
    /// Scales a resizable window: puts <paramref name="rootContent"/> in a box that is drawn bigger, keeps the window's least size in step, keeps the window on its
    /// monitor, and listens for Ctrl + / Ctrl - / Ctrl 0. <paramref name="onChanged"/> hears each new scale the player picks (not the starting one).
    /// </summary>
    public static WindowScale Attach(Window window, FrameworkElement rootContent, double baseMinWidth, double baseMinHeight, int initialPercent, Action<int>? onChanged = null)
    {
        if (ReferenceEquals(window.Content, rootContent)) window.Content = null;
        var decorator = new Decorator { UseLayoutRounding = true, Child = rootContent };
        window.Content = decorator;

        var scale = new WindowScale(window, decorator, baseMinWidth, baseMinHeight, onChanged);
        window.SetValue(InstanceProperty, scale);

        // the starting scale: drawn at once, but the saved outer size is not multiplied again and nothing is saved
        scale.Percent = UiScale.Nearest(initialPercent);
        scale.ApplyLook();
        window.MinWidth = baseMinWidth * scale.Factor;
        window.MinHeight = baseMinHeight * scale.Factor;

        window.SourceInitialized += (_, _) => scale.Settle(keepUnsetPosition: true);
        window.Loaded += (_, _) => scale.Settle(keepUnsetPosition: false);
        window.LocationChanged += (_, _) =>
        {
            if (scale._adjusting || !window.IsLoaded || window.WindowState != WindowState.Normal || GetOffScreenSnapshot(window)) return;
            scale.Settle(keepUnsetPosition: false);
        };
        // bubbling, so a box that records keys (the timer key box) takes Ctrl 0 and the like first
        window.KeyDown += (_, e) =>
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    scale.SetScale(UiScale.Next(scale.Percent));
                    e.Handled = true;
                    break;
                case Key.OemMinus or Key.Subtract:
                    scale.SetScale(UiScale.Previous(scale.Percent));
                    e.Handled = true;
                    break;
                case Key.D0 or Key.NumPad0:
                    scale.SetScale(UiScale.Default);
                    e.Handled = true;
                    break;
            }
        };
        return scale;
    }

    /// <summary>
    /// Scales a small dialog that takes its height from its content (<see cref="SizeToContent.Height"/>): call it from <see cref="Window.SourceInitialized"/>,
    /// after the content is set. Sets the width and the most height for the scale; leaves the height to the content, and binds no keys.
    /// </summary>
    public static void AttachDialog(Window window, double baseWidth, double baseMaxHeight, int percent)
    {
        percent = UiScale.Nearest(percent);
        var s = UiScale.Factor(percent);
        window.SetValue(DialogScaleProperty, percent);

        var decorator = window.GetValue(DialogDecoratorProperty) as Decorator;
        if (decorator is null && window.Content is FrameworkElement content)
        {
            window.Content = null;
            decorator = new Decorator { UseLayoutRounding = true, Child = content };
            window.Content = decorator;
            window.SetValue(DialogDecoratorProperty, decorator);
        }
        if (decorator is not null) decorator.LayoutTransform = percent == UiScale.Default ? Transform.Identity : new ScaleTransform(s, s);
        TextOptions.SetTextFormattingMode(window, percent > UiScale.Default ? TextFormattingMode.Ideal : TextFormattingMode.Display);

        if (GetOffScreenSnapshot(window))
        {
            window.Width = baseWidth * s;
            window.MaxHeight = baseMaxHeight * s;
            return;
        }
        var work = WorkAreaFor(window);
        window.Width = Math.Min(baseWidth * s, Math.Max(200, work.Width - 24));
        window.MaxHeight = Math.Min(baseMaxHeight * s, Math.Max(200, work.Height - 24));
    }

    // ------------------------------------------------------------------ changing the scale

    /// <summary>
    /// Draws the window at <paramref name="percent"/> (snapped to an offered scale). With <paramref name="resizeOuter"/> the window grows or shrinks with its
    /// contents, keeping its top-left corner; either way it is kept on its monitor.
    /// </summary>
    public void SetScale(int percent, bool resizeOuter = true)
    {
        var next = UiScale.Nearest(percent);
        var old = Percent;
        Percent = next;
        ApplyLook();

        if (GetOffScreenSnapshot(_window))
        {
            _window.MinWidth = _baseMinWidth * Factor;
            _window.MinHeight = _baseMinHeight * Factor;
            if (resizeOuter && old != next && !double.IsNaN(_window.Width) && !double.IsNaN(_window.Height))
            {
                var ratio = UiScale.Factor(next) / UiScale.Factor(old);
                _window.Width *= ratio;
                _window.Height *= ratio;
            }
        }
        else
        {
            var work = WorkAreaFor(_window);
            var curW = !double.IsNaN(_window.Width) && _window.Width > 0 ? _window.Width : (_window.ActualWidth > 0 ? _window.ActualWidth : _baseMinWidth * UiScale.Factor(old));
            var curH = !double.IsNaN(_window.Height) && _window.Height > 0 ? _window.Height : (_window.ActualHeight > 0 ? _window.ActualHeight : _baseMinHeight * UiScale.Factor(old));
            var curL = !double.IsNaN(_window.Left) ? _window.Left : work.Left + Math.Max(0, (work.Width - curW) / 2);
            var curT = !double.IsNaN(_window.Top) ? _window.Top : work.Top + Math.Max(0, (work.Height - curH) / 2);
            var box = resizeOuter && _window.WindowState == WindowState.Normal && old != next
                ? UiScale.ResizeForScale(curL, curT, curW, curH, _baseMinWidth, _baseMinHeight, old, next, work)
                : UiScale.ClampToWorkArea(curL, curT, curW, curH, _baseMinWidth, _baseMinHeight, next, work);
            ApplyBox(box, work, setPosition: true);
        }

        if (old != next) _onChanged?.Invoke(Percent);
    }

    /// <summary>
    /// Changes the least size at normal scale (a window whose contents change, such as a compact view), and keeps the window on its monitor;
    /// <paramref name="targetOuterHeight"/> also sets the window's height.
    /// </summary>
    public void SetBaseMinSize(double baseMinWidth, double baseMinHeight, double? targetOuterHeight = null)
    {
        _baseMinWidth = baseMinWidth;
        _baseMinHeight = baseMinHeight;

        if (GetOffScreenSnapshot(_window))
        {
            _window.MinWidth = baseMinWidth * Factor;
            _window.MinHeight = baseMinHeight * Factor;
            if (targetOuterHeight is double th) _window.Height = Math.Max(_window.MinHeight, th);
            return;
        }

        var work = WorkAreaFor(_window);
        var w = !double.IsNaN(_window.Width) && _window.Width > 0 ? _window.Width : (_window.ActualWidth > 0 ? _window.ActualWidth : baseMinWidth * Factor);
        var h = targetOuterHeight ?? (!double.IsNaN(_window.Height) && _window.Height > 0 ? _window.Height : _window.ActualHeight);
        if (double.IsNaN(h) || h <= 0) h = baseMinHeight * Factor;
        var setPosition = !double.IsNaN(_window.Left) && !double.IsNaN(_window.Top);
        var l = setPosition ? _window.Left : work.Left;
        var t = setPosition ? _window.Top : work.Top;
        var box = UiScale.ClampToWorkArea(l, t, w, h, baseMinWidth, baseMinHeight, Percent, work);
        ApplyBox(box, work, setPosition);
    }

    // ------------------------------------------------------------------ the text-size button and menu

    /// <summary>A title-bar button ("Aa") that opens the text-size menu.</summary>
    public Button MakeTitleButton(Style iconButtonStyle)
    {
        var btn = new Button
        {
            Style = iconButtonStyle, Content = Icons.TextSize(), Width = 30, Height = 26, Margin = new Thickness(1, 0, 1, 0),
            Focusable = false, ToolTip = TitleTip(),
        };
        btn.Click += (_, _) => OpenMenu(btn);
        _titleButton = btn;
        return btn;
    }

    /// <summary>Opens the menu of offered scales under <paramref name="anchor"/>, with a tick on the current one.</summary>
    public void OpenMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Text size", IsEnabled = false });
        foreach (var p in UiScale.Presets)
        {
            var item = new MenuItem { Header = p == UiScale.Default ? $"{p}% (default)" : $"{p}%", IsCheckable = true, IsChecked = p == Percent };
            var pick = p;
            item.Click += (_, _) => SetScale(pick);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private string TitleTip() => $"Text size ({Percent}%) — Ctrl + / Ctrl -";

    // ------------------------------------------------------------------ inner workings

    /// <summary>Draws the contents at <see cref="Percent"/>: the box's transform, the text mode that stays sharp at that size, and the button's tooltip.</summary>
    private void ApplyLook()
    {
        _decorator.LayoutTransform = Percent == UiScale.Default ? Transform.Identity : new ScaleTransform(Factor, Factor);
        TextOptions.SetTextFormattingMode(_window, Percent > UiScale.Default ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        if (_titleButton is not null) _titleButton.ToolTip = TitleTip();
    }

    /// <summary>
    /// Keeps the window on its monitor at the current scale without growing it: when the window is made, when it is first laid out, and when it moves.
    /// <paramref name="keepUnsetPosition"/> leaves an unset place alone so the window's start-up location can still place it.
    /// </summary>
    private void Settle(bool keepUnsetPosition)
    {
        if (_adjusting) return;
        if (GetOffScreenSnapshot(_window))
        {
            _window.MinWidth = _baseMinWidth * Factor;
            _window.MinHeight = _baseMinHeight * Factor;
            return;
        }
        var work = WorkAreaFor(_window);
        var w = !double.IsNaN(_window.Width) && _window.Width > 0 ? _window.Width : (_window.ActualWidth > 0 ? _window.ActualWidth : _baseMinWidth * Factor);
        var h = !double.IsNaN(_window.Height) && _window.Height > 0 ? _window.Height : (_window.ActualHeight > 0 ? _window.ActualHeight : _baseMinHeight * Factor);
        var unset = double.IsNaN(_window.Left) || double.IsNaN(_window.Top);
        var l = !double.IsNaN(_window.Left) ? _window.Left : work.Left + Math.Max(0, (work.Width - w) / 2);
        var t = !double.IsNaN(_window.Top) ? _window.Top : work.Top + Math.Max(0, (work.Height - h) / 2);
        var box = UiScale.ClampToWorkArea(l, t, w, h, _baseMinWidth, _baseMinHeight, Percent, work);
        ApplyBox(box, work, setPosition: !(keepUnsetPosition && unset));
    }

    /// <summary>Writes a worked-out place and size to the window (only what changed), guarded so the moves it makes are not taken for the player's.</summary>
    private void ApplyBox(WindowBox box, WorkArea work, bool setPosition)
    {
        _adjusting = true;
        try
        {
            SetIfChanged(Window.MaxWidthProperty, work.Width);
            SetIfChanged(Window.MaxHeightProperty, work.Height);
            SetIfChanged(Window.MinWidthProperty, box.MinWidth);
            SetIfChanged(Window.MinHeightProperty, box.MinHeight);
            if (_window.WindowState != WindowState.Normal) return;
            SetIfChanged(Window.WidthProperty, box.Width);
            SetIfChanged(Window.HeightProperty, box.Height);
            if (!setPosition) return;
            SetIfChanged(Window.LeftProperty, box.Left);
            SetIfChanged(Window.TopProperty, box.Top);
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void SetIfChanged(DependencyProperty property, double value)
    {
        var current = (double)_window.GetValue(property);
        if (double.IsNaN(current) || double.IsInfinity(current) || Math.Abs(current - value) > 0.5) _window.SetValue(property, value);
    }

    // ------------------------------------------------------------------ menus and tooltips

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var target = menu.PlacementTarget ?? (Mouse.DirectlyOver as DependencyObject);
        var owner = (target is not null ? Window.GetWindow(target) : null)
                    ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var percent = ScaleOf(owner);
        menu.SetValue(PopupScaleProperty, percent);
        ScalePopup(menu, percent);
    }

    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip) return;
        var target = tip.PlacementTarget ?? (Mouse.DirectlyOver as DependencyObject);
        var owner = target is not null ? Window.GetWindow(target) : null;
        var percent = ScaleOf(owner);
        // an item inside an open context menu lives in the menu's own popup: take the scale the menu was drawn at
        if ((owner is null || percent == UiScale.Default) && MenuScaleAbove(target) is int menuScale) percent = menuScale;
        ScalePopup(tip, percent);
    }

    /// <summary>The scale of the nearest open context menu above <paramref name="start"/>, if any.</summary>
    private static int? MenuScaleAbove(DependencyObject? start)
    {
        var node = start;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is ContextMenu menu)
            {
                var scale = (int)menu.GetValue(PopupScaleProperty);
                return scale > 0 ? scale : null;
            }
            var parent = LogicalTreeHelper.GetParent(node);
            if (parent is null && node is Visual or System.Windows.Media.Media3D.Visual3D) parent = VisualTreeHelper.GetParent(node);
            node = parent;
        }
        return null;
    }

    private static void ScalePopup(FrameworkElement popup, int percent)
    {
        var s = UiScale.Factor(percent);
        popup.LayoutTransform = percent == UiScale.Default ? Transform.Identity : new ScaleTransform(s, s);
        TextOptions.SetTextFormattingMode(popup, percent > UiScale.Default ? TextFormattingMode.Ideal : TextFormattingMode.Display);
    }

    // ------------------------------------------------------------------ monitors

    /// <summary>The work area (screen less taskbar) of the monitor a window is on, or would be on, in device-independent units.</summary>
    public static WorkArea WorkAreaFor(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero && WorkAreaOf(monitor, PixelsPerDip(window)) is { } found) return found;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or EntryPointNotFoundException or DllNotFoundException)
        {
            // fall through to the place the window means to be
        }

        var fallback = SystemParameters.WorkArea;
        var width = !double.IsNaN(window.Width) && window.Width > 0 ? window.Width : (window.ActualWidth > 0 ? window.ActualWidth : 1);
        var height = !double.IsNaN(window.Height) && window.Height > 0 ? window.Height : (window.ActualHeight > 0 ? window.ActualHeight : 1);
        var left = !double.IsNaN(window.Left) ? window.Left : fallback.Left + Math.Max(0, (fallback.Width - width) / 2);
        var top = !double.IsNaN(window.Top) ? window.Top : fallback.Top + Math.Max(0, (fallback.Height - height) / 2);
        return WorkAreaForRect(left, top, width, height, window);
    }

    /// <summary>The work area of the monitor nearest a rectangle given in device-independent units (sized by <paramref name="dpiReference"/>'s DPI).</summary>
    public static WorkArea WorkAreaForRect(double left, double top, double width, double height, Visual? dpiReference = null)
    {
        try
        {
            var dpi = dpiReference is not null ? PixelsPerDip(dpiReference) : 1.0;
            if (double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && double.IsFinite(height))
            {
                var rect = new RECT
                {
                    Left = (int)Math.Round(left * dpi),
                    Top = (int)Math.Round(top * dpi),
                    Right = (int)Math.Round((left + Math.Max(1, width)) * dpi),
                    Bottom = (int)Math.Round((top + Math.Max(1, height)) * dpi),
                };
                var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero && WorkAreaOf(monitor, dpi) is { } found) return found;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or EntryPointNotFoundException or DllNotFoundException or OverflowException)
        {
            // fall through to the main monitor
        }
        var wa = SystemParameters.WorkArea;
        return new WorkArea(wa.Left, wa.Top, wa.Width, wa.Height);
    }

    private static double PixelsPerDip(Visual visual)
    {
        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(visual).PixelsPerDip; }
        catch (InvalidOperationException) { dpi = 1.0; }
        return dpi > 0 && double.IsFinite(dpi) ? dpi : 1.0;
    }

    private static WorkArea? WorkAreaOf(IntPtr monitor, double dpi)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return null;
        var r = info.rcWork;
        if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return null;
        return new WorkArea(r.Left / dpi, r.Top / dpi, (r.Right - r.Left) / dpi, (r.Bottom - r.Top) / dpi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize; // 40
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
}
