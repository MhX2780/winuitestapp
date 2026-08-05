using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private void InputTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool ctrlHeld = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrlHeld)
            {
                e.Handled = true;
                _ = SendMessageAsync();
            }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendMessageAsync();

    // Public methods called from MainWindow navigation actions
    public void ExecuteClearChat()
    {
        _messages.Clear();
        _streamingMsg = null;
        Bind();
    }

    public void ExecuteNewChat()
    {
        ExecuteClearChat();
        MemoryManager.ClearExecutionLog();
        MemoryManager.LogMessage("system", "New chat started");
    }

    public async void ExecuteUndo()
    {
        try
        {
            var (result, success) = await ToolExecutor.ExecuteAsync("undo_last_change", "{}");
            _messages.Add(new() { Role = "system", Content = result });
            Bind();
            ScrollBottom();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UGA Undo Error] {ex.Message}");
        }
    }

    private async Task<bool> ShowApiKeyDialog()
    {
        InputTextBox.IsEnabled = false;

        var pwd = new PasswordBox { PlaceholderText = "Enter Gemini API Key..." };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter your Gemini API key to start chatting.",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
        });
        panel.Children.Add(pwd);

        var dlg = new ContentDialog
        {
            Title = "API Key Required",
            Content = panel,
            PrimaryButtonText = "Save & Send",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        var result = await dlg.ShowAsync();
        InputTextBox.IsEnabled = true;

        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(pwd.Password))
        {
            ConfigManager.SaveApiKey(pwd.Password);
            _service = new GeminiService();
            return true;
        }
        return false;
    }

    // Fade animation for Send button -> Spinner
    private async Task ShowSendSpinner()
    {
        SendProgressRing.Visibility = Visibility.Visible;
        SendProgressRing.IsActive = true;
        SendProgressRing.Opacity = 0;

        for (int i = 5; i >= 0; i--)
        {
            SendButton.Opacity = i / 5.0;
            SendProgressRing.Opacity = 1 - (i / 5.0);
            await System.Threading.Tasks.Task.Delay(35);
        }
        SendButton.Visibility = Visibility.Collapsed;
    }

    private async Task ShowSendButton()
    {
        SendButton.Visibility = Visibility.Visible;
        SendButton.Opacity = 0;

        for (int i = 0; i <= 5; i++)
        {
            SendButton.Opacity = i / 5.0;
            SendProgressRing.Opacity = 1 - (i / 5.0);
            await System.Threading.Tasks.Task.Delay(35);
        }
        SendProgressRing.IsActive = false;
        SendProgressRing.Visibility = Visibility.Collapsed;
    }

    private async Task SendMessageAsync()
    {
        var text = InputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        CrashLogger.Log("INFO", $"SendMessageAsync: textLen={text.Length}");

        if (!ConfigManager.HasApiKey)
        {
            var keyEntered = await ShowApiKeyDialog();
            if (!keyEntered) return;
        }

        InputTextBox.Text = "";
        _messages.Add(new() { Role = "user", Content = text });
        _streamingMsg = null;
        Bind();

        StatusText.Text = "Thinking...";
        await ShowSendSpinner();
        TaskbarProgress.SetIndeterminate();

        _service ??= new GeminiService();

        _service.OnTokenReceived -= OnToken;
        _service.OnToolCallStarted -= OnToolStart;
        _service.OnToolCallCompleted -= OnToolDone;
        _service.OnError -= OnErr;
        _service.OnComplete -= OnDone;

        _service.OnTokenReceived += OnToken;
        _service.OnToolCallStarted += OnToolStart;
        _service.OnToolCallCompleted += OnToolDone;
        _service.OnError += OnErr;
        _service.OnComplete += OnDone;

        _cts = new();
        try
        {
            var history = _messages
                .Where(m => m.Role is "user" or "model")
                .Where(m => !(m.Role == "user" && m.Content == text))
                .Select(m => new Dictionary<string, object>
                {
                    { "role", (object)m.Role },
                    { "parts", (object)new List<object> { new Dictionary<string, object> { { "text", (object?)m.Content ?? "" } } } }
                }).ToList();

            await _service.SendStreamingAsync(history, text, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"SendMessageAsync: {ex.GetType().Name}: {ex.Message}");
            OnErr($"Error: {ex.Message}");
        }
    }

    private void OnToken(string t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(t)) return;
            if (_streamingMsg == null)
            {
                _streamingMsg = new()
                {
                    Role = "model", Content = t,
                    IsStreaming = true, ModelName = _service?.CurrentModel
                };
                _messages.Add(_streamingMsg);
            }
            else
            {
                _streamingMsg.Content += t;
            }
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
                Role = "tool", Content = "Running...",
                IsToolCall = true, ToolName = name,
                ToolCallStatus = "running"
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
                last.Content = ok ? "Completed" : "Failed";
                last.ToolSuccess = ok;
                last.ToolCallStatus = ok ? "done" : "failed";
            }
            Bind();
            ScrollBottom();
        });
    }

    private void OnErr(string err)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            StatusText.Text = "Error";
            await ShowSendButton();
            TaskbarProgress.SetError();
            _messages.Add(new() { Role = "system", Content = err });
            Bind();
            ScrollBottom();
        });
    }

    private void OnDone()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            StatusText.Text = "Ready";
            await ShowSendButton();
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
