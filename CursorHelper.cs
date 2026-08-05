using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AdvaBrowser;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
/// ProtectedCursor is protected, so we use WinRT CoreWindow.PointerCursor.
/// Falls back to Win32 SetCursor if CoreWindow is unavailable.
/// </summary>
public static class CursorHelper
{
    // Win32 fallback
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursor = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    /// <summary>
    /// Sets the global pointer cursor to a hand shape.
    /// </summary>
    public static void SetHand()
    {
        // Try CoreWindow first (WinRT way — persists properly)
        try
        {
            var coreWindow = Windows.UI.Core.CoreWindow.GetForCurrentThread();
            if (coreWindow != null)
            {
                coreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Hand, 0);
                return;
            }
        }
        catch { }

        // Fallback to Win32 SetCursor
        SetCursor(HandCursor);
    }

    /// <summary>
    /// Sets the global pointer cursor back to arrow.
    /// </summary>
    public static void SetArrow()
    {
        try
        {
            var coreWindow = Windows.UI.Core.CoreWindow.GetForCurrentThread();
            if (coreWindow != null)
            {
                coreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Arrow, 0);
                return;
            }
        }
        catch { }

        SetCursor(ArrowCursor);
    }

    /// <summary>
    /// Attaches hand/arrow cursor to a UIElement via PointerEntered/PointerExited.
    /// </summary>
    public static void SetHandOn(UIElement el)
    {
        el.PointerEntered += (_, _) => SetHand();
        el.PointerExited += (_, _) => SetArrow();
    }
}
