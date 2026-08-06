using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace UGA;

/// <summary>
/// File-based crash and runtime logger.
/// Writes to %APPDATA%/UGA/app_log.txt and crash_log.txt.
/// Catches: AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException,
/// WinUI UnhandledException, and native SetUnhandledExceptionFilter.
/// </summary>
public static class CrashLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UGA");
    private static readonly string LogFile = Path.Combine(LogDir, "app_log.txt");
    private static readonly string CrashFile = Path.Combine(LogDir, "crash_log.txt");
    private static readonly object _lock = new();

    static CrashLogger()
    {
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>
    /// Call once at app startup to install all exception handlers.
    /// </summary>
    public static void Initialize()
    {
        Log("INFO", "Application starting");

        // .NET unhandled exception (catches most managed crashes)
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log("FATAL", $"AppDomain.UnhandledException: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}");
            if (e.IsTerminating)
                WriteCrash($"AppDomain terminating: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}");
        };

        // Unobserved task exceptions (async void / fire-and-forget tasks)
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log("ERROR", $"UnobservedTaskException: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception}");
            WriteCrash($"UnobservedTaskException: {e.Exception.Message}\n{e.Exception}");
            e.SetObserved(); // prevent .NET from raising it later
        };

        // Native crash handler (catches StackOverflow, AccessViolation, etc.)
        // Note: For StackOverflowException, this may or may not fire depending on .NET version
        try
        {
            var prevFilter = SetUnhandledExceptionFilter(NativeCrashHandler);
            Log("INFO", $"Native crash handler installed (prev={prevFilter})");
        }
        catch (Exception ex)
        {
            Log("WARN", $"Could not install native crash handler: {ex.Message}");
        }
    }

    /// <summary>
    /// Log a message to the log file. Thread-safe.
    /// </summary>
    public static void Log(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            Debug.WriteLine($"[UGA] {line}");
            lock (_lock)
            {
                // Append to main log (keep last 500KB)
                var lines = new[] { line };
                File.AppendAllLines(LogFile, lines);
                TrimFile(LogFile, 512 * 1024);
            }
        }
        catch { /* don't crash the logger */ }
    }

    /// <summary>
    /// Write a crash dump. Thread-safe. Always appends.
    /// </summary>
    public static void WriteCrash(string message)
    {
        try
        {
            lock (_lock)
            {
                var crashMsg = $"===== CRASH {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====\n{message}\n\n";
                File.AppendAllText(CrashFile, crashMsg);
            }
        }
        catch { }
    }

    /// <summary>
    /// Read the crash log file contents.
    /// </summary>
    public static string ReadCrashLog()
    {
        try
        {
            if (File.Exists(CrashFile))
                return File.ReadAllText(CrashFile);
        }
        catch { }
        return "No crash log found.";
    }

    /// <summary>
    /// Read the application log file contents.
    /// </summary>
    public static string ReadAppLog()
    {
        try
        {
            if (File.Exists(LogFile))
                return File.ReadAllText(LogFile);
        }
        catch { }
        return "No app log found.";
    }

    public static string LogFilePath => LogFile;
    public static string CrashLogFilePath => CrashFile;

    private static void TrimFile(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                // Read last half of file
                var allLines = File.ReadAllLines(path);
                var keep = allLines.Skip(allLines.Length / 2).ToArray();
                File.WriteAllLines(path, keep);
            }
        }
        catch { }
    }

    // ─── Native crash handler ───

    private delegate int NativeExceptionFilter(IntPtr exceptionInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern NativeExceptionFilter SetUnhandledExceptionFilter(NativeExceptionFilter filter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void GetCurrentThreadId();

    private static int NativeCrashHandler(IntPtr exceptionInfo)
    {
        try
        {
            WriteCrash($"NATIVE CRASH at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n" +
                       $"ExceptionInfo pointer: 0x{exceptionInfo.ToInt64():X16}\n" +
                       $"Managed stack trace:\n{Environment.StackTrace}");
        }
        catch { }

        // Return EXCEPTION_CONTINUE_SEARCH = 0 to let default handler run
        // Return EXCEPTION_EXECUTE_HANDLER = 1 to try to recover (risky)
        return 0;
    }
}
