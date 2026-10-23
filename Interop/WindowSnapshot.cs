using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win11StageManager.Interop;

/// <summary>
/// Captures a still image of a window so the strip can draw it in 3D. DWM's live thumbnails can't be
/// transformed, so the tilted cards use a snapshot taken right before the window is minimized.
/// </summary>
internal static class WindowSnapshot
{
    const int MaxWidthPx = 480;
    const double CornerRadiusPx = 16;

    static readonly Dictionary<string, BitmapSource> Placeholders = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.IsHungAppWindow(hwnd)) return null;
        if (!Native.GetWindowRect(hwnd, out RECT outer)) return null;

        int w = outer.Width, h = outer.Height;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = Native.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        IntPtr previous = Native.SelectObject(memDc, dib);
        try
        {
            int stride = w * 4;

            // PW_RENDERFULLCONTENT also captures GPU-rendered content (browsers, modern apps). Some older
            // apps come back blank with it, so retry the classic way before giving up.
            bool captured = Native.PrintWindow(hwnd, memDc, Native.PW_RENDERFULLCONTENT) && !IsBlank(bits, w, h, stride);
            if (!captured)
                captured = Native.PrintWindow(hwnd, memDc, 0) && !IsBlank(bits, w, h, stride);
            if (!captured)
            {
                Log.Info($"Could not capture window 0x{hwnd.ToInt64():X} ({WindowFilter.GetClassName(hwnd)}); using placeholder");
                return null;
            }

            // Alpha from PrintWindow is unreliable, so read the pixels as opaque.
            var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bits, stride * h, stride);
            return Finish(CropToVisibleFrame(full, hwnd, outer));
        }
        finally
        {
            Native.SelectObject(memDc, previous);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Stand-in for windows we have no image of (the app blocks capture, or it was minimized before we
    /// could take one): a dark rounded window with the app's icon and name, so the card is never empty.
    /// </summary>
    public static BitmapSource Placeholder(string appKey, string appName, ImageSource? icon)
    {
        if (Placeholders.TryGetValue(appKey, out var cached)) return cached;

        const int w = 400, h = 250;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var fill = new LinearGradientBrush(Color.FromRgb(0x3A, 0x3A, 0x3E), Color.FromRgb(0x20, 0x20, 0x24), 90);
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 2);
            dc.DrawRoundedRectangle(fill, outline, new Rect(1, 1, w - 2, h - 2), CornerRadiusPx, CornerRadiusPx);

            if (icon != null) dc.DrawImage(icon, new Rect(w / 2.0 - 36, h / 2.0 - 62, 72, 72));

            var label = new FormattedText(appName, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Variable Display, Segoe UI"), 24, Brushes.White, 1.0)
            {
                MaxTextWidth = w - 40,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(label, new Point(20, h / 2.0 + 24));
        }

        var result = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        Placeholders[appKey] = result;
        return result;
    }

    /// <summary>Drops the invisible resize borders that GetWindowRect includes on Windows 10/11.</summary>
    static BitmapSource CropToVisibleFrame(BitmapSource full, IntPtr hwnd, RECT outer)
    {
        var frame = WindowFilter.GetFrameBounds(hwnd);
        int x = Math.Max(0, frame.Left - outer.Left);
        int y = Math.Max(0, frame.Top - outer.Top);
        int cw = Math.Min(full.PixelWidth - x, frame.Width);
        int ch = Math.Min(full.PixelHeight - y, frame.Height);
        if (cw <= 0 || ch <= 0) return full;
        return new CroppedBitmap(full, new Int32Rect(x, y, cw, ch));
    }

    /// <summary>Apps that can't be captured hand back an all-black image; sample a grid to spot that.</summary>
    static bool IsBlank(IntPtr bits, int w, int h, int stride)
    {
        for (int gy = 1; gy < 12; gy++)
        {
            for (int gx = 1; gx < 12; gx++)
            {
                int px = w * gx / 12, py = h * gy / 12;
                if ((Marshal.ReadInt32(bits, py * stride + px * 4) & 0x00FFFFFF) != 0) return false;
            }
        }
        return true;
    }

    /// <summary>Downscales and rounds the corners so the image looks like a Windows 11 window.</summary>
    static BitmapSource Finish(BitmapSource source)
    {
        double scale = Math.Min(1.0, (double)MaxWidthPx / source.PixelWidth);
        int tw = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int th = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, tw, th);
            dc.PushClip(new RectangleGeometry(rect, CornerRadiusPx, CornerRadiusPx));
            dc.DrawImage(source, rect);
            dc.Pop();
        }

        var result = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
