using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace AdvaBrowser;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
/// ProtectedCursor is protected in WinUI 3, so we use a DispatcherQueueTimer
/// that continuously sets the Win32 cursor at 60fps while the pointer is over the element.
/// This defeats WinUI 3's continuous cursor reset.
/// </summary>
public static class CursorHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursor = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    /// <summary>
    /// Attaches a hand cursor to a UIElement. Uses a DispatcherQueueTimer at 60fps
    /// to continuously override WinUI 3's cursor reset behavior.
    /// </summary>
    public static void SetHandOn(UIElement el)
    {
        DispatcherQueueTimer? timer = null;

        el.PointerEntered += (_, _) =>
        {
            SetCursor(HandCursor);
            // Start a 60fps timer to keep resetting cursor while hovering
            var dq = el.DispatcherQueue;
            if (dq != null && dq.HasThreadAccess)
            {
                timer = dq.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(16);
                timer.IsRepeating = true;
                timer.Tick += (_, _) => SetCursor(HandCursor);
                timer.Start();
            }
        };

        el.PointerExited += (_, _) =>
        {
            timer?.Stop();
            timer = null;
            SetCursor(ArrowCursor);
        };
    }
}
