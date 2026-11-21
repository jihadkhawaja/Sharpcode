using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sharpcode.Core;

namespace Sharpcode;

internal sealed record ProviderChoice(ModelSettings Settings, string Key);

internal static class ProviderWindow
{
    public static string ProviderLabel(ModelProvider provider) => provider switch
    {
        ModelProvider.OpenAI => "OpenAI",
        ModelProvider.Claude => "Claude",
        ModelProvider.LMStudio => "LM Studio",
        _ => "OpenAI-compatible"
    };

    public static Task<ProviderChoice?> EditAsync(Window owner, ModelSettings settings,
        Func<ModelProvider, string?> sessionKey, ModelClient client)
    {
        var window = Dialogs.Window("Model Provider", 570, 605);
        var providers = Enum.GetValues<ModelProvider>();
        var picker = new ComboBox
        {
            ItemsSource = providers.Select(ProviderLabel).ToArray(),
            SelectedIndex = Array.IndexOf(providers, settings.Provider),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var url = new TextBox { Text = settings.BaseUrl };
        var model = new TextBox { Text = settings.Model, PlaceholderText = "Model identifier" };
        var key = new TextBox { Text = sessionKey(settings.Provider), PasswordChar = '●' };
        var modelList = new ComboBox { IsVisible = false, PlaceholderText = "Choose a model from the server" };
        var fetch = Dialogs.Button("Find Models");
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "Sharpcode sends prompts and workspace context to the selected provider. API keys entered here last for this session.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap
        };
        var providerHint = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var history = new CheckBox
        {
            Content = "Keep recent chat between launches",
            IsChecked = settings.KeepHistory && AgentHistory.Supported,
            IsEnabled = AgentHistory.Supported
        };
        var actions = Dialogs.Actions();
        var dismiss = Dialogs.Button("Cancel");
        var save = Dialogs.Button("Save Provider", true);
        actions.Children.Add(dismiss);
        actions.Children.Add(save);
        var details = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        details.Children.Add(Dialogs.Heading("Model provider"));
        details.Children.Add(Labeled("Provider", picker));
        details.Children.Add(providerHint);
        details.Children.Add(Labeled("Server URL", url));
        details.Children.Add(Labeled("API key", key));
        var modelRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
        modelRow.Children.Add(model);
        modelRow.Children.Add(PickerWindow.InColumn(fetch, 1));
        details.Children.Add(Labeled("Model", modelRow));
        details.Children.Add(modelList);
        details.Children.Add(history);
        if (!AgentHistory.Supported)
            details.Children.Add(new TextBlock { Text = "Chat stays in this session on macOS.", Classes = { "muted" } });
        details.Children.Add(status);
        details.Children.Add(note);
        details.Children.Add(actions);
        window.Content = new ScrollViewer { Content = details };

        ModelProvider Selected() => providers[Math.Clamp(picker.SelectedIndex, 0, providers.Length - 1)];
        ModelSettings Read() => new(Selected(), model.Text?.Trim() ?? "", url.Text?.Trim() ?? "", history.IsChecked == true);
        var previousProvider = settings.Provider;
        var drafts = new Dictionary<ModelProvider, (string? Url, string? Model, string? Key)>();
        void UpdateHints()
        {
            url.PlaceholderText = ModelSettings.DefaultEndpoint(Selected());
            providerHint.Text = Selected() switch
            {
                ModelProvider.LMStudio => "Start the local server in LM Studio, then use Find Models.",
                ModelProvider.OpenAICompatible => "Enter the base URL of your OpenAI-compatible server, including its API prefix.",
                _ => "Enter an API key or use the provider’s environment variable."
            };
            key.PlaceholderText = Selected() switch
            {
                ModelProvider.OpenAI => "Uses OPENAI_API_KEY when blank",
                ModelProvider.Claude => "Uses ANTHROPIC_API_KEY when blank",
                ModelProvider.LMStudio => "Optional · LM_STUDIO_API_TOKEN",
                _ => "Optional · SHARPCODE_API_KEY"
            };
        }
        UpdateHints();
        picker.SelectionChanged += (_, _) =>
        {
            drafts[previousProvider] = (url.Text, model.Text, key.Text);
            previousProvider = Selected();
            (string? Url, string? Model, string? Key) draft = drafts.TryGetValue(previousProvider, out var saved) ? saved
                : ("", "", sessionKey(previousProvider));
            url.Text = draft.Url;
            model.Text = draft.Model;
            key.Text = draft.Key;
            modelList.IsVisible = false;
            status.Text = "";
            UpdateHints();
        };
        modelList.SelectionChanged += (_, _) =>
        {
            if (modelList.SelectedItem is string selected)
                model.Text = selected;
        };

        CancellationTokenSource? loading = null;
        var closed = false;
        fetch.Click += async (_, _) =>
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            loading = request;
            fetch.IsEnabled = picker.IsEnabled = url.IsEnabled = key.IsEnabled = false;
            modelList.IsVisible = false;
            status.Text = "Contacting provider…";
            try
            {
                var models = await client.ModelsAsync(Read(), key.Text, request.Token);
                if (closed)
                    return;
                modelList.ItemsSource = models;
                modelList.IsVisible = models.Count > 0;
                status.Text = models.Count switch
                {
                    0 => "The server returned no models. You can enter a model name manually.",
                    1 => "1 model available.",
                    _ => $"{models.Count:N0} models available. Choose one from the list."
                };
                if (models.Count == 1 && string.IsNullOrEmpty(model.Text))
                    modelList.SelectedIndex = 0;
            }
            catch (Exception exception)
            {
                if (!closed)
                    status.Text = exception is OperationCanceledException ? "The model list request timed out." : exception.Message;
            }
            finally
            {
                loading = null;
                fetch.IsEnabled = picker.IsEnabled = url.IsEnabled = key.IsEnabled = true;
            }
        };
        window.Closed += (_, _) => { closed = true; loading?.Cancel(); };
        dismiss.Click += (_, _) => window.Close(null);
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(null); };
        save.Click += (_, _) =>
        {
            var result = Read();
            if (result.Model.Length == 0)
            {
                status.Text = "Enter or select a model identifier.";
                model.Focus();
                return;
            }
            if (!Uri.TryCreate(result.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            {
                status.Text = "Enter an HTTP(S) URL without credentials, a query, or a fragment.";
                url.Focus();
                return;
            }
            window.Close(new ProviderChoice(result, key.Text ?? ""));
        };
        Automation.SetName(model, "Model identifier");
        window.Opened += (_, _) => picker.Focus();
        return window.ShowDialog<ProviderChoice?>(owner);
    }

    private static StackPanel Labeled(string label, Control value)
    {
        Automation.SetName(value, label);
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label, FontWeight = FontWeight.Medium }, value }
        };
    }
}
