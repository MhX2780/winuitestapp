using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace AdvaBrowser;

/// <summary>
/// Win32 cursor helper for WinUI 3.
/// ProtectedCursor is protected and inaccessible from outside the owning class.
/// This uses Win32 SetCursor P/Invoke as a reliable workaround.
/// </summary>
public static class Win32Cursor
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursor = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    public static void SetHand()
    {
        SetCursor(HandCursor);
    }

    public static void SetArrow()
    {
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
