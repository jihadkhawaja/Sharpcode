using System.Text;
using System.Text.Json;
using Sharpcode.Core;

namespace Sharpcode.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("sharpcode-");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OlderPreferencesKeepBothPanelsEnabled()
    {
        var settings = JsonSerializer.Deserialize<Preferences>("""{"Model":{"Provider":3,"Model":"fixture"}}""")!;
        Assert.True(settings.ShowSidebar);
        Assert.True(settings.ShowAgent);
        Assert.Equal("fixture", settings.Model.Model);
    }

    [Fact]
    public async Task RefreshInvalidatesTheQuickOpenIndex()
    {
        var workspace = new Workspace(folder.FullName);
        await File.WriteAllTextAsync(workspace.Resolve("existing.txt"), "", Token);
        Assert.Equal(["existing.txt"], await workspace.FilesAsync(Token));
        await File.WriteAllTextAsync(workspace.Resolve("new.txt"), "", Token);
        workspace.InvalidateFileIndex();
        Assert.Equal(["existing.txt", "new.txt"], await workspace.FilesAsync(Token));
    }

    [Fact]
    public async Task WorkspaceSearchReportsUnicodeCharacterColumnsAndKeepsCredentialsFromAgent()
    {
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, ".env"), "answer=47", Token);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "code.cs"), "é😀 answer = 42;", Token);
        var workspace = new Workspace(folder.FullName);

        var visible = await workspace.SearchAsync("ANSWER", 10, forAgent: true, Token);
        var hit = Assert.Single(visible);
        Assert.Equal("code.cs", hit.Path);
        Assert.Equal(5, hit.Column);
        Assert.Equal(2, (await workspace.SearchAsync("answer", 10, forAgent: false, Token)).Count);
        Assert.Throws<IOException>(() => workspace.Resolve(".env", forAgent: true));
        Assert.Throws<IOException>(() => workspace.Resolve("../code.cs", forAgent: true));
        Assert.Throws<IOException>(() => workspace.Resolve(".git/config", forAgent: true));
    }

    [Fact]
    public async Task DraftsUseTheOriginalFileVersionAndPreserveEncodingAndLineEndings()
    {
        var path = Path.Combine(folder.FullName, "text.txt");
        var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        const string initial = "alpha\r\nbeta\r\n";
        await File.WriteAllTextAsync(path, initial, encoding, Token);
        var workspace = new Workspace(folder.FullName);
        var changes = new ChangeSet(workspace);

        await changes.ReplaceAsync("text.txt", "alpha\nbeta", "one\ntwo", Token);
        await changes.ReplaceAsync("text.txt", "two", "three", Token);
        Assert.Equal(initial, await File.ReadAllTextAsync(path, Token));
        var change = Assert.Single(changes.Changes);
        Assert.Equal("one\r\nthree\r\n", change.Text);

        var version = await ChangeSet.ApplyAsync(workspace, change, Token);
        var after = await TextFiles.ReadAsync(path, TextFiles.AgentLimit, Token);
        Assert.Equal(change.Text, after.Text);
        Assert.Equal(encoding.GetPreamble(), after.Encoding.GetPreamble());
        Assert.Equal(version, after.Version);
    }

    [Fact]
    public async Task AnExternalEditAfterReviewPreventsOverwrite()
    {
        var path = Path.Combine(folder.FullName, "conflict.cs");
        await File.WriteAllTextAsync(path, "first", Token);
        var workspace = new Workspace(folder.FullName);
        var changes = new ChangeSet(workspace);
        await changes.ReplaceAsync("conflict.cs", "first", "draft", Token);
        var draft = Assert.Single(changes.Changes);
        await File.WriteAllTextAsync(path, "external update", Token);

        await Assert.ThrowsAsync<IOException>(() => ChangeSet.ApplyAsync(workspace, draft, Token));
        Assert.Equal("external update", await File.ReadAllTextAsync(path, Token));
        Assert.Equal(["conflict.cs"], Directory.GetFiles(folder.FullName).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFileCreatedAfterDraftingIsPreserved()
    {
        var workspace = new Workspace(folder.FullName);
        var changes = new ChangeSet(workspace);
        changes.Create("new.cs", "draft");
        var draft = Assert.Single(changes.Changes);
        var path = Path.Combine(folder.FullName, "new.cs");
        await File.WriteAllTextAsync(path, "external creation", Token);

        await Assert.ThrowsAsync<IOException>(() => ChangeSet.ApplyAsync(workspace, draft, Token));
        Assert.Equal("external creation", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task FollowUpCanReadAndRefineAnUnappliedDraft()
    {
        var workspace = new Workspace(folder.FullName);
        var first = new ChangeSet(workspace);
        first.Create("docs/readme.md", "# Draft");
        var followUp = new ChangeSet(workspace, first.Changes);
        Assert.Equal("# Draft", await followUp.ReadAsync("docs/readme.md", Token));

        await followUp.ReplaceAsync("docs/readme.md", "# Draft", "# Refined", Token);
        var draft = Assert.Single(followUp.Changes);
        Assert.False(Directory.Exists(Path.Combine(folder.FullName, "docs")));
        Assert.True(draft.IsNew);
        await ChangeSet.ApplyAsync(workspace, draft, Token);
        Assert.Equal("# Refined", await File.ReadAllTextAsync(workspace.Resolve(draft.RelativePath), Token));
    }

    [Fact]
    public async Task AmbiguousReplacementLeavesTheChangeSetUntouched()
    {
        var workspace = new Workspace(folder.FullName);
        await File.WriteAllTextAsync(workspace.Resolve("test.txt"), "a a", Token);
        var changes = new ChangeSet(workspace);
        await Assert.ThrowsAsync<IOException>(() => changes.ReplaceAsync("test.txt", "a", "b", Token));
        Assert.Empty(changes.Changes);
    }

    public void Dispose() => folder.Delete(recursive: true);
}
