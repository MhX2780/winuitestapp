using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Newtonsoft.Json;

namespace AdvaBrowser;

public sealed partial class HomePage : Page
{
    private GeminiService? _service;
    private MultiAgentOrchestrator? _orchestrator;
    private CancellationTokenSource? _cts;
    private readonly List<ChatMessage> _messages = new();
    private ChatMessage? _streamingMsg;

    public HomePage()
    {
        this.InitializeComponent();
        LoadChatHistory();
        UpdateVisibility();
        Bind();
    }

    private void UpdateVisibility()
    {
        WelcomePanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatListView.Visibility = _messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Loaded handler for the RichTextBlock in model response template.
    /// Renders markdown content into the RichTextBlock.
    /// </summary>
    private void ModelRichText_Loaded(object sender, RoutedEventArgs e)
    {
        RenderMarkdown(sender);
    }

    private void ModelRichText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (e.NewValue != null)
            RenderMarkdown(sender);
    }

    private void RenderMarkdown(object sender)
    {
        if (sender is RichTextBlock rtb && rtb.DataContext is ChatMessage msg)
        {
            rtb.Blocks.Clear();
            var blocks = MarkdownRenderer.ParseBlocks(msg.Content ?? "");
            foreach (var block in blocks)
                rtb.Blocks.Add(block);
        }
    }

    private void InputTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool shiftHeld = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            if (!shiftHeld)
            {
                e.Handled = true;
                _ = SendMessageAsync();
            }
            else
            {
                // Shift+Enter: insert newline manually since AcceptsReturn=False
                e.Handled = true;
                var tb = (TextBox)sender;
                var caret = tb.SelectionStart;
                tb.Text = tb.Text.Insert(caret, "\r");
                tb.SelectionStart = caret + 1;
            }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendMessageAsync();

    // Public methods called from MainWindow navigation actions
    public void ExecuteClearChat()
    {
        _messages.Clear();
        _streamingMsg = null;
        SaveChatHistory();
        Bind();
    }

    public void ExecuteNewChat()
    {
        // Archive current chat before clearing
        ArchiveCurrentChat();
        ExecuteClearChat();
        MemoryManager.ClearExecutionLog();
        MemoryManager.LogMessage("system", "New chat started");
    }

    private void ArchiveCurrentChat()
    {
        if (_messages.Count == 0) return;
        try
        {
            var sessions = ConfigManager.GetChatSessionFiles();
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var archivePath = Path.Combine(ConfigManager.ChatHistoryDir, $"chat_{ts}.json");
            // Get first user message as preview
            var preview = _messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
            if (preview.Length > 50) preview = preview[..50] + "...";
            var json = JsonConvert.SerializeObject(_messages, Formatting.Indented);
            File.WriteAllText(archivePath, json);
            CrashLogger.Log("INFO", $"Chat archived: {archivePath}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to archive chat: {ex.Message}");
        }
    }

    public async void ShowChatHistory()
    {
        var sessions = ConfigManager.GetChatSessionFiles();
        var panel = new StackPanel { Spacing = 8 };

        // Also include the active chat if it has messages
        if (File.Exists(ConfigManager.ActiveChatFile))
        {
            try
            {
                var activeJson = File.ReadAllText(ConfigManager.ActiveChatFile);
                var activeMsgs = JsonConvert.DeserializeObject<List<ChatMessage>>(activeJson);
                if (activeMsgs != null && activeMsgs.Count > 0)
                {
                    sessions.Insert(0, ConfigManager.ActiveChatFile); // Add at beginning
                }
            }
            catch { }
        }

        if (sessions.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No previous chat sessions found.", Opacity = 0.7 });
        }
        else
        {
            var listView = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinHeight = 200,
                MaxHeight = 400,
                Width = 500,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            foreach (var filePath in sessions.OrderByDescending(f => f))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var display = fileName.Replace("chat_", "").Replace("_", " ");
                // Try to get preview
                string preview = "";
                try
                {
                    var chatJson = File.ReadAllText(filePath);
                    var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(chatJson);
                    var first = msgs?.FirstOrDefault(m => m.Role == "user");
                    if (first != null)
                    {
                        preview = first.Content ?? "";
                        if (preview.Length > 60) preview = preview[..60] + "...";
                        preview = $" — \"{preview}\"";
                    }
                }
                catch { }

                listView.Items.Add(new TextBlock
                {
                    Text = display + preview,
                    FontSize = 14,
                    Tag = filePath,
                });
            }

            panel.Children.Add(new TextBlock { Text = "Select a chat session to load:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(listView);
        }

        var dlg = new ContentDialog
        {
            Title = "Chat History",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };

        // Double-click on a session to load it and close the dialog
        if (sessions.Count > 0)
        {
            var selectedListView = panel.Children.OfType<ListView>().FirstOrDefault();
            if (selectedListView != null)
            {
                selectedListView.DoubleTapped += (s, e) =>
                {
                    if (selectedListView.SelectedItem is TextBlock tb && tb.Tag is string selectedPath)
                    {
                        try
                        {
                            var json = File.ReadAllText(selectedPath);
                            var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(json);
                            if (msgs != null)
                            {
                                _messages.Clear();
                                _messages.AddRange(msgs);
                                SaveChatHistory();
                                Bind();
                                ScrollBottom();
                                CrashLogger.Log("INFO", $"Loaded chat session: {selectedPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            CrashLogger.Log("ERROR", $"Failed to load session: {ex.Message}");
                        }
                    }
                    dlg.Hide();
                };
            }
        }

        await dlg.ShowAsync();
    }

    // Chat history persistence
    private void LoadChatHistory()
    {
        try
        {
            if (File.Exists(ConfigManager.ActiveChatFile))
            {
                var json = File.ReadAllText(ConfigManager.ActiveChatFile);
                var msgs = JsonConvert.DeserializeObject<List<ChatMessage>>(json);
                if (msgs != null)
                {
                    _messages.AddRange(msgs);
                    CrashLogger.Log("INFO", $"Loaded {_messages.Count} messages from chat history");
                }
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to load chat history: {ex.Message}");
        }
    }

    private void SaveChatHistory()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_messages, Formatting.Indented);
            File.WriteAllText(ConfigManager.ActiveChatFile, json);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WARN", $"Failed to save chat history: {ex.Message}");
        }
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
        panel.Children.Add(new TextBlock { Text = "Enter your Gemini API key." });
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

    // Fade animations
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
        ModelStatusText.Text = $"[{_service.CurrentModel}]";

        _service.OnTokenReceived -= OnToken;
        _service.OnToolCallStarted -= OnToolStart;
        _service.OnToolCallCompleted -= OnToolDone;
        _service.OnError -= OnErr;
        _service.OnComplete -= OnDone;
        _service.OnModelSwitched -= OnModelSwitch;

        _service.OnTokenReceived += OnToken;
        _service.OnToolCallStarted += OnToolStart;
        _service.OnToolCallCompleted += OnToolDone;
        _service.OnError += OnErr;
        _service.OnComplete += OnDone;
        _service.OnModelSwitched += OnModelSwitch;

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

            // ── Multi-Agent path ──
            bool usedMa = false;
            if (ConfigManager.MultiAgentEnabled)
            {
                _orchestrator ??= new MultiAgentOrchestrator();
                _orchestrator.OnEvent -= OnMultiAgentEvent;
                _orchestrator.OnEvent += OnMultiAgentEvent;

                var maResult = await _orchestrator.RunAsync(text, _cts.Token);
                _orchestrator.OnEvent -= OnMultiAgentEvent;

                if (!string.IsNullOrEmpty(maResult))
                {
                    usedMa = true;
                    _streamingMsg = new()
                    {
                        Role = "model", Content = maResult,
                        ModelName = "Multi-Agent"
                    };
                    _messages.Add(_streamingMsg);
                    MemoryManager.LogMessage("model", maResult, "Multi-Agent");
                    _streamingMsg = null;
                    Bind();
                    ScrollBottom();
                }
            }

            if (!usedMa)
            {
                await _service.SendStreamingAsync(history, text, _cts.Token);
            }
            else
            {
                // Multi-agent cleanup: hide spinner, clear taskbar, save
                await ShowSendButton();
                TaskbarProgress.Clear();
                StatusText.Text = "Ready";
                SaveChatHistory();
            }
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

    private void OnModelSwitch(string model)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = $"Switched to {model}";
            ModelStatusText.Text = $"[{model}]";
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
            // Consolidate system/tool messages into a single action card
            ConsolidateSystemActions();
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

    private void OnMultiAgentEvent(MultiAgentEvent evt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (evt.Kind)
            {
                case "classified_result":
                    var cx = evt.Data?.GetValueOrDefault("complexity") ?? "";
                    var tt = evt.Data?.GetValueOrDefault("task_type") ?? "";
                    StatusText.Text = $"Classified: {cx} / {tt}";
                    break;
                case "plan_ready":
                    StatusText.Text = "Plan ready — executing steps...";
                    break;
                case "step_start":
                    var stepNum = evt.Data?.GetValueOrDefault("step_number")?.ToString() ?? "?";
                    var totalSteps = evt.Data?.GetValueOrDefault("total_steps")?.ToString() ?? "?";
                    var desc = evt.Data?.GetValueOrDefault("description")?.ToString() ?? "";
                    StatusText.Text = $"Step {stepNum}/{totalSteps}: {desc}";

                    // Find or create the task list card
                    var taskCard = _messages.LastOrDefault(m => m.IsTaskList);
                    if (taskCard == null)
                    {
                        taskCard = new ChatMessage
                        {
                            Role = "system",
                            Content = "Task Plan",
                            IsTaskList = true,
                            TaskItems = new List<TaskItem>()
                        };
                        _messages.Add(taskCard);
                    }

                    // Add new task item
                    taskCard.TaskItems.Add(new TaskItem
                    {
                        Description = $"Step {stepNum}: {desc}",
                        Status = "running"
                    });
                    taskCard.Content = $"Executing step {stepNum}/{totalSteps}...";
                    Bind();
                    ScrollBottom();
                    break;
                case "step_done":
                    var doneNum = evt.Data?.GetValueOrDefault("step_number")?.ToString() ?? "?";
                    var doneDesc = evt.Data?.GetValueOrDefault("description")?.ToString() ?? "";
                    var doneSuccess = evt.Data?.GetValueOrDefault("success")?.ToString() ?? "True";
                    StatusText.Text = $"Step {doneNum} done";

                    var tlCard = _messages.LastOrDefault(m => m.IsTaskList);
                    if (tlCard?.TaskItems != null)
                    {
                        var taskItem = tlCard.TaskItems.FirstOrDefault(t => t.Description.Contains($"Step {doneNum}"));
                        if (taskItem != null)
                        {
                            taskItem.Status = doneSuccess?.ToString() == "True" ? "done" : "failed";
                            taskItem.Result = doneDesc;
                        }
                        // Count progress
                        var completed = tlCard.TaskItems.Count(t => t.Status is "done" or "failed").ToString();
                        var total = tlCard.TaskItems.Count.ToString();
                        tlCard.Content = $"Progress: {completed}/{total} steps completed";
                    }
                    Bind();
                    ScrollBottom();
                    break;
                case "review_done":
                    var passed = evt.Data?.GetValueOrDefault("passed");
                    StatusText.Text = passed?.ToString() == "True" ? "Review: Passed" : "Review: Needs revision";
                    break;
            }
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
            SaveChatHistory();
            Bind();
            ScrollBottom();

            // Notify ArtifactsPage to refresh (if it exists in the nav frame cache)
            NotifyArtifactsRefresh();
        });
    }

    /// <summary>
    /// Consolidates consecutive tool/system messages into a single IsSystemAction card.
    /// Groups: tool calls (done/failed), system messages with content like "Created file...", "Edited...".
    /// </summary>
    private void ConsolidateSystemActions()
    {
        if (_messages.Count < 2) return;
        var actions = new List<string>();
        int i = _messages.Count - 1;

        // Walk backwards collecting consecutive tool/system messages
        while (i >= 0)
        {
            var msg = _messages[i];
            if (msg.IsToolCall && msg.ToolCallStatus != "running")
            {
                var icon = msg.ToolSuccess ? "\u2713" : "\u2717";
                actions.Add($"{icon} {msg.ToolName}: {msg.Content}");
                i--;
            }
            else if (msg.Role == "system" && !string.IsNullOrEmpty(msg.Content))
            {
                actions.Add(msg.Content);
                i--;
            }
            else break;
        }

        if (actions.Count < 2) return;

        // Remove the collected messages (from i+1 to end)
        _messages.RemoveRange(i + 1, _messages.Count - (i + 1));

        // Add single consolidated card
        _messages.Add(new ChatMessage
        {
            Role = "system",
            Content = string.Join("\n", actions),
            IsSystemAction = true,
            ActionItems = actions
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
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(50);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ChatListView.Items.Count > 0)
                        ChatListView.ScrollIntoView(ChatListView.Items[^1]);
                });
            });
        }
    }

    /// <summary>
    /// Notifies the ArtifactsPage (if registered) to refresh with latest messages.
    /// ArtifactsPage registers/unregisters itself via OnNavigatedTo/From.
    /// </summary>
    private void NotifyArtifactsRefresh()
    {
        try
        {
            App.ArtifactsRefreshCallback?.Invoke(_messages);
        }
        catch { }
    }
}
