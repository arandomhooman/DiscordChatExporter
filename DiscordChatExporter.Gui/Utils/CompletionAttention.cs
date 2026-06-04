using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using DiscordChatExporter.Gui.Utils.Extensions;

namespace DiscordChatExporter.Gui.Utils;

// Best-effort "export finished" attention cue. On Windows, flashes the taskbar button when the
// main window isn't focused; no-op everywhere else. No dependency, works for a portable exe.
internal static class CompletionAttention
{
    public static void FlashIfUnfocused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Only signal if the user isn't already looking at the window.
        if (
            Application.Current?.ApplicationLifetime?.TryGetTopLevel() is not Window window
            || window.IsActive
        )
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        try
        {
            FlashTaskbar(handle.Value);
        }
        catch
        {
            // Best-effort: an attention cue must never disrupt the app.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void FlashTaskbar(IntPtr hwnd)
    {
        var info = new NativeMethods.Windows.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.Windows.FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = NativeMethods.Windows.FLASHW_TRAY | NativeMethods.Windows.FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };

        NativeMethods.Windows.FlashWindowEx(ref info);
    }
}
