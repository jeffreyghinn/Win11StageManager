using System;
using System.Threading;
using System.Windows;
using Win11StageManager.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Win11StageManager;

public partial class App : Application
{
    const string AppName = "Win11StageManager";

    const string HelpText =
        AppName + " keeps the app you're using on screen and moves everything else into the strip on the left.\n\n" +
        "• Click a card to switch to it.\n" +
        "• Drag a card out of the strip (or Shift+click it) to add its windows to the current stage.\n" +
        "• Drag a window by its title bar onto the strip to send it away.\n" +
        "• Ctrl+click a grouped card to split it back into separate windows.\n" +
        "• Move the pointer to the left edge to reveal the strip when a window covers it.\n" +
        "• Minimizing a window takes it out of " + AppName + ".\n" +
        "• Ctrl+Alt+S turns " + AppName + " on and off.";

    Mutex? _instanceMutex;
    bool _ownsMutex;
    StageController? _controller;
    StripWindow? _strip;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _toggleItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\Win11StageManager", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        _controller = new StageController();
        Log.Info("Starting");

        // Log errors instead of dying on them, and never leave the user's windows stuck minimized.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Fatal exception", ex);
            SafeRestore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRestore();

        _strip = new StripWindow();
        _strip.HotkeyPressed += () => _controller.Toggle();
        _strip.Attach(_controller);

        _tray = CreateTray();
        _controller.EnabledChanged += on =>
        {
            _toggleItem!.Checked = on;
            _tray.Text = on ? AppName + ": on" : AppName + ": off";
            // No snapping while it's on: windows are just dragged around.
            SnapSuppressor.Set(on);
        };

        _controller.Enable();

        _tray.ShowBalloonTip(4000, AppName + " is running",
            _strip.HotkeyRegistered
                ? "Press Ctrl+Alt+S or click the tray icon to turn it on and off."
                : "Ctrl+Alt+S is taken by another app. Use the tray icon to turn it on and off.",
            Forms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"Exiting (code {e.ApplicationExitCode})");
        SnapSuppressor.Restore();
        _controller?.Dispose();
        _strip?.Close();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }

    void SafeRestore()
    {
        try { SnapSuppressor.Restore(); }
        catch { /* best effort during shutdown */ }
        try { _controller?.RestoreMinimizedWindows(); }
        catch { /* best effort during shutdown */ }
        try { _strip?.ReleaseScreenSpace(); }
        catch { /* best effort during shutdown */ }
    }

    Forms.NotifyIcon CreateTray()
    {
        _toggleItem = new Forms.ToolStripMenuItem(AppName, null, (_, _) => _controller!.Toggle())
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+S",
        };

        var reserveItem = new Forms.ToolStripMenuItem("Keep windows clear of the strip") { Checked = true };
        reserveItem.Click += (_, _) =>
        {
            reserveItem.Checked = !reserveItem.Checked;
            _strip!.ReserveSpace = reserveItem.Checked;
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(reserveItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("How to use", null, (_, _) => MessageBox.Show(HelpText, AppName));
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        var tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) _controller!.Toggle();
        };
        return tray;
    }

    static void OpenLog()
    {
        if (!System.IO.File.Exists(Log.FilePath)) Log.Info("Log opened");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    /// <summary>Draws the icon in code: three small side cards next to one large center window.</summary>
    static Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var side = new Drawing.SolidBrush(Drawing.Color.FromArgb(220, 255, 255, 255));
            using var center = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 96, 165, 250));
            g.FillRectangle(side, 1, 6, 8, 5);
            g.FillRectangle(side, 1, 13, 8, 5);
            g.FillRectangle(side, 1, 20, 8, 5);
            g.FillRectangle(center, 12, 4, 19, 24);
        }
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
