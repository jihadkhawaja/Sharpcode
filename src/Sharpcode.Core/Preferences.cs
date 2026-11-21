using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpace;

namespace Sharpcode.Core;

public sealed record Preferences(ModelSettings Model, string? LastWorkspace = null, double FontSize = 14,
    string[]? RecentWorkspaces = null, bool ShowSidebar = true, bool ShowAgent = false, AppearanceSettings? Appearance = null)
{
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("SHARPCODE_DATA_DIR") is { Length: > 0 } overrideDirectory
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sharpcode");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static Preferences Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath));
            return saved?.Model is not null && Enum.IsDefined(saved.Model.Provider) ? saved : new(ModelSettings.Default);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(ModelSettings.Default);
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = Path.Combine(DataDirectory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

public sealed class AgentHistory
{
    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private const string Session = "chat";
    private readonly AgentSpaceHost host = new(Path.Combine(Preferences.DataDirectory, "agent-history"),
        new DelegateWorkspacePolicyProvider(_ => new(new WorkspacePolicy
        {
            AllowShortTermMemory = true,
            Limits = new WorkspaceLimits { MaxShortTermEntries = 12 }
        })));

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(string workspace, CancellationToken cancellationToken)
    {
        var memory = Open(workspace);
        var entries = await memory.ReadAsync(Session, limit: 25, cancellationToken: cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => JsonSerializer.Deserialize<Turn>(entry.Text))
            .OfType<Turn>().SelectMany(turn => new ChatMessage[] { new("user", turn.User), new("assistant", turn.Assistant) })
            .ToArray();
    }

    public async Task AppendAsync(string workspace, string user, string assistant, CancellationToken cancellationToken)
    {
        var memory = Open(workspace);
        var turn = new Turn(CodingAgent.Limit(user, 700), CodingAgent.Limit(assistant, 1800));
        await memory.AppendAsync(Session, JsonSerializer.Serialize(turn),
            TimeSpan.FromDays(7), cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(string workspace, CancellationToken cancellationToken) =>
        Open(workspace).ClearAsync(Session, cancellationToken);

    private IShortTermMemory Open(string workspace)
    {
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? workspace.ToUpperInvariant() : workspace)))[..32];
        return host.Open(new WorkspaceIdentity("sharpcode", identity)).ShortTermMemory;
    }

    private sealed record Turn(string User, string Assistant);
}
