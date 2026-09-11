using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace UGA;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
///
/// UIElement.ProtectedCursor is a *protected* WinRT property with no public
/// setter, so it can't be assigned directly from outside a UIElement
/// subclass. The reflection approach that actually works — confirmed by
/// Microsoft's own .NET MAUI Windows handler code (Microsoft Q&A) and by
/// Simon Mourier (Microsoft MVP) — is Type.InvokeMember with
/// BindingFlags.SetProperty, NOT GetProperty()+PropertyInfo.SetValue(), which
/// silently fails to change the visible cursor on WinRT-projected properties
/// like this one even when the PropertyInfo itself is found.
/// </summary>
public static class CursorHelper
{
    private static readonly Dictionary<InputSystemCursorShape, InputSystemCursor> _cursorCache = new();

    private static InputSystemCursor GetCursor(InputSystemCursorShape shape)
    {
        if (!_cursorCache.TryGetValue(shape, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _cursorCache[shape] = cursor;
        }
        return cursor;
    }

    /// <summary>
    /// Sets an element's cursor via the confirmed-working InvokeMember pattern.
    /// Safe to call repeatedly (e.g. on every PointerEntered) — failures are
    /// swallowed and logged rather than throwing, since a cursor glitch should
    /// never crash the app.
    /// </summary>
    private static void SetCursor(UIElement element, InputCursor? cursor)
    {
        try
        {
            typeof(UIElement).InvokeMember(
                "ProtectedCursor",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance,
                null,
                element,
                new object?[] { cursor });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"CursorHelper.SetCursor failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a hand cursor while the pointer is over this element, reverting
    /// to the default arrow on exit. Use on any non-Button element (Border,
    /// Grid, StackPanel...) that has a Tapped/PointerPressed handler, since
    /// only real controls like Button get a hand cursor automatically.
    /// </summary>
    public static void SetHandOn(UIElement element)
    {
        element.PointerEntered += (_, _) => SetCursor(element, GetCursor(InputSystemCursorShape.Hand));
        element.PointerExited += (_, _) => SetCursor(element, null); // null = inherit default (arrow)
    }
}
