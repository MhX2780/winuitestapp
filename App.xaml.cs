using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdvaBrowser;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var window = new MainWindow();
        window.SystemBackdrop = new DesktopAcrylicBackdrop();
        MainWindow = window;
        window.Activate();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"[UGA UNHANDLED] {e.Exception.Message}\n{e.Exception.StackTrace}");
        // Show error dialog on the main window's thread if possible
        try
        {
            MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                var dlg = new ContentDialog
                {
                    Title = "Error",
                    Content = $"{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
                    CloseButtonText = "OK",
                    XamlRoot = MainWindow?.Content?.XamlRoot,
                };
                _ = dlg.ShowAsync();
            });
        }
        catch { }
    }
}
