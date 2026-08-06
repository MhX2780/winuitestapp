using Microsoft.UI.Xaml.Controls;

namespace UGA;

public sealed partial class LoadingPage : Page
{
    public LoadingPage()
    {
        this.InitializeComponent();
        _ = InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        // Ensure directories exist
        System.IO.Directory.CreateDirectory(ConfigManager.WorkspaceDir);
        System.IO.Directory.CreateDirectory(ConfigManager.ChatHistoryDir);

        // Pre-load settings
        ConfigManager.LoadSettings();

        // Small delay for visual effect
        await System.Threading.Tasks.Task.Delay(600);

        // Signal that loading is complete — MainWindow will navigate to chat
        LoadingComplete?.Invoke(this, System.EventArgs.Empty);
    }

    public static event System.EventHandler? LoadingComplete;
}
