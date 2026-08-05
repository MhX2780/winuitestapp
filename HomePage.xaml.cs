using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AdvaBrowser;

public sealed partial class HomePage : Page
{
    private GeminiService? _service;
    private CancellationTokenSource? _cts;
    private readonly List<ChatMessage> _messages = new();
    private ChatMessage? _streamingMsg;

    public HomePage()
    {
        this.InitializeComponent();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        WelcomePanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatListView.Visibility = _messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl) { e.Handled = true; _ = SendMessageAsync(); }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendMessageAsync();

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        _streamingMsg = null;
        Bind();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        ClearChat_Click(sender, e);
        MemoryManager.ClearExecutionLog();
        MemoryManager.LogMessage("system", "New chat started");
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var (result, success) = await ToolExecutor.ExecuteAsync("undo_last_change", "{}");
        _messages.Add(new() { Role = "system", Content = result });
        Bind();
    }

    private async Task SendMessageAsync()
    {
        var text = InputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Check API key
        if (!ConfigManager.HasApiKey)
        {
            var pwd = new PasswordBox { PlaceholderText = "Enter Gemini API Key..." };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Enter your Gemini API key to start chatting.",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.White)
            });
            panel.Children.Add(pwd);

            var dlg = new ContentDialog
            {
                Title = "API Key Required",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary
                && !string.IsNullOrWhiteSpace(pwd.Password))
            {
                ConfigManager.SaveApiKey(pwd.Password);
                _service = new GeminiService();
                await SendMessageAsync();
            }
            return;
        }

        InputTextBox.Text = "";
        _messages.Add(new() { Role = "user", Content = text });
        _streamingMsg = null;
        Bind();

        // Update UI state
        ThinkingRing.IsActive = true;
        ThinkingRing.Visibility = Visibility.Visible;
        StatusText.Text = "Thinking...";
        SendButton.IsEnabled = false;

        // Taskbar progress
        TaskbarProgress.SetIndeterminate();

        _service ??= new GeminiService();

        // Detach first to prevent handler accumulation on repeated sends
        _service.OnTokenReceived -= OnToken;
        _service.OnToolCallStarted -= OnToolStart;
        _service.OnToolCallCompleted -= OnToolDone;
        _service.OnError -= OnErr;
        _service.OnComplete -= OnDone;

        // Attach event handlers
        _service.OnTokenReceived += OnToken;
        _service.OnToolCallStarted += OnToolStart;
        _service.OnToolCallCompleted += OnToolDone;
        _service.OnError += OnErr;
        _service.OnComplete += OnDone;

        _cts = new();
        try
        {
            var history = _messages.Where(m => m.Role is "user" or "model").Select(m =>
                new Dictionary<string, object>
                {
                    { "role", m.Role },
                    { "parts", new[] { new { text = m.Content } } }
                }).ToList();

            await _service.SendStreamingAsync(history, text, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnErr($"Error: {ex.Message}"); }
    }

    private void OnToken(string t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_streamingMsg == null)
                _streamingMsg = new()
                {
                    Role = "model", Content = t,
                    IsStreaming = true, ModelName = _service?.CurrentModel
                };
            else
                _streamingMsg.Content += t;
            Bind();
            ScrollBottom();
        });
    }

    private void OnToolStart(string name)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = $"Tool: {name}...";
            _messages.Add(new()
            {
                Role = "tool", Content = $"Calling {name}...",
                IsToolCall = true, ToolName = name
            });
            Bind();
            ScrollBottom();
        });
    }

    private void OnToolDone(string name, bool ok)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = ok ? $"{name} done" : $"{name} failed";
            var last = _messages.LastOrDefault(m => m.IsToolCall && m.ToolName == name);
            if (last != null)
            {
                last.Content = $"{name}: {(ok ? "Done" : "Failed")}";
                last.ToolSuccess = ok;
            }
            Bind();
            ScrollBottom();
        });
    }

    private void OnErr(string err)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ThinkingRing.IsActive = false;
            ThinkingRing.Visibility = Visibility.Collapsed;
            StatusText.Text = "Error";
            SendButton.IsEnabled = true;
            TaskbarProgress.SetError();
            _messages.Add(new() { Role = "system", Content = err });
            Bind();
            ScrollBottom();
        });
    }

    private void OnDone()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ThinkingRing.IsActive = false;
            ThinkingRing.Visibility = Visibility.Collapsed;
            StatusText.Text = "Ready";
            SendButton.IsEnabled = true;
            TaskbarProgress.Clear();

            if (_streamingMsg != null)
            {
                _streamingMsg.IsStreaming = false;
                MemoryManager.LogMessage("model", _streamingMsg.Content, _streamingMsg.ModelName);
                _streamingMsg = null;
            }
            Bind();
            ScrollBottom();
        });
    }

    private void Bind()
    {
        UpdateVisibility();
        ChatListView.ItemsSource = null;
        ChatListView.ItemsSource = _messages;
    }

    private void ScrollBottom()
    {
        if (ChatListView.Items.Count > 0)
            ChatListView.ScrollIntoView(ChatListView.Items[^1]);
    }
}
