using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AdvaBrowser;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += App_UnhandledException;

        // Install file-based crash logger (AppDomain + TaskScheduler + native)
        CrashLogger.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        CrashLogger.Log("INFO", "OnLaunched: Creating MainWindow");

        try
        {
            var window = new MainWindow();
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            MainWindow = window;
            window.Activate();

            CrashLogger.Log("INFO", "MainWindow created and activated");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("FATAL", $"Failed to create window: {ex.Message}\n{ex.StackTrace}");
            CrashLogger.WriteCrash($"Window creation failed: {ex}");
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        var ex = e.Exception;
        CrashLogger.Log("ERROR", $"WinUI UnhandledException: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        CrashLogger.WriteCrash($"WinUI Unhandled: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        System.Diagnostics.Debug.WriteLine($"[UGA UNHANDLED] {ex.Message}\n{ex.StackTrace}");

        // Show error dialog on the main window's thread if possible
        try
        {
            MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                var dlg = new ContentDialog
                {
                    Title = "Error",
                    Content = $"{ex.Message}\n\n{ex.GetType().Name}\n\nLog saved to:\n{CrashLogger.LogFilePath}",
                    CloseButtonText = "OK",
                    XamlRoot = MainWindow?.Content?.XamlRoot,
                };
                _ = dlg.ShowAsync();
            });
        }
        catch { }
    }
}
