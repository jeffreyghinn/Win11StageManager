using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Win11StageManager.Interop;

/// <summary>Decides which windows belong in Stage Manager and describes them.</summary>
internal static class WindowFilter
{
    // Shell surfaces and system overlays that look like normal windows but must never be staged.
    static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.Internal.Shell.TabProxyWindow",
    };

    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<IntPtr> EnumerateTopLevel()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Roughly the same rules Alt+Tab uses to decide what counts as an app window.</summary>
    public static bool IsStageable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;

        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;

        if ((style & Native.WS_CHILD) != 0) return false;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (IsCloaked(hwnd)) return false;
        if (Native.GetWindowTextLength(hwnd) == 0) return false;
        if (ExcludedClasses.Contains(GetClassName(hwnd))) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return false;

        if (!Native.IsIconic(hwnd))
        {
            var r = GetFrameBounds(hwnd);
            if (r.Width < 60 || r.Height < 40) return false;
        }
        return true;
    }

    /// <summary>Hidden UWP apps and windows on other virtual desktops are "cloaked" rather than hidden.</summary>
    public static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Visible bounds, without the invisible resize borders GetWindowRect includes.</summary>
    public static RECT GetFrameBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r,
                System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            return r;
        Native.GetWindowRect(hwnd, out r);
        return r;
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(Native.GetWindowTextLength(hwnd) + 1);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Returns a stable per-app key, a display name and the executable path (if readable).</summary>
    public static (string Key, string Name, string? ExePath) GetAppIdentity(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string? exe = GetProcessPath(pid);

        if (exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // UWP apps are hosted in a frame window; the real app owns a child window in another process.
            string? inner = null;
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out uint childPid);
                if (childPid == pid) return true;
                inner = GetProcessPath(childPid);
                return false;
            }, IntPtr.Zero);

            if (inner == null)
            {
                string title = GetTitle(hwnd);
                return ("uwp:" + title, title, null);
            }
            exe = inner;
        }

        if (exe == null)
        {
            string title = GetTitle(hwnd);
            return ("pid:" + pid, title, null);
        }
        return (exe.ToLowerInvariant(), GetFriendlyName(exe), exe);
    }

    static string? GetProcessPath(uint pid)
    {
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    static string GetFriendlyName(string exe)
    {
        if (NameCache.TryGetValue(exe, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(description) && !description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = description.Trim();
        }
        catch
        {
            // Protected executables (e.g. under WindowsApps) can refuse access; keep the file name.
        }
        NameCache[exe] = name;
        return name;
    }

    public static ImageSource? GetIcon(IntPtr hwnd, string appKey, string? exePath)
    {
        if (IconCache.TryGetValue(appKey, out var cached)) return cached;

        ImageSource? result = null;
        IntPtr hIcon = GetWindowIconHandle(hwnd);
        if (hIcon != IntPtr.Zero) result = FromHIcon(hIcon);

        if (result == null && exePath != null)
        {
            try
            {
                using var icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) result = FromHIcon(icon.Handle);
            }
            catch
            {
                // No icon is fine; the label just shows the name.
            }
        }

        IconCache[appKey] = result;
        return result;
    }

    static IntPtr GetWindowIconHandle(IntPtr hwnd)
    {
        foreach (int type in new[] { Native.ICON_BIG, Native.ICON_SMALL2 })
        {
            if (Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)type, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 100, out IntPtr icon) != IntPtr.Zero && icon != IntPtr.Zero)
                return icon;
        }
        IntPtr classIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        return classIcon != IntPtr.Zero ? classIcon : Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
    }

    static ImageSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
