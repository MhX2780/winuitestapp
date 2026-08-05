using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AdvaBrowser;

/// <summary>
/// Cursor helper for WinUI 3 Desktop.
/// Strategy 1: Reflection to set UIElement.ProtectedCursor with InputCursor.
/// Strategy 2: Win32 SetCursor via DispatcherQueue.CreateTimer (resolved via reflection).
/// All WinRT type lookups use Assembly scan to avoid Type.GetType resolution failures on CI.
/// </summary>
public static class CursorHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private static readonly IntPtr HandCursorHandle = LoadCursor(IntPtr.Zero, 32649); // IDC_HAND
    private static readonly IntPtr ArrowCursorHandle = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW

    // Reflection-cached members
    private static readonly PropertyInfo? ProtectedCursorProp;
    private static readonly MethodInfo? CreateInputCursorMethod;
    private static readonly Type? InputCursorKindType;
    private static readonly MethodInfo? CreateTimerMethod;
    private static readonly PropertyInfo? TimerIntervalProp;
    private static readonly PropertyInfo? TimerIsRepeatingProp;
    private static readonly MethodInfo? TimerStartMethod;
    private static readonly MethodInfo? TimerStopMethod;
    private static readonly EventInfo? TimerTickEvent;

    private static object? _cachedHandCursor;
    private static bool _cursorReflectionReady;
    private static bool _cursorReflectionFailed;
    private static bool _win32Ready;

    static CursorHelper()
    {
        // Scan loaded assemblies for WinUI types
        Type? inputCursorType = null;
        Type? inputCursorKindType = null;
        Type? dispatcherQueueTimerType = null;
        Type? dispatcherQueueType = null;

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (inputCursorType == null)
                    inputCursorType = asm.GetType("Microsoft.UI.Input.InputCursor");
                if (inputCursorKindType == null)
                    inputCursorKindType = asm.GetType("Microsoft.UI.Input.InputCursorKind");
                if (dispatcherQueueTimerType == null)
                    dispatcherQueueTimerType = asm.GetType("Microsoft.UI.Dispatching.DispatcherQueueTimer");
                if (dispatcherQueueType == null)
                    dispatcherQueueType = asm.GetType("Microsoft.UI.Dispatching.DispatcherQueue");
            }

            CrashLogger.Log("INFO", $"Type scan: InputCursor={inputCursorType != null}, InputCursorKind={inputCursorKindType != null}, " +
                $"DQTimer={dispatcherQueueTimerType != null}, DQ={dispatcherQueueType != null}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Assembly scan failed: {ex.Message}");
        }

        // ProtectedCursor on UIElement
        try
        {
            ProtectedCursorProp = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            CrashLogger.Log("INFO", $"ProtectedCursor: found={ProtectedCursorProp != null}, CanWrite={ProtectedCursorProp?.CanWrite}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"ProtectedCursor lookup failed: {ex.Message}");
        }

        // InputCursor.CreateFromKnownCursor
        if (inputCursorType != null)
        {
            CreateInputCursorMethod = inputCursorType.GetMethod("CreateFromKnownCursor");
            InputCursorKindType = inputCursorKindType;
            CrashLogger.Log("INFO", $"CreateFromKnownCursor: found={CreateInputCursorMethod != null}, KindType={InputCursorKindType?.Name}");
        }

        // DispatcherQueue.CreateTimer and DispatcherQueueTimer members
        if (dispatcherQueueType != null)
        {
            CreateTimerMethod = dispatcherQueueType.GetMethod("CreateTimer");
            CrashLogger.Log("INFO", $"DQ.CreateTimer: found={CreateTimerMethod != null}");
        }

        if (dispatcherQueueTimerType != null)
        {
            TimerIntervalProp = dispatcherQueueTimerType.GetProperty("Interval");
            TimerIsRepeatingProp = dispatcherQueueTimerType.GetProperty("IsRepeating");
            TimerStartMethod = dispatcherQueueTimerType.GetMethod("Start");
            TimerStopMethod = dispatcherQueueTimerType.GetMethod("Stop");
            TimerTickEvent = dispatcherQueueTimerType.GetEvent("Tick");
            CrashLogger.Log("INFO", $"DQTimer: Interval={TimerIntervalProp != null}, Start={TimerStartMethod != null}, " +
                $"Stop={TimerStopMethod != null}, Tick={TimerTickEvent != null}");
        }

        // Check if cursor reflection strategy is viable
        if (ProtectedCursorProp != null && ProtectedCursorProp.CanWrite &&
            CreateInputCursorMethod != null && InputCursorKindType != null)
        {
            _cursorReflectionReady = true;
            CrashLogger.Log("INFO", "CursorHelper: Strategy 1 (ProtectedCursor + InputCursor) is READY");
        }
        else
        {
            _cursorReflectionFailed = true;
            CrashLogger.Log("WARN", "CursorHelper: Strategy 1 not available, will use Strategy 2 (Win32)");
        }

        // Check if Win32 timer strategy is viable
        if (CreateTimerMethod != null && TimerIntervalProp != null &&
            TimerStartMethod != null && TimerTickEvent != null)
        {
            _win32Ready = true;
            CrashLogger.Log("INFO", "CursorHelper: Strategy 2 (Win32 timer) is READY");
        }
    }

    private static object? CreateInputCursor(int kindValue)
    {
        if (CreateInputCursorMethod == null || InputCursorKindType == null) return null;
        try
        {
            var kind = Enum.ToObject(InputCursorKindType, kindValue);
            return CreateInputCursorMethod.Invoke(null, new[] { kind });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"CreateInputCursor({kindValue}) failed: {ex.Message}");
            return null;
        }
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

    /// <summary>
    /// Attaches hand cursor to a UIElement.
    /// </summary>
    public static void SetHandOn(UIElement el)
    {
        // Strategy 1: ProtectedCursor + InputCursor via reflection
        if (_cursorReflectionReady)
        {
            var cursor = CreateInputCursor(3); // InputCursorKind.Hand = 3
            if (cursor != null && SetProtectedCursor(el, cursor))
                return;
        }

        // Strategy 2: Win32 SetCursor + reflection-based DispatcherQueueTimer
        if (_win32Ready)
        {
            SetHandViaWin32(el);
            return;
        }

        // Strategy 3: Plain Win32 SetCursor on PointerEntered (no timer — cursor will flicker)
        SetHandViaWin32Simple(el);
    }

    /// <summary>
    /// Win32 SetCursor with DispatcherQueueTimer (via reflection) to persist cursor.
    /// </summary>
    private static void SetHandViaWin32(UIElement el)
    {
        object? timerObj = null;
        Delegate? tickHandler = null;

        el.PointerEntered += (_, _) =>
        {
            SetCursor(HandCursorHandle);
            try
            {
                var dq = el.DispatcherQueue;
                if (dq == null || CreateTimerMethod == null) return;

                timerObj = CreateTimerMethod.Invoke(dq, null);
                if (timerObj == null) return;

                TimerIntervalProp?.SetValue(timerObj, TimeSpan.FromMilliseconds(16));
                TimerIsRepeatingProp?.SetValue(timerObj, true);

                if (TimerTickEvent != null && tickHandler == null)
                {
                    var tickType = TimerTickEvent.EventHandlerType;
                    if (tickType != null)
                    {
                        var method = typeof(CursorHelper).GetMethod(nameof(TickHandler), BindingFlags.NonPublic | BindingFlags.Static);
                        if (method != null)
                            tickHandler = Delegate.CreateDelegate(tickType, method);
                    }
                }
                if (tickHandler != null && TimerTickEvent != null)
                {
                    TimerTickEvent.AddEventHandler(timerObj, tickHandler);
                }

                TimerStartMethod?.Invoke(timerObj, null);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("WARN", $"Cursor timer start failed: {ex.Message}");
            }
        };

        el.PointerExited += (_, _) =>
        {
            try { TimerStopMethod?.Invoke(timerObj, null); } catch { }
            timerObj = null;
            SetCursor(ArrowCursorHandle);
        };
    }

    /// <summary>
    /// Simple fallback: just SetCursor on PointerEntered. Cursor will flicker but at least shows hand.
    /// </summary>
    private static void SetHandViaWin32Simple(UIElement el)
    {
        el.PointerEntered += (_, _) => SetCursor(HandCursorHandle);
        el.PointerExited += (_, _) => SetCursor(ArrowCursorHandle);
        // Note: Intentionally set HAND on exit too, since WinUI resets to arrow on every move
    }

    private static void TickHandler(object? sender, object? e) => SetCursor(HandCursorHandle);
}
