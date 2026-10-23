using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Win11StageManager.Interop;

namespace Win11StageManager;

/// <summary>
/// The strip on the left edge of the primary monitor. It shows one tilted 3D card per side stage,
/// drawn from snapshots of its windows. By default it reserves its column of the screen (like the
/// taskbar does), so maximized windows sit beside it instead of covering it.
/// </summary>
public partial class StripWindow : Window
{
    const double StripWidthDip = 200;
    const double CardWidthDip = 168;
    const double ThumbHeightDip = 100;
    const double LabelHeightDip = 28;
    const double CardGapDip = 14;
    const double LeftMarginDip = 16;
    const int MaxCards = 6;
    const int HotkeyId = 0x5347;
    const uint HotkeyKey = 0x53; // S
    const double RestAngle = 26;  // degrees the stack is swung back at rest
    const double HoverAngle = 12;

    static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    // Alpha 1/255: invisible, but non-zero so the layered window still receives clicks on the card.
    static readonly Brush RestBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF)));

    readonly List<Card> _cards = new();
    readonly DispatcherTimer _timer;
    Card? _hovered;
    StageController? _controller;
    IntPtr _hwnd;
    double _scale = 1;
    RECT _monitor;
    RECT _work;
    RECT _band;          // where the strip sits when fully shown
    RECT _appliedBounds;
    int _offsetPx = int.MinValue; // 0 = fully shown, -width = fully hidden
    bool _peeking;
    bool _hasCards;
    bool _reserveSpace = true;
    bool _appBarRegistered;
    bool _appBarDirty = true;
    int _reservedLeft;
    uint _appBarMessage;
    bool _rebuildQueued;
    string _rebuildReason = "startup";
    string? _lastLayout;

    // Dragging a card out of the strip.
    const int DragThresholdPx = 6;
    Card? _pressedCard;
    POINT _pressPoint;
    int _pressKeys;
    DragGhost? _ghost;

    // Dragging an on-stage window toward the strip.
    TrackedWindow? _movingWindow;
    bool _dropZoneActive;

    static readonly Brush DropZoneIdle = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    static readonly Brush DropZoneHot = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));

    public StripWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        // DpiChanged can be raised in the middle of WPF's layout pass (e.g. while the window is first
        // shown), so it must not touch the visual tree directly.
        DpiChanged += (_, _) =>
        {
            _appBarDirty = true;
            RequestRebuild("DPI changed");
        };
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            try { Tick(); }
            catch (Exception ex) { Log.Error("Strip tick", ex); }
        };
    }

    public event Action? HotkeyPressed;

    public bool HotkeyRegistered { get; private set; }

    /// <summary>When true, maximized windows stop at the strip's edge instead of covering it.</summary>
    public bool ReserveSpace
    {
        get => _reserveSpace;
        set
        {
            if (_reserveSpace == value) return;
            _reserveSpace = value;
            _appBarDirty = true;
        }
    }

    internal void Attach(StageController controller)
    {
        _controller = controller;
        controller.StagesChanged += () => RequestRebuild("stages changed");
        controller.WindowMoveStarted += window => _movingWindow = window;
        controller.WindowMoveEnded += OnWindowMoveEnded;
        new WindowInteropHelper(this).EnsureHandle();
        _timer.Start();
    }

    /// <summary>
    /// Rebuilds the cards once the dispatcher is idle. Rebuilding synchronously could replace the canvas
    /// children while WPF is still measuring them, which throws and leaves the strip unable to draw.
    /// Several requests in a row collapse into a single rebuild.
    /// </summary>
    void RequestRebuild(string reason)
    {
        _rebuildReason = reason;
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _rebuildQueued = false;
            try { Rebuild(); }
            catch (Exception ex) { Log.Error("Strip rebuild", ex); }
        }, DispatcherPriority.Background);
    }

    /// <summary>Gives the reserved column back to other windows. Safe to call from exit handlers.</summary>
    public void ReleaseScreenSpace()
    {
        if (!_appBarRegistered) return;
        var data = NewAppBarData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        _appBarRegistered = false;
        _appBarDirty = true;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Never take focus and never appear in Alt+Tab.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        // Per-pixel transparency comes from AllowsTransparency in the XAML (a layered window).
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.AddHook(WndProc);

        _appBarMessage = Native.RegisterWindowMessage("Win11StageManager.AppBar");
        HotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId,
            Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, HotkeyKey);
        Log.Info($"Strip created; hotkey registered = {HotkeyRegistered}");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBarMessage != 0 && (uint)msg == _appBarMessage)
        {
            // The taskbar or another app bar moved; renegotiate our column.
            if (wParam.ToInt32() == Native.ABN_POSCHANGED) _appBarDirty = true;
            handled = true;
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);

            // Mouse input is handled here rather than through WPF events, which don't fire reliably
            // in a window that never becomes active.
            case Native.WM_LBUTTONDOWN:
            {
                handled = true;
                var card = HitTest(ClientPoint(lParam));
                if (card != null)
                {
                    // Capture so we keep getting mouse moves after the pointer leaves the strip.
                    _pressedCard = card;
                    _pressKeys = (int)wParam.ToInt64();
                    Native.GetCursorPos(out _pressPoint);
                    Native.SetCapture(_hwnd);
                }
                break;
            }
            case Native.WM_MOUSEMOVE when _pressedCard != null:
                handled = true;
                OnPressedMouseMove();
                break;
            case Native.WM_LBUTTONUP:
                handled = true;
                OnMouseReleased(ClientPoint(lParam));
                break;
            case Native.WM_CAPTURECHANGED when _pressedCard != null:
                // Something else took the mouse (e.g. a system dialog): abandon the drag.
                CancelCardDrag();
                break;
            case Native.WM_SETCURSOR when _hovered != null:
                handled = true;
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_HAND)));
                return new IntPtr(1);

            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                HotkeyPressed?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        CancelCardDrag();
        ReleaseScreenSpace();
        if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, HotkeyId);
        base.OnClosed(e);
    }

    void Rebuild()
    {
        if (_hwnd == IntPtr.Zero || _controller is not { Enabled: true })
        {
            ResetCards();
            Root.Children.Clear();
            _lastLayout = null;
            return;
        }

        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        UpdateBand();

        double heightDip = _band.Height / _scale;
        double cardHeight = ThumbHeightDip + LabelHeightDip;
        int fit = (int)((heightDip - 2 * CardGapDip) / (cardHeight + CardGapDip));
        var stages = _controller.SideStages.Take(Math.Clamp(fit, 1, MaxCards)).ToList();

        // Recreating the cards resets hover (the stack snaps back and swings again, which flickers) and
        // orphans a card that's mid-click. So if nothing visible has changed, keep the cards as they are.
        string layout = DescribeLayout(stages);
        if (layout == _lastLayout) return;
        _lastLayout = layout;

        Log.Info($"Rebuild ({_rebuildReason}): {stages.Count} cards, band = ({_band.Left},{_band.Top})-({_band.Right},{_band.Bottom}), " +
                 $"scale = {_scale}, space reserved = {_appBarRegistered}");
        ResetCards();

        // Build everything off to the side first. Capturing snapshots waits on other apps, and Windows can
        // deliver messages (and so a layout pass) to us during that wait; the live canvas must not be
        // half-built when it happens. Then swap all the children in one go.
        var elements = new List<UIElement>();
        double total = stages.Count * cardHeight + Math.Max(0, stages.Count - 1) * CardGapDip;
        double y = Math.Max(CardGapDip, (heightDip - total) / 2); // centered vertically, like Stage Manager
        foreach (var stage in stages)
        {
            AddCard(elements, stage, LeftMarginDip, y);
            y += cardHeight + CardGapDip;
        }

        Root.Children.Clear();
        foreach (var element in elements) Root.Children.Add(element);
        _hasCards = stages.Count > 0;
    }

    void ResetCards()
    {
        _cards.Clear();
        _hovered = null;
        _hasCards = false;
    }

    /// <summary>Everything that affects what the strip draws: position, scale, and each card's windows.</summary>
    string DescribeLayout(List<Stage> stages)
    {
        var parts = new List<string> { $"{_band.Left},{_band.Top},{_band.Right},{_band.Bottom}@{_scale}" };
        foreach (var stage in stages)
        {
            var shown = stage.Windows.OrderByDescending(w => w.LastActive).Take(3)
                .Select(w => $"{w.Handle.ToInt64():X}{(w.Snapshot != null ? "s" : "")}");
            parts.Add($"{RuntimeHelpers.GetHashCode(stage)}:{stage.Windows.Count}:{string.Join(",", shown)}");
        }
        return string.Join("|", parts);
    }

    void AddCard(List<UIElement> elements, Stage stage, double x, double y)
    {
        double cardLeft = x - 6, cardTop = y - 4;
        double cardWidth = CardWidthDip + 12, cardHeight = ThumbHeightDip + LabelHeightDip + 8;

        // Visual only: clicks and hover are hit-tested in WndProc against Card.Bounds.
        var highlight = new Border
        {
            Width = cardWidth,
            Height = cardHeight,
            CornerRadius = new CornerRadius(12),
            Background = RestBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(highlight, cardLeft);
        Canvas.SetTop(highlight, cardTop);
        elements.Add(highlight);

        var card = new Card(stage, highlight,
            new RECT(Px(cardLeft), Px(cardTop), Px(cardLeft + cardWidth), Px(cardTop + cardHeight)));
        _cards.Add(card);

        var windows = stage.Windows.OrderByDescending(w => w.LastActive).ToList();
        var stack = windows.Take(3).Reverse().ToList(); // back to front

        // Live DWM thumbnails of minimized windows often render nothing at all, so every card is drawn
        // from images we own: the snapshot taken before minimizing, or an app-icon placeholder.
        var images = stack.Select(ImageFor).ToList();
        var viewport = CreatePerspectiveStack(images, card.Rotations);
        Canvas.SetLeft(viewport, x);
        Canvas.SetTop(viewport, y);
        elements.Add(viewport);

        elements.Add(CreateLabel(windows, x, y + ThumbHeightDip + 4));
    }

    static BitmapSource ImageFor(TrackedWindow window)
    {
        if (window.Snapshot == null && !Native.IsIconic(window.Handle))
        {
            try { window.Snapshot = WindowSnapshot.Capture(window.Handle); }
            catch (Exception ex) { Log.Error($"Snapshot of {window.AppName}", ex); }
        }
        return window.Snapshot ?? WindowSnapshot.Placeholder(window.AppKey, window.AppName, window.Icon);
    }

    /// <summary>
    /// Builds the macOS-style tilted stack: each window is a textured plane in 3D, swung back around its
    /// left edge so the right side recedes, with older windows stacked further back and up.
    /// </summary>
    static Viewport3D CreatePerspectiveStack(List<BitmapSource> backToFront, List<AxisAngleRotation3D> rotations)
    {
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White));

        int count = backToFront.Count;
        for (int i = 0; i < count; i++)
        {
            int depth = count - 1 - i; // 0 = front
            var image = backToFront[i];
            double aspect = (double)image.PixelWidth / image.PixelHeight;

            // Fit into a 1.0 x 0.62 box in scene units, shrinking windows further back.
            double w = Math.Min(1.0, 0.62 * aspect);
            double h = w / aspect;
            double shrink = 1 - 0.12 * depth;
            w *= shrink;
            h *= shrink;

            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(-w / 2, -h / 2, 0), new(w / 2, -h / 2, 0), new(w / 2, h / 2, 0), new(-w / 2, h / 2, 0),
                },
                TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
            };

            var brush = new ImageBrush(image);
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            var material = new DiffuseMaterial(brush);

            var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), RestAngle);
            rotations.Add(rotation);

            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(rotation, new Point3D(-w / 2, 0, 0)));
            transform.Children.Add(new TranslateTransform3D(-0.12 + depth * 0.14, -0.04 + depth * 0.07, -depth * 0.3));

            // Added back-to-front so the rounded (transparent) corners blend over the windows behind.
            scene.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material, Transform = transform });
        }

        var viewport = new Viewport3D
        {
            Width = CardWidthDip,
            Height = ThumbHeightDip,
            ClipToBounds = false,
            IsHitTestVisible = false,
            Camera = new PerspectiveCamera(new Point3D(0.05, 0.04, 2.4), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 32),
            Effect = new DropShadowEffect { ShadowDepth = 3, Direction = 270, BlurRadius = 14, Opacity = 0.45 },
        };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        return viewport;
    }

    void SetHovered(Card? card)
    {
        if (card == _hovered) return;
        if (_hovered != null) AnimateCard(_hovered, hovered: false);
        _hovered = card;
        if (card != null) AnimateCard(card, hovered: true);
    }

    static void AnimateCard(Card card, bool hovered)
    {
        card.Highlight.Background = hovered ? HoverBrush : RestBrush;
        // On hover the stack swings toward you, like Stage Manager does.
        var swing = new DoubleAnimation(hovered ? HoverAngle : RestAngle, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        foreach (var rotation in card.Rotations)
            rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, swing);
    }

    Card? HitTest(int clientX, int clientY) =>
        _cards.FirstOrDefault(c => clientX >= c.Bounds.Left && clientX < c.Bounds.Right &&
                                   clientY >= c.Bounds.Top && clientY < c.Bounds.Bottom);

    sealed class Card
    {
        public Card(Stage stage, Border highlight, RECT bounds)
        {
            Stage = stage;
            Highlight = highlight;
            Bounds = bounds;
        }

        public Stage Stage { get; }
        public Border Highlight { get; }
        public RECT Bounds { get; } // physical pixels, relative to the strip window
        public List<AxisAngleRotation3D> Rotations { get; } = new();
    }

    static UIElement CreateLabel(List<TrackedWindow> windows, double x, double y)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        foreach (var app in windows.DistinctBy(w => w.AppKey).Take(3))
        {
            if (app.Icon is { } icon)
                panel.Children.Add(new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 5, 0) });
        }

        var names = windows.Select(w => w.AppName).Distinct().ToList();
        string text = names.Count > 1 ? $"{names[0]} +{names.Count - 1}"
            : windows.Count > 1 ? $"{names[0]} ({windows.Count})"
            : names[0];

        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = CardWidthDip - panel.Children.Count * 23,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9 },
        });

        Canvas.SetLeft(panel, x);
        Canvas.SetTop(panel, y);
        return panel;
    }

    static POINT ClientPoint(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return new POINT { X = (short)(packed & 0xFFFF), Y = (short)((packed >> 16) & 0xFFFF) };
    }

    Card? HitTest(POINT client) => HitTest(client.X, client.Y);

    bool IsOverStrip(POINT screen) =>
        screen.X >= _band.Left && screen.X < _band.Right && screen.Y >= _band.Top && screen.Y < _band.Bottom;

    /// <summary>Starts the drag once the pointer has moved far enough, then keeps the preview under it.</summary>
    void OnPressedMouseMove()
    {
        if ((Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0)
        {
            CancelCardDrag();
            return;
        }

        Native.GetCursorPos(out POINT pointer);
        if (_ghost == null)
        {
            if (Math.Abs(pointer.X - _pressPoint.X) < DragThresholdPx &&
                Math.Abs(pointer.Y - _pressPoint.Y) < DragThresholdPx)
                return;

            var stage = _pressedCard!.Stage;
            var front = stage.Front;
            if (front == null) return;
            var windows = stage.Windows.OrderByDescending(w => w.LastActive).ToList();
            string caption = windows.Count > 1 ? $"{front.AppName} (+{windows.Count - 1})" : front.AppName;

            _ghost = new DragGhost(ImageFor(front), caption);
            _ghost.Show();
            Log.Info($"Card drag started: {front.AppName}");
        }

        _ghost.MoveTo(pointer);
        _ghost.SetWillDrop(!IsOverStrip(pointer));
    }

    /// <summary>Left button released: either a plain click on a card, or the end of a card drag.</summary>
    void OnMouseReleased(POINT client)
    {
        var card = _pressedCard;
        bool dragged = _ghost != null;
        EndCardPress();
        if (card == null) return;

        if (!dragged)
        {
            // Compare stages, not card objects: the card may have been rebuilt between press and release.
            if (HitTest(client)?.Stage == card.Stage)
                OnCardClicked(card, (_pressKeys & Native.MK_SHIFT) != 0, (_pressKeys & Native.MK_CONTROL) != 0);
            else
                Log.Info("Click ignored: released outside the card it started on");
            return;
        }

        Native.GetCursorPos(out POINT drop);
        if (IsOverStrip(drop))
        {
            Log.Info("Card drag cancelled (released over the strip)");
            return;
        }

        Log.Info($"Card dropped on stage: {card.Stage.Front?.AppName} at ({drop.X},{drop.Y})");
        Dispatcher.InvokeAsync(() =>
        {
            try { _controller?.AddToCurrentStage(card.Stage, drop); }
            catch (Exception ex) { Log.Error("Card drop", ex); }
        });
    }

    void CancelCardDrag()
    {
        if (_pressedCard == null) return;
        if (_ghost != null) Log.Info("Card drag cancelled");
        EndCardPress();
    }

    void EndCardPress()
    {
        // Clear state before releasing capture, because ReleaseCapture sends WM_CAPTURECHANGED.
        _pressedCard = null;
        var ghost = _ghost;
        _ghost = null;
        ghost?.Close();
        Native.ReleaseCapture();
    }

    /// <summary>An on-stage window was dropped. If the pointer is over the strip, send it there.</summary>
    void OnWindowMoveEnded(TrackedWindow window, MoveStart start)
    {
        _movingWindow = null;
        Native.GetCursorPos(out POINT pointer);
        if (!IsOverStrip(pointer) || !Native.GetWindowRect(window.Handle, out RECT now)) return;

        // Esc during a drag puts the window back where it was: treat that as "never moved".
        // A changed size means it was resized, not moved, unless it started out maximized.
        bool moved = !now.Equals(start.Bounds);
        bool resized = now.Width != start.Bounds.Width || now.Height != start.Bounds.Height;
        if (!moved || (resized && !start.WasMaximized)) return;

        Log.Info($"Window dropped on strip: {window.AppName}");
        try { _controller?.SendToStrip(window, start); }
        catch (Exception ex) { Log.Error("Window drop", ex); }
    }

    void UpdateDropZone(bool show, bool hot)
    {
        DropZone.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && hot != _dropZoneActive) DropZoneFill.Fill = hot ? DropZoneHot : DropZoneIdle;
        _dropZoneActive = show && hot;
    }

    void OnCardClicked(Card card, bool shift, bool ctrl)
    {
        if (_controller == null) return;
        Log.Info($"Card clicked: {card.Stage.Front?.AppName} (shift={shift}, ctrl={ctrl})");

        // Defer so the strip isn't rebuilt while we're still inside its window procedure.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (ctrl) _controller.Ungroup(card.Stage);
                else if (shift) _controller.AddToCurrentStage(card.Stage);
                else _controller.ActivateStage(card.Stage);
            }
            catch (Exception ex)
            {
                Log.Error("Card click", ex);
            }
        });
    }

    void UpdateMonitor()
    {
        var primary = Native.MonitorFromPoint(new POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(primary, ref info)) return;
        _monitor = info.rcMonitor;
        _work = info.rcWork;
    }

    /// <summary>Works out the strip's column, registering it as a reserved app bar when enabled.</summary>
    void UpdateBand()
    {
        UpdateMonitor();
        int widthPx = Px(StripWidthDip);
        bool wantAppBar = _reserveSpace && _controller is { Enabled: true };

        if (!wantAppBar)
        {
            ReleaseScreenSpace();
            UpdateMonitor();
        }
        else
        {
            if (!_appBarRegistered)
            {
                var data = NewAppBarData();
                data.uCallbackMessage = _appBarMessage;
                _appBarRegistered = Native.SHAppBarMessage(Native.ABM_NEW, ref data) != UIntPtr.Zero;
                _appBarDirty = true;
                if (!_appBarRegistered) Log.Info("Could not reserve screen space; falling back to overlay mode");
            }

            if (_appBarRegistered && _appBarDirty)
            {
                var data = NewAppBarData();
                data.uEdge = Native.ABE_LEFT;
                data.rc = _monitor;
                data.rc.Right = data.rc.Left + widthPx;
                Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data);
                data.rc.Right = data.rc.Left + widthPx; // the shell may shift Left if something else is docked there
                Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
                _reservedLeft = data.rc.Left;
                _appBarDirty = false;
                UpdateMonitor();
            }
        }

        int left = _appBarRegistered ? _reservedLeft : _work.Left;
        _band = new RECT(left, _work.Top, left + widthPx, _work.Bottom);
    }

    APPBARDATA NewAppBarData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    /// <summary>Runs every 30 ms: decides whether the strip should be visible and animates the slide.</summary>
    void Tick()
    {
        if (_hwnd == IntPtr.Zero) return;

        var previousBand = _band;
        UpdateBand();
        if (previousBand.Width > 0 && !previousBand.Equals(_band)) RequestRebuild("strip position changed");

        int widthPx = _band.Width;
        bool wantVisible = false;
        // Turning Stage Manager off mid-drag removes the hooks, so the drag's end is never reported.
        if (_controller is not { Enabled: true }) _movingWindow = null;
        bool windowDrag = _movingWindow != null;
        bool cardDrag = _pressedCard != null;
        Native.GetCursorPos(out POINT cursor);

        if (_controller is { Enabled: true } && (_hasCards || windowDrag || cardDrag))
        {
            bool inRows = cursor.Y >= _band.Top && cursor.Y < _band.Bottom;
            bool atEdge = inRows && cursor.X >= _monitor.Left && cursor.X <= _band.Left + 2;
            bool overStrip = inRows && cursor.X >= _monitor.Left && cursor.X < _band.Right;

            // Touching the left edge reveals the strip; it stays while the pointer is over it.
            // It also stays out for the whole of a drag, in either direction: hiding it mid-drag
            // would drop the mouse capture, and a dragged window needs somewhere to land.
            _peeking = atEdge || (_peeking && overStrip && _offsetPx > -widthPx);
            wantVisible = _peeking || windowDrag || cardDrag || !IsStripCovered();
        }
        else
        {
            _peeking = false;
        }

        UpdateDropZone(windowDrag, windowDrag && IsOverStrip(cursor));

        int target = wantVisible ? 0 : -widthPx;
        if (_offsetPx == int.MinValue) _offsetPx = target;
        if (_offsetPx != target)
        {
            int step = Math.Max(1, widthPx / 6);
            _offsetPx = _offsetPx < target ? Math.Min(target, _offsetPx + step) : Math.Max(target, _offsetPx - step);
        }

        bool visible = _offsetPx > -widthPx;
        var bounds = new RECT(_band.Left + _offsetPx, _band.Top, _band.Right + _offsetPx, _band.Bottom);

        if (!visible)
        {
            SetHovered(null);
            if (IsVisible)
            {
                Hide();
                Log.Info($"Strip hidden (cards = {_hasCards}, covered = {_hasCards && !_peeking && IsStripCovered()})");
            }
            return;
        }

        if (!IsVisible)
        {
            // Position first so WPF's first layout pass already has the final size.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                Native.SWP_NOACTIVATE);
            Show();
            Log.Info($"Strip shown at ({bounds.Left},{bounds.Top}) {bounds.Width}x{bounds.Height}");
            _appliedBounds = default; // WPF may apply its own size on Show; always reposition after it
        }
        if (!bounds.Equals(_appliedBounds))
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                Native.SWP_NOACTIVATE);
            _appliedBounds = bounds;
        }

        Native.GetCursorPos(out POINT pointer);
        SetHovered(_movingWindow != null ? null : HitTest(pointer.X - _appliedBounds.Left, pointer.Y - _appliedBounds.Top));
    }

    /// <summary>True when an on-stage window (or a fullscreen app) has been moved over the strip.</summary>
    bool IsStripCovered()
    {
        var current = _controller?.Current;
        if (current != null)
        {
            foreach (var window in current.Windows)
            {
                if (!Native.IsIconic(window.Handle) && WindowFilter.GetFrameBounds(window.Handle).Intersects(_band, 4))
                    return true;
            }
        }

        var foreground = Native.GetForegroundWindow();
        return foreground != _hwnd && WindowFilter.IsStageable(foreground) && !Native.IsIconic(foreground) &&
               WindowFilter.GetFrameBounds(foreground).Intersects(_band, 4);
    }

    int Px(double dip) => (int)Math.Round(dip * _scale);

    static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
