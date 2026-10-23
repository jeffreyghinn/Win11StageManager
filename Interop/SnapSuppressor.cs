using System;

namespace Win11StageManager.Interop;

/// <summary>
/// Turns off Windows Snap while Win11StageManager is on, so windows are simply dragged around: no
/// half-screen snapping at the edges, no maximize at the top, no snap layouts. The change is
/// runtime-only (not saved to the user's settings) and is undone when the app turns off or exits.
/// </summary>
internal static class SnapSuppressor
{
    static readonly object Gate = new();
    static bool _suppressed;

    public static void Set(bool suppress)
    {
        lock (Gate)
        {
            if (suppress == _suppressed) return;

            if (suppress)
            {
                // Leave it alone if the user has Snap turned off already; there is nothing to restore later.
                if (!Native.SystemParametersInfo(Native.SPI_GETWINARRANGING, 0, out int enabled, 0) || enabled == 0)
                {
                    Log.Info("Snap is already off in Windows settings; leaving it alone");
                    return;
                }
                if (Write(false))
                {
                    _suppressed = true;
                    Log.Info("Snap turned off");
                }
            }
            else
            {
                Write(true);
                _suppressed = false;
                Log.Info("Snap turned back on");
            }
        }
    }

    /// <summary>Puts Snap back if we turned it off. Safe to call from exit and crash handlers.</summary>
    public static void Restore() => Set(false);

    // The documentation is inconsistent about which parameter carries the value, so pass it in both.
    static bool Write(bool on) =>
        Native.SystemParametersInfo(Native.SPI_SETWINARRANGING, on ? 1u : 0u, new IntPtr(on ? 1 : 0), 0);
}
