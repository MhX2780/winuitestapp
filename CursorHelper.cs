using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AdvaBrowser;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
/// Strategy 1: Reflection to set UIElement.ProtectedCursor with InputCursor.
/// Strategy 2: Win32 SetCursor via DispatcherQueue.CreateTimer (resolved via reflection).
/// </summary>
public static class CursorHelper
{
    private static readonly PropertyInfo? ProtectedCursorProp;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursorHandle = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursorHandle = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    private static object? _cachedHandCursor;
    private static bool _cursorInitFailed;
    private static bool _reflectionWorked;

    // DispatcherQueue.CreateTimer method (resolved via reflection)
    private static readonly MethodInfo? CreateTimerMethod;
    private static readonly PropertyInfo? TimerIntervalProp;
    private static readonly PropertyInfo? TimerIsRepeatingProp;
    private static readonly MethodInfo? TimerStartMethod;
    private static readonly MethodInfo? TimerStopMethod;
    private static readonly EventInfo? TimerTickEvent;

    static CursorHelper()
    {
        try
        {
            ProtectedCursorProp = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            CrashLogger.Log("INFO", $"ProtectedCursor found: {ProtectedCursorProp != null}, CanWrite: {ProtectedCursorProp?.CanWrite}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"ProtectedCursor lookup failed: {ex.Message}");
        }

        try
        {
            // Resolve DispatcherQueueTimer type and its members via reflection
            var dqType = Type.GetType("Microsoft.UI.Dispatching.DispatcherQueue, Microsoft.WinUI") ??
                         Type.GetType("Microsoft.UI.Dispatching.DispatcherQueue");
            if (dqType != null)
            {
                CreateTimerMethod = dqType.GetMethod("CreateTimer");
                CrashLogger.Log("INFO", $"CreateTimer found: {CreateTimerMethod != null}");
            }

            var timerType = Type.GetType("Microsoft.UI.Dispatching.DispatcherQueueTimer, Microsoft.WinUI") ??
                            Type.GetType("Microsoft.UI.Dispatching.DispatcherQueueTimer");
            if (timerType != null)
            {
                TimerIntervalProp = timerType.GetProperty("Interval");
                TimerIsRepeatingProp = timerType.GetProperty("IsRepeating");
                TimerStartMethod = timerType.GetMethod("Start");
                TimerStopMethod = timerType.GetMethod("Stop");
                TimerTickEvent = timerType.GetEvent("Tick");
                CrashLogger.Log("INFO", $"DispatcherQueueTimer members resolved: Interval={TimerIntervalProp != null}, Start={TimerStartMethod != null}, Tick={TimerTickEvent != null}");
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"DispatcherQueueTimer reflection failed: {ex.Message}");
        }
    }

    private static object? CreateInputCursor(int kind)
    {
        try
        {
            var inputCursorType = Type.GetType("Microsoft.UI.Input.InputCursor, Microsoft.WinUI") ??
                                  Type.GetType("Microsoft.UI.Input.InputCursor");
            if (inputCursorType == null) return null;

            var cursorKindType = Type.GetType("Microsoft.UI.Input.InputCursorKind, Microsoft.WinUI") ??
                                 Type.GetType("Microsoft.UI.Input.InputCursorKind");
            if (cursorKindType == null) return null;

            var kindValue = Enum.ToObject(cursorKindType, kind);
            var createMethod = inputCursorType.GetMethod("CreateFromKnownCursor", new[] { cursorKindType });
            return createMethod?.Invoke(null, new[] { kindValue });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"CreateInputCursor failed: {ex.Message}");
            return null;
        }
    }

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
            CrashLogger.Log("WARN", $"SetProtectedCursor failed: {ex.Message}");
            return false;
        }
    }

    public static void SetHandOn(UIElement el)
    {
        if (_reflectionWorked)
        {
            // Strategy 1 already worked before — use it directly
            var handCursor = GetHandCursor();
            if (handCursor != null)
            {
                SetProtectedCursor(el, handCursor);
                return;
            }
        }

        // Try Strategy 1 once
        var cursor = GetHandCursor();
        if (cursor != null && SetProtectedCursor(el, cursor))
        {
            _reflectionWorked = true;
            CrashLogger.Log("INFO", "Hand cursor set via ProtectedCursor reflection");
            return;
        }

        // Strategy 2: Win32 SetCursor via reflection-based timer
        SetHandViaWin32(el);
    }

    private static void SetHandViaWin32(UIElement el)
    {
        object? timerObj = null;
        Delegate? tickHandler = null;

        el.PointerEntered += (_, _) =>
        {
            SetCursor(HandCursorHandle);

            if (CreateTimerMethod == null || TimerIntervalProp == null ||
                TimerIsRepeatingProp == null || TimerStartMethod == null || TimerTickEvent == null)
            {
                return;
            }

            try
            {
                var dq = el.DispatcherQueue;
                if (dq == null) return;

                // Create timer via reflection: dq.CreateTimer()
                timerObj = CreateTimerMethod.Invoke(dq, null);
                if (timerObj == null) return;

                // timer.Interval = TimeSpan.FromMilliseconds(16)
                TimerIntervalProp.SetValue(timerObj, TimeSpan.FromMilliseconds(16));
                // timer.IsRepeating = true
                TimerIsRepeatingProp.SetValue(timerObj, true);

                // timer.Tick += handler
                // Create a delegate of the correct type for the Tick event
                var tickType = TimerTickEvent.EventHandlerType;
                if (tickType != null && tickHandler == null)
                {
                    // Create an open delegate: static void TickHandler(object sender, object e)
                    var tickMethod = typeof(CursorHelper).GetMethod(nameof(Win32TickHandler), BindingFlags.NonPublic | BindingFlags.Static);
                    tickHandler = Delegate.CreateDelegate(tickType, tickMethod!);
                }
                if (tickHandler != null)
                {
                    TimerTickEvent.AddEventHandler(timerObj, tickHandler);
                }

                // timer.Start()
                TimerStartMethod.Invoke(timerObj, null);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("WARN", $"Win32 cursor timer failed: {ex.Message}");
            }
        };

        el.PointerExited += (_, _) =>
        {
            try
            {
                if (timerObj != null && TimerStopMethod != null)
                {
                    TimerStopMethod.Invoke(timerObj, null);
                    timerObj = null;
                }
            }
            catch { }
            SetCursor(ArrowCursorHandle);
        };
    }

    // Static handler for the Win32 cursor tick (called via reflection delegate)
    private static void Win32TickHandler(object? sender, object? e) => SetCursor(HandCursorHandle);
}
