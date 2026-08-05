using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System.Runtime.InteropServices;

namespace AdvaBrowser;

// WinRT interop for FolderPicker - desktop window association
[ComImport]
[Guid("79c084eb-0b6e-4e39-a787-5a99b9b53019")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithWindow
{
    void Initialize(IntPtr hwnd);
}

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();
        LoadUI();
    }

    private void LoadUI()
    {
        var s = ConfigManager.Settings;
        ApiKeyBox.Password = ConfigManager.LoadSavedApiKey();
        KeyStatus.Text = string.IsNullOrEmpty(ApiKeyBox.Password) ? "No API key set." : "API key saved.";

        // Set model combo
        for (int i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (((ComboBoxItem)ModelCombo.Items[i]).Content?.ToString()
                == (ConfigManager.ModelChain.FirstOrDefault()?.Name ?? ""))
            {
                ModelCombo.SelectedIndex = i;
                break;
            }
        }

        ModelChainBox.Text = string.Join(", ", ConfigManager.ModelChain.Select(m => m.Name));
        WorkspaceBox.Text = s.WorkspacePath;
        UpdateWsStatus();

        // Providers
        ClaudeKeyBox.Password = ConfigManager.LoadProviderApiKey("claude");
        OpenAIKeyBox.Password = ConfigManager.LoadProviderApiKey("openai");
        PuterTokenBox.Password = ConfigManager.LoadPuterToken();

        // Memory
        SystemPromptBox.Text = s.SystemPromptOverride;
        MaxHistory.Value = s.MaxHistoryMessages;

        // Deep Thinking
        DeepThinking.IsOn = s.DeepThinkingEnabled;
        ThinkBudget.Value = s.DeepThinkingBudget;
        ThinkInclude.IsOn = s.DeepThinkingIncludeThoughts;

        // Multi-Agent
        MultiAgent.IsOn = s.MultiAgentEnabled;
        SetCombo(ClassifierCombo, s.MultiAgentRoles?.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite");
        SetCombo(PlannerCombo, s.MultiAgentRoles?.GetValueOrDefault("planner") ?? "gemini-3.6-flash");
        SetCombo(ExecutorCombo, s.MultiAgentRoles?.GetValueOrDefault("executor") ?? "gemini-3.5-flash");
        SetCombo(ReviewerCombo, s.MultiAgentRoles?.GetValueOrDefault("reviewer") ?? "gemini-2.5-flash-lite");
    }

    private void UpdateWsStatus()
    {
        var p = WorkspaceBox.Text?.Trim();
        if (string.IsNullOrEmpty(p))
            WorkspaceStatus.Text = $"Default: {ConfigManager.WorkspaceDir}";
        else if (Directory.Exists(p))
            WorkspaceStatus.Text = "Directory exists.";
        else
            WorkspaceStatus.Text = "Will be created on first use.";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        ((IInitializeWithWindow)picker).Initialize(hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            WorkspaceBox.Text = folder.Path;
            UpdateWsStatus();
        }
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var k = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(k)) { KeyStatus.Text = "Enter a valid key."; return; }
        ConfigManager.SaveApiKey(k);
        KeyStatus.Text = "Key saved.";
    }

    private void ResetKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = "";
        ConfigManager.DeleteApiKey();
        KeyStatus.Text = "Key removed.";
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var s = ConfigManager.Settings;

        // Model chain
        var modelName = ((ComboBoxItem)ModelCombo.SelectedItem)?.Content?.ToString()
            ?? "gemini-2.5-flash";
        var chainNames = ModelChainBox.Text?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? new[] { modelName };
        s.ModelChain = chainNames.Select(n => new ModelChainEntry { Name = n }).ToList();

        // Workspace
        s.WorkspacePath = WorkspaceBox.Text?.Trim() ?? "";

        // Providers
        ConfigManager.SaveProviderApiKey("claude", ClaudeKeyBox.Password?.Trim() ?? "");
        ConfigManager.SaveProviderApiKey("openai", OpenAIKeyBox.Password?.Trim() ?? "");
        if (!string.IsNullOrEmpty(PuterTokenBox.Password?.Trim()))
            ConfigManager.SavePuterToken(PuterTokenBox.Password.Trim());

        // Multi-Agent
        s.MultiAgentEnabled = MultiAgent.IsOn;
        s.MultiAgentRoles = new()
        {
            ["classifier"] = ((ComboBoxItem)ClassifierCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-2.5-flash-lite",
            ["planner"] = ((ComboBoxItem)PlannerCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-3.6-flash",
            ["executor"] = ((ComboBoxItem)ExecutorCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-3.5-flash",
            ["reviewer"] = ((ComboBoxItem)ReviewerCombo.SelectedItem)?.Content?.ToString()
                ?? "gemini-2.5-flash-lite",
        };

        // Deep Thinking
        s.DeepThinkingEnabled = DeepThinking.IsOn;
        s.DeepThinkingBudget = (int)ThinkBudget.Value;
        s.DeepThinkingIncludeThoughts = ThinkInclude.IsOn;

        // Memory
        s.SystemPromptOverride = SystemPromptBox.Text?.Trim() ?? "";
        s.MaxHistoryMessages = (int)MaxHistory.Value;

        ConfigManager.SaveSettings();
        SaveStatus.Text = "All settings saved!";

        // Clear status after 3s
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            DispatcherQueue.TryEnqueue(() => SaveStatus.Text = "");
        });
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        ConfigManager.Settings = new();
        ConfigManager.SaveSettings();
        LoadUI();
        SaveStatus.Text = "Reset to defaults.";
    }

    private static void SetCombo(ComboBox combo, string text)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((ComboBoxItem)combo.Items[i]).Content?.ToString() == text)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }
}
