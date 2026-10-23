using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Win11StageManager.Interop;

namespace Win11StageManager;

/// <summary>Where a window was, and whether it was maximized, when the user started dragging it.</summary>
internal readonly record struct MoveStart(RECT Bounds, bool WasMaximized);

/// <summary>
/// Owns the stage model and keeps it in sync with what the user does in Windows.
/// The "current" stage is the set of windows on screen; every other stage is minimized
/// and shown as a thumbnail card in the strip.
/// </summary>
internal sealed class StageController : IDisposable
{
    // Our own restores/minimizes make Windows bounce focus around for a moment;
    // foreground changes during this window are ignored so they don't trigger a switch back.
    const int SwitchSettleMs = 600;

    readonly Dictionary<IntPtr, TrackedWindow> _windows = new();
    readonly List<Stage> _stages = new();
    readonly HashSet<IntPtr> _expectMinimize = new();
    readonly HashSet<IntPtr> _expectRestore = new();
    readonly List<IntPtr> _hooks = new();
    readonly Native.WinEventDelegate _winEventProc; // kept in a field so the GC doesn't collect it
    readonly Dispatcher _dispatcher;
    long _clock;
    long _ignoreForegroundUntil;

    public StageController()
    {
        _winEventProc = OnWinEvent;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool Enabled { get; private set; }
    public Stage? Current { get; private set; }

    public event Action? StagesChanged;
    public event Action<bool>? EnabledChanged;

    /// <summary>The user started dragging an on-stage window by its title bar (or resizing it).</summary>
    public event Action<TrackedWindow>? WindowMoveStarted;

    /// <summary>That drag finished; carries where the window was when it started.</summary>
    public event Action<TrackedWindow, MoveStart>? WindowMoveEnded;

    TrackedWindow? _movingWindow;
    MoveStart _moveStart;

    /// <summary>Stages that belong in the strip, most recently used first.</summary>
    public IReadOnlyList<Stage> SideStages =>
        _stages.Where(s => s != Current && s.Windows.Count > 0).OrderByDescending(s => s.LastActive).ToList();

    public void Toggle()
    {
        if (Enabled) Disable();
        else Enable();
    }

    public void Enable()
    {
        if (Enabled) return;
        Enabled = true;

        // EnumWindows walks the z-order top-down, so track bottom-up to make the top window the most recent.
        // Windows that are already minimized stay in the taskbar, the same way macOS leaves them in the Dock.
        var handles = WindowFilter.EnumerateTopLevel()
            .Where(h => WindowFilter.IsStageable(h) && !Native.IsIconic(h))
            .ToList();
        for (int i = handles.Count - 1; i >= 0; i--) Track(handles[i]);

        var front = ResolveTracked(Native.GetForegroundWindow(), allowNew: false);
        Current = front?.Stage ?? _stages.OrderByDescending(s => s.LastActive).FirstOrDefault();
        if (Current != null) Current.LastActive = ++_clock;

        foreach (var stage in _stages.Where(s => s != Current))
            foreach (var window in stage.Windows)
                Minimize(window);

        Log.Info($"Enabled: {_windows.Count} windows in {_stages.Count} stages; current = {Current?.Front?.AppName ?? "none"}");
        InstallHooks();
        EnabledChanged?.Invoke(true);
        StagesChanged?.Invoke();
    }

    public void Disable()
    {
        if (!Enabled) return;
        Enabled = false;
        RemoveHooks();

        var focus = Current?.Front?.Handle ?? IntPtr.Zero;
        RestoreMinimizedWindows();
        if (focus != IntPtr.Zero) WindowActions.ForceForeground(focus);

        _windows.Clear();
        _stages.Clear();
        _expectMinimize.Clear();
        _expectRestore.Clear();
        _movingWindow = null;
        Current = null;

        EnabledChanged?.Invoke(false);
        StagesChanged?.Invoke();
    }

    /// <summary>Brings back every window we minimized. Safe to call from crash/exit handlers.</summary>
    public void RestoreMinimizedWindows()
    {
        // Oldest first, so the most recently used window ends up on top.
        foreach (var window in _windows.Values.Where(w => w.MinimizedByUs).OrderBy(w => w.LastActive).ToList())
        {
            if (Native.IsWindow(window.Handle) && Native.IsIconic(window.Handle))
                Native.ShowWindowAsync(window.Handle, Native.SW_RESTORE);
            window.MinimizedByUs = false;
        }
    }

    /// <summary>Click on a strip card: make that stage the one on screen.</summary>
    public void ActivateStage(Stage stage)
    {
        if (!_stages.Contains(stage)) return;
        SwitchTo(stage, focus: null);
    }

    /// <summary>
    /// Shift+click, or dragging a card out of the strip: pull its windows into the current stage.
    /// When dropped, the stage's front window is moved under the pointer.
    /// </summary>
    public void AddToCurrentStage(Stage stage, POINT? dropAt = null)
    {
        if (!_stages.Contains(stage)) return;
        var dropped = stage.Front;

        if (Current == null || Current == stage)
        {
            ActivateStage(stage);
        }
        else
        {
            var target = Current;
            _stages.Remove(stage);
            foreach (var window in stage.Windows.OrderBy(w => w.LastActive).ToList())
            {
                target.Add(window);
                window.LastActive = ++_clock;
            }
            target.LastActive = ++_clock;

            _ignoreForegroundUntil = Environment.TickCount64 + SwitchSettleMs;
            foreach (var window in target.Windows.OrderBy(w => w.LastActive)) Restore(window);
            if (target.Front is { } front) WindowActions.ForceForeground(front.Handle);
            StagesChanged?.Invoke();
        }

        if (dropAt is { } pointer && dropped != null) WindowActions.PlaceAt(dropped.Handle, pointer);
    }

    /// <summary>A window dragged onto the strip: it leaves the stage and becomes its own card.</summary>
    public void SendToStrip(TrackedWindow window, MoveStart start)
    {
        var stage = window.Stage;
        if (stage == null || stage != Current || !Native.IsWindow(window.Handle)) return;

        // Put it back the way it was before the drag, so it doesn't reopen on top of the strip later.
        if (start.WasMaximized)
        {
            Native.ShowWindow(window.Handle, Native.SW_MAXIMIZE);
        }
        else
        {
            var r = start.Bounds;
            Native.SetWindowPos(window.Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        var single = new Stage();
        single.Add(window); // also takes it out of its old stage
        window.LastActive = ++_clock;
        single.LastActive = _clock;
        _stages.Add(single);
        if (stage.Windows.Count == 0)
        {
            _stages.Remove(stage);
            Current = null;
        }

        _ignoreForegroundUntil = Environment.TickCount64 + SwitchSettleMs;
        Minimize(window);
        if (Current?.Front is { } next) WindowActions.ForceForeground(next.Handle);
        StagesChanged?.Invoke();
    }

    /// <summary>Ctrl+click on a strip card: split a group back into one stage per window.</summary>
    public void Ungroup(Stage stage)
    {
        if (!_stages.Contains(stage) || stage.Windows.Count < 2) return;

        var front = stage.Front!;
        foreach (var window in stage.Windows.Where(w => w != front).ToList())
        {
            var single = new Stage { LastActive = window.LastActive };
            single.Add(window);
            _stages.Add(single);
        }
        StagesChanged?.Invoke();
    }

    void SwitchTo(Stage target, TrackedWindow? focus)
    {
        var previous = Current;
        Current = target;
        target.LastActive = ++_clock;
        _ignoreForegroundUntil = Environment.TickCount64 + SwitchSettleMs;

        // Restore back-to-front, then focus, so the z-order matches how the stage was left.
        foreach (var window in target.Windows.OrderBy(w => w.LastActive)) Restore(window);
        var focusWindow = focus ?? target.Front;
        if (focusWindow != null)
        {
            focusWindow.LastActive = ++_clock;
            WindowActions.ForceForeground(focusWindow.Handle);
        }

        if (previous != null && previous != target)
            foreach (var window in previous.Windows)
                Minimize(window);

        StagesChanged?.Invoke();
    }

    void Restore(TrackedWindow window)
    {
        window.MinimizedByUs = false;
        if (!Native.IsIconic(window.Handle)) return;
        _expectRestore.Add(window.Handle);
        WindowActions.Restore(window.Handle);
    }

    void Minimize(TrackedWindow window)
    {
        if (Native.IsIconic(window.Handle)) return;
        try
        {
            window.Snapshot = WindowSnapshot.Capture(window.Handle) ?? window.Snapshot;
        }
        catch (Exception ex)
        {
            Log.Error($"Snapshot of {window.AppName}", ex);
        }
        _expectMinimize.Add(window.Handle);
        window.MinimizedByUs = true;
        WindowActions.MinimizeWithoutActivating(window.Handle);
    }

    TrackedWindow Track(IntPtr hwnd)
    {
        var (key, name, exe) = WindowFilter.GetAppIdentity(hwnd);
        var window = new TrackedWindow(hwnd, key, name, exe) { LastActive = ++_clock };

        // New windows join the most recent stage that already holds the same app (Stage Manager's default).
        var stage = _stages
            .Where(s => s.Windows.Any(w => w.AppKey == key))
            .OrderByDescending(s => s.LastActive)
            .FirstOrDefault();
        if (stage == null)
        {
            stage = new Stage();
            _stages.Add(stage);
        }
        stage.Add(window);
        stage.LastActive = Math.Max(stage.LastActive, window.LastActive);

        _windows[hwnd] = window;
        return window;
    }

    void Untrack(TrackedWindow window)
    {
        _windows.Remove(window.Handle);
        _expectMinimize.Remove(window.Handle);
        _expectRestore.Remove(window.Handle);

        var stage = window.Stage;
        if (stage == null) return;
        stage.Windows.Remove(window);
        window.Stage = null;
        if (stage.Windows.Count == 0)
        {
            _stages.Remove(stage);
            if (Current == stage) Current = null;
        }
    }

    /// <summary>Maps any window (including dialogs and popups) to the tracked app window that owns it.</summary>
    TrackedWindow? ResolveTracked(IntPtr hwnd, bool allowNew)
    {
        var h = Native.GetAncestor(hwnd, Native.GA_ROOT);
        for (int depth = 0; h != IntPtr.Zero && depth < 8; depth++)
        {
            if (_windows.TryGetValue(h, out var tracked)) return tracked;
            if (allowNew && WindowFilter.IsStageable(h)) return Track(h);
            h = Native.GetWindow(h, Native.GW_OWNER);
        }
        return null;
    }

    void InstallHooks()
    {
        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);
        Hook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND);
        Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_HIDE);
        Hook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED);
    }

    void Hook(uint min, uint max)
    {
        var hook = Native.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    void RemoveHooks()
    {
        foreach (var hook in _hooks) Native.UnhookWinEvent(hook);
        _hooks.Clear();
    }

    // Out-of-context hooks can fire inside any nested message wait (including WPF layout and our own
    // ShowWindow calls), so only queue the event here and handle it once the dispatcher is idle.
    void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Enabled || hwnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW || idChild != 0) return;

        if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART)
        {
            // Record the starting position right away; by the time the queued handler runs, the window
            // may already have moved.
            Native.GetWindowRect(hwnd, out RECT bounds);
            var start = new MoveStart(bounds, Native.IsZoomed(hwnd));
            _dispatcher.InvokeAsync(() => OnMoveStart(hwnd, start));
            return;
        }

        _dispatcher.InvokeAsync(() => HandleWinEvent(eventType, hwnd));
    }

    void OnMoveStart(IntPtr hwnd, MoveStart start)
    {
        if (!Enabled || !_windows.TryGetValue(hwnd, out var window) || window.Stage != Current) return;
        _movingWindow = window;
        _moveStart = start;
        WindowMoveStarted?.Invoke(window);
    }

    void HandleWinEvent(uint eventType, IntPtr hwnd)
    {
        if (!Enabled) return;
        try
        {
            switch (eventType)
            {
                case Native.EVENT_SYSTEM_FOREGROUND:
                    OnForeground(hwnd, respectSettle: true);
                    break;
                case Native.EVENT_SYSTEM_MOVESIZEEND:
                    if (_movingWindow?.Handle == hwnd)
                    {
                        var moved = _movingWindow;
                        _movingWindow = null;
                        WindowMoveEnded?.Invoke(moved, _moveStart);
                    }
                    break;
                case Native.EVENT_SYSTEM_MINIMIZESTART:
                    OnMinimizeStart(hwnd);
                    break;
                case Native.EVENT_SYSTEM_MINIMIZEEND:
                    // The user restored a window themselves (taskbar, Alt+Tab): follow them to its stage.
                    if (!_expectRestore.Remove(hwnd)) OnForeground(hwnd, respectSettle: false);
                    break;
                case Native.EVENT_OBJECT_DESTROY:
                case Native.EVENT_OBJECT_HIDE:
                case Native.EVENT_OBJECT_CLOAKED:
                    OnGone(hwnd, eventType);
                    break;
                case Native.EVENT_OBJECT_SHOW:
                case Native.EVENT_OBJECT_UNCLOAKED:
                    OnAppeared(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Windows can vanish between the event and our calls; never let that take the app down.
            Log.Error($"Window event 0x{eventType:X}", ex);
        }
    }

    void OnForeground(IntPtr hwnd, bool respectSettle)
    {
        var window = ResolveTracked(hwnd, allowNew: true);
        if (window?.Stage == null) return;

        if (window.Stage == Current)
        {
            window.LastActive = ++_clock;
            window.Stage.LastActive = _clock;
            return;
        }
        if (respectSettle && Environment.TickCount64 < _ignoreForegroundUntil) return;

        SwitchTo(window.Stage, window);
    }

    void OnMinimizeStart(IntPtr hwnd)
    {
        if (_expectMinimize.Remove(hwnd)) return;
        if (!_windows.TryGetValue(hwnd, out var window)) return;

        // Minimized by the user: it leaves Stage Manager and lives in the taskbar until restored.
        Untrack(window);
        StagesChanged?.Invoke();
    }

    void OnGone(IntPtr hwnd, uint eventType)
    {
        if (!_windows.TryGetValue(hwnd, out var window)) return;
        bool gone = eventType == Native.EVENT_OBJECT_DESTROY || !Native.IsWindow(hwnd) ||
                    !Native.IsWindowVisible(hwnd) || WindowFilter.IsCloaked(hwnd);
        if (!gone) return;

        Untrack(window);
        StagesChanged?.Invoke();
    }

    void OnAppeared(IntPtr hwnd)
    {
        if (_windows.ContainsKey(hwnd) || Native.IsIconic(hwnd) || !WindowFilter.IsStageable(hwnd)) return;
        // Usually followed by a foreground event, which handles any stage switch.
        Track(hwnd);
        StagesChanged?.Invoke();
    }

    public void Dispose() => Disable();
}
