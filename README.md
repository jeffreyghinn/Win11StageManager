# Win11StageManager

A small Windows 11 utility that copies macOS Stage Manager. The app you're using stays on screen.
Everything else is minimized into a strip on the left edge, shown as tilted cards grouped by app.

## Build and run

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer) on Windows 10/11.

```powershell
cd Win11StageManager
dotnet run -c Release
```

To get a single standalone `.exe`:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# output: bin\Release\net8.0-windows\win-x64\publish\Win11StageManager.exe
```

To start it at login, put a shortcut to `Win11StageManager.exe` in `shell:startup`.

## Using it

| Action | Result |
| --- | --- |
| Click a card | Switch to that stage (the current one is minimized into the strip) |
| Drag a card out of the strip | Add its windows to the current stage, placing the front one where you drop it |
| Shift+click a card | Add its windows to the current stage |
| Drag a window by its title bar onto the strip | Send it to the strip as its own card |
| Ctrl+click a grouped card | Split it into one card per window |
| Pointer to the left edge | Reveal the strip when a window is covering it |
| Taskbar / Alt+Tab to a window | Its stage takes over the screen automatically |
| Minimize a window | Takes it out of Win11StageManager (it stays in the taskbar) |
| Ctrl+Alt+S or left-click tray icon | Turn Win11StageManager on/off |

Turning it off, exiting, or a crash restores every window it minimized.

While Win11StageManager is on, Windows Snap is switched off, so windows are simply dragged around: no
half-screen snapping at the edges, no maximize when dragged to the top, and no snap layouts. Snap comes back
on when you turn Win11StageManager off or exit it. The change is never saved, so your **Snap windows**
setting in Windows Settings is unaffected, and if you've turned Snap off yourself, it's left alone.

By default the strip reserves its column of the screen (the same way the taskbar does), so maximized
windows sit beside it. Untick **Keep windows clear of the strip** in the tray menu to let windows cover it
macOS-style; the strip then slides away and you reveal it by pushing the pointer to the left edge.

Problems are logged to `%LOCALAPPDATA%\Win11StageManager\Win11StageManager.log` (tray menu → **Open log**).

## How it works

- **StageController.cs** keeps the stage model. WinEvent hooks track foreground changes, minimize/restore,
  and windows appearing or closing. Switching restores the target stage back-to-front, focuses it, then
  minimizes the previous stage.
- **StripWindow.xaml.cs** is a transparent, topmost strip that never takes focus. It draws each stage as a tilted 3D
  stack of window snapshots and slides out of the way when an on-stage window overlaps it.
- **Interop/WindowFilter.cs** applies Alt+Tab-style rules to decide which windows count as app windows,
  and resolves app names and icons (including UWP apps hosted in ApplicationFrameHost).

## Known limitations

- Primary monitor only, current virtual desktop only.
- Cards are tilted 3D snapshots taken the moment a window is moved to the strip, so they don't update live.
  Apps that block capture (some games, DRM video) show a placeholder card with the app's icon and name.
- Windows from elevated (admin) apps can't be managed unless Win11StageManager also runs as admin.
- No drag-and-drop of files onto cards yet.
