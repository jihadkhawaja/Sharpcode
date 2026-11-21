using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using Sharpcode.Core;

namespace Sharpcode;

public partial class MainWindow
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Dictionary<ModelProvider, string> sessionKeys = [];
    private readonly ObservableCollection<ChatEntry> conversation = [];
    private readonly ObservableCollection<ProposedChange> proposals = [];
    private readonly List<ChatMessage> history = [];
    private ModelClient model = null!;
    private AgentHistory? persistentHistory;
    private CancellationTokenSource? agentCancellation;
    private bool agentApplying;
    private string? applyingPath;
    private Suggestion? suggestion;

    private sealed record Suggestion(EditorTab Tab, ITextSourceVersion Version, int Offset, string Text);

    private void InitializeAgent()
    {
        model = new ModelClient(http);
        Transcript.ItemsSource = conversation;
        ReviewFiles.ItemsSource = proposals;
        proposals.CollectionChanged += (_, _) =>
        {
            ReviewPane.IsVisible = proposals.Count > 0;
            ReviewTitle.Text = proposals.Count == 1 ? "1 DRAFT FILE" : $"{proposals.Count} DRAFT FILES";
            if (proposals.Count > 0 && ReviewFiles.SelectedIndex < 0)
                ReviewFiles.SelectedIndex = 0;
        };
        UpdateProvider();
        ToolTip.SetTip(SendButton, $"Send ({PrimaryLabel}Enter)");
    }

    private string? KeyFor(ModelProvider provider) => sessionKeys.GetValueOrDefault(provider);

    private async Task EditProviderAsync()
    {
        var saved = await ProviderWindow.EditAsync(this, preferences.Model, KeyFor, model);
        if (saved is null)
            return;
        sessionKeys[saved.Settings.Provider] = saved.Key;
        preferences = preferences with { Model = saved.Settings };
        UpdateProvider();
        await PersistPreferencesAsync();
        Status($"Provider configured · {ProviderWindow.ProviderLabel(saved.Settings.Provider)}");
    }

    private void UpdateProvider()
    {
        var settings = preferences.Model;
        ModelLabel.Text = string.IsNullOrWhiteSpace(settings.Model)
            ? "Choose a model…" : $"{ProviderWindow.ProviderLabel(settings.Provider)} · {settings.Model}";
        ToolTip.SetTip(ModelLabel, string.IsNullOrWhiteSpace(settings.Model)
            ? "Open provider settings" : $"{settings.Model} · {settings.Endpoint}");
        ConfigureProvider.IsVisible = string.IsNullOrWhiteSpace(settings.Model);
        UpdateAgentComposer();
    }

    private void UpdateAgentComposer()
    {
        if (SendButton is null || PromptBox is null)
            return;
        var busy = agentCancellation is not null;
        var configured = !string.IsNullOrWhiteSpace(preferences.Model.Model);
        AgentEditMode.IsEnabled = workspace is not null;
        if (workspace is null && AgentModePicker.SelectedIndex == 1)
            AgentModePicker.SelectedIndex = 0;
        PromptBox.PlaceholderText = AgentModePicker.SelectedIndex == 1
            ? "Describe the changes to draft" : "Ask a question about your code";
        ToolTip.SetTip(AgentModePicker, workspace is null ? "Open a folder to draft file changes."
            : "Ask reads workspace files. Edit files can also draft changes for review.");
        ChatWelcomeDescription.Text = workspace is null
            ? "Open a folder to give the agent workspace context."
            : "Ask a question or choose Edit files to draft a change for review.";
        AgentStarters.IsVisible = workspace is not null;
        SendButton.Content = configured ? "Send" : "Set Up & Send";
        UpdateSendState();
        NewChatButton.IsEnabled = !busy;
        ReviewButton.IsEnabled = !busy && !agentApplying;
        UpdateAttachment();
    }

    private void UpdateSendState()
    {
        if (SendButton is not null && PromptBox is not null)
            SendButton.IsEnabled = agentCancellation is null && !agentApplying && !string.IsNullOrWhiteSpace(PromptBox.Text);
    }

    private void UpdateAttachment()
    {
        if (AttachEditor is null)
            return;
        var canAttach = CanAttachActiveEditor;
        AttachEditor.IsEnabled = canAttach;
        ExplainFileButton.IsVisible = canAttach;
        AttachmentLabel.Text = canAttach ? $"Attach {activeTab!.Title}" : "Attach editor";
        ToolTip.SetTip(AttachEditor, canAttach ? $"Send the selection or nearby lines from {activeTab!.Title}"
            : "Open a source file to attach its text.");
        if (!canAttach)
            AttachEditor.IsChecked = false;
    }

    private bool CanAttachActiveEditor => activeTab is { } tab
        && (tab.Path is null || Workspace.IsAgentVisible(tab.Path.Replace('\\', '/')));

    private void SetAgentNotice(string? text, bool error = false)
    {
        AgentNotice.Text = text;
        AgentNotice.IsVisible = !string.IsNullOrEmpty(text);
        AgentNotice.Classes.Set("error", error);
    }

    private void ResetConversation()
    {
        history.Clear();
        conversation.Clear();
        Transcript.IsVisible = false;
        ChatWelcome.IsVisible = true;
        SetAgentNotice(null);
    }

    private void FocusAgent()
    {
        SetAgentVisible(true);
        PromptBox.Focus();
    }

    private void FillAgentPrompt(string text, bool attachEditor)
    {
        FocusAgent();
        if (!string.IsNullOrWhiteSpace(PromptBox.Text))
            return;
        AgentModePicker.SelectedIndex = 0;
        AttachEditor.IsChecked = attachEditor;
        PromptBox.Text = text;
        PromptBox.CaretIndex = text.Length;
    }

    private async Task LoadHistoryAsync()
    {
        if (workspace is not { } current || !preferences.Model.KeepHistory || !AgentHistory.Supported)
            return;
        persistentHistory ??= new AgentHistory();
        var messages = await persistentHistory.LoadAsync(current.Root, lifetime.Token);
        foreach (var message in messages)
        {
            history.Add(message);
            AddChat(message.Role == "user" ? "You" : "Sharpcode", message.Text);
        }
    }

    private async Task ClearChatAsync()
    {
        if (agentCancellation is not null || switchingWorkspace)
        {
            Status("Stop the current run before starting a new chat.");
            return;
        }
        if (workspace is { } current && AgentHistory.Supported && (preferences.Model.KeepHistory || persistentHistory is not null))
        {
            persistentHistory ??= new AgentHistory();
            await persistentHistory.ClearAsync(current.Root, lifetime.Token);
        }
        ResetConversation();
        Status("New conversation");
        PromptBox.Focus();
    }

    private ChatEntry AddChat(string role, string text)
    {
        var entry = new ChatEntry(role, text);
        conversation.Add(entry);
        if (conversation.Count > 100)
            conversation.RemoveAt(0);
        Transcript.IsVisible = true;
        ChatWelcome.IsVisible = false;
        Transcript.ScrollIntoView(entry);
        return entry;
    }

    private async Task SendAsync()
    {
        if (agentCancellation is not null || agentApplying || switchingWorkspace || string.IsNullOrWhiteSpace(PromptBox.Text))
            return;
        if (string.IsNullOrWhiteSpace(preferences.Model.Model))
        {
            await EditProviderAsync();
            if (string.IsNullOrWhiteSpace(preferences.Model.Model))
                return;
        }

        var settings = preferences.Model;
        var current = workspace;
        var mode = AgentModePicker.SelectedIndex == 0 ? AgentMode.Ask : AgentMode.Edit;
        var prompt = PromptBox.Text.Trim();
        var context = AttachEditor.IsChecked == true ? ActiveContext() : null;
        var prior = proposals.ToArray();
        var conversationContext = history.TakeLast(14).ToArray();
        PromptBox.Text = "";
        SetAgentNotice("Connecting to the model…");
        SetAgentVisible(true);
        AddChat("You", prompt);
        var response = AddChat("Sharpcode", "Connecting…");
        using var cancel = BeginAgentRun(TimeSpan.FromMinutes(6));
        var pending = new ConcurrentQueue<string>();
        var streamed = new StringBuilder();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        void Flush()
        {
            if (!pending.TryDequeue(out var chunk))
                return;
            var scroll = Transcript.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var following = scroll is null || scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 48;
            do { streamed.Append(chunk); } while (pending.TryDequeue(out chunk));
            response.Update(streamed.ToString());
            if (AgentNotice.Text != "Writing a response…")
                SetAgentNotice("Writing a response…");
            if (following)
                Transcript.ScrollIntoView(response);
        }
        timer.Tick += (_, _) => Flush();
        timer.Start();
        try
        {
            var result = await new CodingAgent(model).RunAsync(settings, KeyFor(settings.Provider), mode, current,
                conversationContext, prompt, context, pending.Enqueue,
                operation => Dispatcher.UIThread.Post(() =>
                {
                    if (agentCancellation == cancel)
                        SetAgentNotice(operation switch
                        {
                            "list files" => "Checking workspace files…",
                            "search code" => "Searching the workspace…",
                            "read file" => "Reading a file…",
                            _ => "Preparing draft changes…"
                        });
                }), cancel.Token, prior);
            timer.Stop();
            response.Update(result.Text);
            if (mode == AgentMode.Edit)
            {
                proposals.Clear();
                foreach (var change in result.Changes)
                    proposals.Add(change);
            }
            history.Add(new("user", CodingAgent.Limit(prompt, 3500)));
            history.Add(new("assistant", CodingAgent.Limit(result.Text, 5500)));
            if (history.Count > 24)
                history.RemoveRange(0, history.Count - 24);
            SetAgentNotice(null);
            Status(proposals.Count switch
            {
                0 => "Agent finished",
                1 => "1 draft file ready for review",
                _ => $"{proposals.Count} draft files ready for review"
            });
            if (settings.KeepHistory && AgentHistory.Supported && current is not null)
            {
                persistentHistory ??= new AgentHistory();
                await TryAsync(() => persistentHistory.AppendAsync(current.Root, prompt, result.Text, lifetime.Token));
            }
        }
        catch (OperationCanceledException)
        {
            Flush();
            response.Update(streamed.Length == 0 ? "Stopped." : streamed + "\n\nStopped.");
            RestorePrompt(prompt);
            SetAgentNotice("Run stopped.");
            Status("Agent run stopped.");
        }
        catch (Exception exception)
        {
            Flush();
            response.Update(streamed.Length == 0 ? "The request couldn’t be completed." : streamed.ToString());
            RestorePrompt(prompt);
            SetAgentNotice(exception.Message, error: true);
            Status("Agent run ended with an error.");
        }
        finally
        {
            timer.Stop();
            EndAgentRun();
        }
    }

    private void RestorePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            PromptBox.Text = prompt;
            PromptBox.CaretIndex = prompt.Length;
        }
    }

    private CancellationTokenSource BeginAgentRun(TimeSpan duration)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(duration);
        agentCancellation = cancel;
        UpdateAgentComposer();
        CancelAgent.IsVisible = true;
        return cancel;
    }

    private void EndAgentRun()
    {
        agentCancellation = null;
        UpdateAgentComposer();
        CancelAgent.IsVisible = false;
    }

    private string? ActiveContext()
    {
        if (activeTab is not { } tab)
            return null;
        var display = tab.Path is null ? tab.Title : workspace?.Relative(tab.Path) ?? tab.Title;
        if (!Workspace.IsAgentVisible(display.Replace('\\', '/')))
            throw new IOException("This editor contains a credential file.");
        if (Editor.SelectionLength > 0)
            return $"{display}\nSelection:\n{tab.Document.GetText(Editor.SelectionStart, Math.Min(Editor.SelectionLength, 11_000))}";
        var offset = Editor.CaretOffset;
        var start = Math.Max(0, offset - 5500);
        var length = Math.Min(11_000, tab.Document.TextLength - start);
        return $"{display} · line {Editor.TextArea.Caret.Line}\n{tab.Document.GetText(start, length)}";
    }

    private async Task SuggestAsync()
    {
        if (activeTab is not { } tab || agentCancellation is not null || agentApplying || switchingWorkspace)
            return;
        if (string.IsNullOrWhiteSpace(preferences.Model.Model))
        {
            await EditProviderAsync();
            if (string.IsNullOrWhiteSpace(preferences.Model.Model))
                return;
        }
        if (tab.Path is { } path && !Workspace.IsAgentVisible(path.Replace('\\', '/')))
            throw new IOException("This editor contains a credential file.");

        DismissSuggestion();
        var offset = Editor.CaretOffset;
        var version = tab.Document.Version;
        var before = Math.Max(0, offset - 8000);
        var prefix = tab.Document.GetText(before, offset - before);
        var suffix = tab.Document.GetText(offset, Math.Min(2500, tab.Document.TextLength - offset));
        var prompt = $"File: {tab.Title}\n\nBefore cursor:\n{prefix}\n\nAfter cursor:\n{suffix}";
        var settings = preferences.Model;
        using var cancel = BeginAgentRun(TimeSpan.FromSeconds(75));
        SetAgentVisible(true);
        Status("Generating a code suggestion…");
        try
        {
            var result = await model.CompleteAsync(settings, KeyFor(settings.Provider),
                "Complete the code at the cursor. Return only the text to insert, preserving indentation. Do not repeat surrounding code or add a code fence.",
                [new("user", prompt)], [], null, cancel.Token);
            if (activeTab != tab || tab.Document.Version.CompareAge(version) != 0 || Editor.CaretOffset != offset)
            {
                Status("The editor moved while generating the suggestion.");
                return;
            }
            var text = StripFence(result.Text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 12_000)
            {
                Status("The model returned no usable completion.");
                return;
            }
            suggestion = new(tab, version, offset, text);
            SuggestionText.Text = text;
            SuggestionPane.IsVisible = true;
            Status("Code suggestion ready");
        }
        catch (OperationCanceledException) { Status("Code suggestion stopped."); }
        finally { EndAgentRun(); }
    }

    private static string StripFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
            return value;
        var line = trimmed.IndexOf('\n');
        return line >= 0 ? trimmed[(line + 1)..^3].TrimEnd('\r', '\n') : "";
    }

    private void AcceptSuggestion()
    {
        if (suggestion is not { } candidate)
            return;
        if (activeTab != candidate.Tab || candidate.Tab.Document.Version.CompareAge(candidate.Version) != 0 ||
            Editor.CaretOffset != candidate.Offset || Editor.IsReadOnly)
        {
            Status("The editor moved. Generate a new suggestion.");
            DismissSuggestion();
            return;
        }
        candidate.Tab.Document.Insert(candidate.Offset, candidate.Text);
        Editor.CaretOffset = candidate.Offset + candidate.Text.Length;
        DismissSuggestion();
        Editor.TextArea.Focus();
    }

    private void DismissSuggestion()
    {
        suggestion = null;
        SuggestionPane.IsVisible = false;
    }

    private async Task ReviewChangeAsync()
    {
        if (agentCancellation is not null || agentApplying || gitWorktreeUpdating ||
            workspace is not { } current || ReviewFiles.SelectedItem is not ProposedChange change)
            return;
        agentApplying = true;
        UpdateAgentComposer();
        try
        {
            var decision = await DiffWindow.ReviewAsync(this, change, lifetime.Token);
            if (decision == "Discard Draft")
            {
                proposals.Remove(change);
                Status($"Discarded draft · {change.RelativePath}");
            }
            if (decision != "Apply File")
                return;
            var path = current.Resolve(change.RelativePath, forAgent: true);
            var tab = TabFor(path);
            if (tab is { IsDirty: true } or { IsSaving: true })
                throw new IOException("Save or close the editor before applying this draft.");

            applyingPath = path;
            UpdateEditorReadOnly();
            var version = await Task.Run(() => ChangeSet.ApplyAsync(current, change, lifetime.Token), lifetime.Token);
            tab ??= TabFor(path);
            if (tab is not null && !tab.IsDirty)
            {
                tab.Document.Replace(0, tab.Document.TextLength, change.Text);
                tab.Saved(path, version, isCurrentVersion: true);
            }
            proposals.Remove(change);
            Status($"Applied · {change.RelativePath}");
            if (change.IsNew)
                await RefreshExplorerAsync();
            if (GitPane.IsVisible)
                await RefreshGitAsync();
        }
        finally
        {
            applyingPath = null;
            agentApplying = false;
            UpdateEditorReadOnly();
            UpdateAgentComposer();
        }
    }

    private async void OnProviderSettings(object? sender, RoutedEventArgs e) => await TryAsync(EditProviderAsync);
    private void OnAgentModeChanged(object? sender, SelectionChangedEventArgs e) => UpdateAgentComposer();
    private void OnPromptChanged(object? sender, TextChangedEventArgs e) => UpdateSendState();
    private void OnExplainWorkspace(object? sender, RoutedEventArgs e) =>
        FillAgentPrompt("Explain this workspace and point me to the main entry points.", attachEditor: false);
    private void OnExplainFile(object? sender, RoutedEventArgs e) =>
        FillAgentPrompt("Explain the attached code and how it fits into this project.", attachEditor: true);
    private async void OnClearChat(object? sender, RoutedEventArgs e) => await TryAsync(ClearChatAsync);
    private async void OnSend(object? sender, RoutedEventArgs e) => await TryAsync(SendAsync);
    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == Primary)
        {
            e.Handled = true;
            await TryAsync(SendAsync);
        }
    }
    private void OnCancelAgent(object? sender, RoutedEventArgs e) => agentCancellation?.Cancel();
    private async void OnSuggest(object? sender, RoutedEventArgs e) => await TryAsync(SuggestAsync);
    private async void OnReviewChange(object? sender, RoutedEventArgs e) => await TryAsync(ReviewChangeAsync);
    private async void OnReviewKeyDown(object? sender, KeyEventArgs e) => await RunOnEnterAsync(e, ReviewChangeAsync);
    private void OnAcceptSuggestion(object? sender, RoutedEventArgs e) => AcceptSuggestion();
    private void OnDismissSuggestion(object? sender, RoutedEventArgs e) => DismissSuggestion();
}
