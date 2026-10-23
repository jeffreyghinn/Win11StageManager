using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11StageManager.Interop;

namespace Win11StageManager;

/// <summary>A top-level window from another app that Stage Manager is managing.</summary>
internal sealed class TrackedWindow
{
    ImageSource? _icon;
    bool _iconLoaded;

    public TrackedWindow(IntPtr handle, string appKey, string appName, string? exePath)
    {
        Handle = handle;
        AppKey = appKey;
        AppName = appName;
        ExePath = exePath;
    }

    public IntPtr Handle { get; }
    public string AppKey { get; }
    public string AppName { get; }
    public string? ExePath { get; }
    public Stage? Stage { get; set; }

    /// <summary>Logical clock value of the last time this window was focused.</summary>
    public long LastActive { get; set; }

    /// <summary>True when we minimized it, so we know to bring it back when Stage Manager turns off.</summary>
    public bool MinimizedByUs { get; set; }

    /// <summary>Still image taken just before we minimized it; used for the tilted 3D cards.</summary>
    public BitmapSource? Snapshot { get; set; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _icon = WindowFilter.GetIcon(Handle, AppKey, ExePath);
                _iconLoaded = true;
            }
            return _icon;
        }
    }
}

/// <summary>A group of windows that are shown and hidden together.</summary>
internal sealed class Stage
{
    public List<TrackedWindow> Windows { get; } = new();
    public long LastActive { get; set; }

    public TrackedWindow? Front => Windows.Count == 0 ? null : Windows.MaxBy(w => w.LastActive);

    public void Add(TrackedWindow window)
    {
        window.Stage?.Windows.Remove(window);
        window.Stage = this;
        Windows.Add(window);
    }
}
