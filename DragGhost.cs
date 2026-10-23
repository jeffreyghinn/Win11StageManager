using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Win11StageManager.Interop;

namespace Win11StageManager;

/// <summary>
/// The see-through preview that follows the pointer while a card is dragged out of the strip.
/// It never takes focus and lets every click pass through it.
/// </summary>
internal sealed class DragGhost : Window
{
    const double PictureWidthDip = 240;
    const double MaxPictureHeightDip = 170;

    readonly Border _card;
    IntPtr _hwnd;

    public DragGhost(BitmapSource image, string caption)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000; // off every screen until the first MoveTo

        double aspect = (double)image.PixelWidth / image.PixelHeight;
        double height = Math.Min(MaxPictureHeightDip, PictureWidthDip / aspect);

        var panel = new StackPanel();
        panel.Children.Add(new Image
        {
            Source = image,
            Width = height * aspect,
            Height = height,
            Stretch = Stretch.Uniform,
        });
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9 },
        });

        _card = new Border
        {
            Child = panel,
            Margin = new Thickness(18), // room for the shadow
            Opacity = 0.85,
            Effect = new DropShadowEffect { ShadowDepth = 6, Direction = 270, BlurRadius = 20, Opacity = 0.5 },
        };
        Content = _card;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex |
                Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
    }

    /// <summary>Centers the preview on the pointer.</summary>
    public void MoveTo(POINT pointer)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out RECT r)) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, pointer.X - r.Width / 2, pointer.Y - r.Height / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Fades the preview while it's over the strip, where letting go cancels the drag.</summary>
    public void SetWillDrop(bool willDrop) => _card.Opacity = willDrop ? 0.85 : 0.4;
}
