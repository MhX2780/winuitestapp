using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AdvaBrowser;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
/// Uses reflection to access UIElement.ProtectedCursor (protected property)
/// with InputCursor.CreateFromKnownCursor (available since Windows App SDK 1.5).
/// Falls back to Win32 SetCursor via DispatcherQueueTimer if reflection fails.
/// </summary>
public static class CursorHelper
{
    // Cache the PropertyInfo for ProtectedCursor
    private static readonly PropertyInfo? ProtectedCursorProp;
    private static readonly PropertyInfo? InputCursorProp;

    // Win32 fallback
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursorHandle = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursorHandle = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    // Cache the InputCursor objects (they are expensive to create)
    private static object? _cachedHandCursor;
    private static object? _cachedArrowCursor;
    private static bool _cursorInitFailed;

    static CursorHelper()
    {
        try
        {
            // Try to find ProtectedCursor property on UIElement (it's protected)
            ProtectedCursorProp = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            CrashLogger.Log("INFO", $"ProtectedCursor property found: {ProtectedCursorProp != null}, CanWrite: {ProtectedCursorProp?.CanWrite}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to find ProtectedCursor property: {ex.Message}");
        }
    }

    /// <summary>
    /// Create an InputCursor via InputCursor.CreateFromKnownCursor.
    /// </summary>
    private static object? CreateInputCursor(int kind)
    {
        try
        {
            // Microsoft.UI.Input.InputCursor.CreateFromKnownCursor(InputCursorKind)
            var inputCursorType = Type.GetType("Microsoft.UI.Input.InputCursor, Microsoft.WinUI") ??
                                  Type.GetType("Microsoft.UI.Input.InputCursor");
            if (inputCursorType == null) return null;

            // InputCursorKind enum
            var cursorKindType = Type.GetType("Microsoft.UI.Input.InputCursorKind, Microsoft.WinUI") ??
                                 Type.GetType("Microsoft.UI.Input.InputCursorKind");
            if (cursorKindType == null) return null;

            var kindValue = Enum.ToObject(cursorKindType, kind);
            var createMethod = inputCursorType.GetMethod("CreateFromKnownCursor", new[] { cursorKindType });
            return createMethod?.Invoke(null, new[] { kindValue });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to create InputCursor: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the hand cursor (InputCursor or null).
    /// </summary>
    private static object? GetHandCursor()
    {
        if (_cursorInitFailed) return null;
        try
        {
            _cachedHandCursor ??= CreateInputCursor(3); // InputCursorKind.Hand = 3
            return _cachedHandCursor;
        }
        catch { _cursorInitFailed = true; return null; }
    }

    /// <summary>
    /// Set ProtectedCursor on a UIElement via reflection.
    /// </summary>
    private static bool SetProtectedCursor(UIElement el, object cursor)
    {
        if (ProtectedCursorProp == null || !ProtectedCursorProp.CanWrite) return false;
        try
        {
            ProtectedCursorProp.SetValue(el, cursor);
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to set ProtectedCursor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attaches a hand cursor to a UIElement.
    /// Strategy 1: Set ProtectedCursor via reflection (WinUI 3 proper way).
    /// Strategy 2: Fall back to Win32 SetCursor via DispatcherQueueTimer.
    /// </summary>
    public static void SetHandOn(UIElement el)
    {
        // Strategy 1: Try to use InputCursor + ProtectedCursor via reflection
        var handCursor = GetHandCursor();
        if (handCursor != null && SetProtectedCursor(el, handCursor))
        {
            // If reflection worked, we're done — no need for PointerEntered/Exited
            return;
        }

        // Strategy 2: Win32 SetCursor with DispatcherQueueTimer fallback
        SetHandViaWin32(el);
    }

    /// <summary>
    /// Win32 SetCursor fallback with DispatcherQueueTimer to persist the cursor.
    /// </summary>
    private static void SetHandViaWin32(UIElement el)
    {
        DispatcherQueueTimer? timer = null;

        el.PointerEntered += (_, _) =>
        {
            SetCursor(HandCursorHandle);
            var dq = el.DispatcherQueue;
            if (dq != null && dq.HasThreadAccess)
            {
                try
                {
                    timer = dq.CreateTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(16);
                    timer.IsRepeating = true;
                    timer.Tick += (_, _) => SetCursor(HandCursorHandle);
                    timer.Start();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("WARN", $"Cursor timer failed: {ex.Message}");
                }
            }
        };

        el.PointerExited += (_, _) =>
        {
            try
            {
                timer?.Stop();
                timer = null;
            }
            catch { }
            SetCursor(ArrowCursorHandle);
        };
    }
}
