using System.Runtime.InteropServices;

namespace AdvaBrowser;

/// <summary>
/// Windows Taskbar Progress Bar integration ported from UGA taskbar_progress.py.
///
/// Uses the Win32 ITaskbarList3 COM interface to control the progress
/// indicator on the application's taskbar icon.
///
/// States:
///   NoProgress   = No progress bar shown
///   Normal       = Green progress bar with percentage (0-100)
///   Error        = Red progress bar
///   Paused       = Yellow progress bar
///   Indeterminate = Pulsing green progress bar
///
/// Usage:
///   TaskbarProgress.SetIndeterminate();
///   TaskbarProgress.SetProgress(50);
///   TaskbarProgress.Clear();
/// </summary>
public static class TaskbarProgress
{
    // ─── Win32 COM Imports ───

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C0A000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hWnd);
        void DeleteTab(IntPtr hWnd);
        void ActivateTab(IntPtr hWnd);
        void SetActiveAlt(IntPtr hWnd);
        void MarkFullscreenWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hWnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hWnd, TBPFLAG tbpFlags);
    }

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C0A000")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private class TaskbarList { }

    private enum TBPFLAG : uint
    {
        NoProgress = 0x00000000,
        Indeterminate = 0x00000001,
        Normal = 0x00000002,
        Error = 0x00000004,
        Paused = 0x00000008,
    }

    private static ITaskbarList3? _taskbar;
    private static IntPtr _hwnd;

    /// <summary>
    /// Initializes the taskbar interface with the given window handle.
    /// Call once after the main window is created.
    /// </summary>
    public static void Initialize(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
            CrashLogger.Log("INFO", "TaskbarProgress initialized successfully");
        }
        catch (Exception ex) 
        { 
            _taskbar = null; 
            CrashLogger.Log("WARN", $"TaskbarProgress init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Auto-initializes from the App.MainWindow if not yet initialized.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_taskbar != null) return;
        try
        {
            if (App.MainWindow != null)
            {
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                _taskbar = (ITaskbarList3)new TaskbarList();
                _taskbar.HrInit();
            }
        }
        catch { _taskbar = null; }
    }

    /// <summary>Show pulsing/indeterminate progress on the taskbar icon.</summary>
    public static void SetIndeterminate()
    {
        EnsureInitialized();
        try { _taskbar?.SetProgressState(_hwnd, TBPFLAG.Indeterminate); }
        catch { }
    }

    /// <summary>Set the taskbar progress to a specific percentage (0-100).</summary>
    public static void SetProgress(int percent)
    {
        EnsureInitialized();
        percent = Math.Max(0, Math.Min(100, percent));
        try
        {
            _taskbar?.SetProgressState(_hwnd, TBPFLAG.Normal);
            _taskbar?.SetProgressValue(_hwnd, (ulong)percent, 100);
        }
        catch { }
    }

    /// <summary>Show error state (red) on the taskbar icon.</summary>
    public static void SetError()
    {
        EnsureInitialized();
        try { _taskbar?.SetProgressState(_hwnd, TBPFLAG.Error); }
        catch { }
    }

    /// <summary>Show paused state (yellow) on the taskbar icon.</summary>
    public static void SetPaused()
    {
        EnsureInitialized();
        try { _taskbar?.SetProgressState(_hwnd, TBPFLAG.Paused); }
        catch { }
    }

    /// <summary>Hide/clear the taskbar progress bar.</summary>
    public static void Clear()
    {
        EnsureInitialized();
        try { _taskbar?.SetProgressState(_hwnd, TBPFLAG.NoProgress); }
        catch { }
    }

    /// <summary>
    /// Update taskbar progress based on the current plan step.
    /// Step 1 = Indeterminate, Step 2+ = percentage of completed steps.
    /// </summary>
    public static void SetStepProgress(int currentStep, int totalSteps)
    {
        if (currentStep <= 1)
            SetIndeterminate();
        else
            SetProgress((int)(((currentStep - 1) / (double)totalSteps) * 100));
    }
}
