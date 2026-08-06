using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace UGA;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Static callback for HomePage to notify ArtifactsPage of new messages.
    /// ArtifactsPage registers itself when navigated to, unregisters when leaving.
    /// </summary>
    public static Action<List<ChatMessage>>? ArtifactsRefreshCallback;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += App_UnhandledException;
        CrashLogger.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        CrashLogger.Log("INFO", "OnLaunched: Creating MainWindow");

        try
        {
            // Show loading screen first
            var window = new MainWindow();
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            MainWindow = window;
            window.Activate();

            // Navigate to loading page first, then to chat after init
            window.NavigateToLoading();

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
