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
        SetupHandCursors();
    }

    private void UpdateVisibility()
    {
        WelcomePanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatListView.Visibility = _messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupHandCursors()
    {
        SetHand(SendButton);
        SetHand(UndoButton);
        SetHand(ClearButton);
        SetHand(NewChatButton);
    }

    private static void SetHand(UIElement el)
    {
        CursorHelper.SetHandOn(el);
    }

    private void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // Check Ctrl modifier
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool ctrlHeld = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrlHeld)
            {
                // Ctrl+Enter = Send
                e.Handled = true; // Prevent newline
                _ = SendMessageAsync();
            }
            // else: let AcceptsReturn handle Enter = newline
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
        try
        {
            var (result, success) = await ToolExecutor.ExecuteAsync("undo_last_change", "{}");
            _messages.Add(new() { Role = "system", Content = result });
            Bind();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UGA Undo Error] {ex.Message}");
        }
    }

    private async Task SendMessageAsync()
    {
        // Save text BEFORE anything else
        var text = InputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Check API key — show dialog if missing
        if (!ConfigManager.HasApiKey)
        {
            InputTextBox.IsEnabled = false;
            SendButton.IsEnabled = false;

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
            SendButton.IsEnabled = true;

            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(pwd.Password))
            {
                ConfigManager.SaveApiKey(pwd.Password);
                _service = new GeminiService();
                // Re-send with the same text (saved above)
                await SendMessageAsync();
            }
            return;
        }

        // Clear input and show user message in chat
        InputTextBox.Text = "";
        _messages.Add(new() { Role = "user", Content = text });
        _streamingMsg = null;
        Bind();

        // Update UI state
        ThinkingRing.IsActive = true;
        ThinkingRing.Visibility = Visibility.Visible;
        StatusText.Text = "Thinking...";
        SendButton.IsEnabled = false;

        TaskbarProgress.SetIndeterminate();

        _service ??= new GeminiService();

        // Detach first to prevent handler accumulation
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
            // Build history from previous messages only (not the one we just added)
            var history = _messages
                .Where(m => m.Role is "user" or "model")
                .Where(m => !(m.Role == "user" && m.Content == text)) // exclude current message
                .Select(m => new Dictionary<string, object>
                {
                    { "role", m.Role },
                    { "parts", new[] { new { text = m.Content } } }
                }).ToList();

            System.Diagnostics.Debug.WriteLine($"[UGA] Sending message, history={history.Count}, text={text.Length}");

            await _service.SendStreamingAsync(history, text, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UGA SendError] {ex.Message}\n{ex.StackTrace}");
            OnErr($"Error: {ex.Message}");
        }
    }

    private void OnToken(string t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(t)) return;
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
