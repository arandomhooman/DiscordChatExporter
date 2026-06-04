using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using DiscordChatExporter.Gui.Utils.Extensions;

namespace DiscordChatExporter.Gui.Utils;

// Best-effort "export finished" attention cue. On Windows, flashes the taskbar button when the
// main window isn't focused; no-op everywhere else. No dependency, works for a portable exe.
internal static partial class CompletionAttention
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
            NativeMethods.FlashTaskbar(handle.Value);
        }
        catch
        {
            // Best-effort: an attention cue must never disrupt the app.
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-flashwinfo
        private const uint FLASHW_TRAY = 0x00000002; // flash the taskbar button
        private const uint FLASHW_TIMERNOFG = 0x0000000C; // flash until the window comes to the foreground

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

        public static void FlashTaskbar(IntPtr hwnd)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };

            FlashWindowEx(ref info);
        }
    }
}
