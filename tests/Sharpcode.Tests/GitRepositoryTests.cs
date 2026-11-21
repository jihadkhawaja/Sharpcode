using System.Diagnostics;
using Sharpcode.Core;

namespace Sharpcode.Tests;

public sealed class GitRepositoryTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("sharpcode-git-");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StagingAndDiffsKeepIndexAndWorkingVersionsSeparate()
    {
        var repo = await CreateRepositoryAsync("work");
        const string name = "-file [1].cs";
        var path = Path.Combine(repo.Root, name);
        await File.WriteAllTextAsync(path, "first\n", Token);
        var before = await repo.StatusAsync(Token);
        Assert.Equal("main", before.Branch);
        Assert.False(before.HasCommit);
        var untracked = Assert.Single(before.Changes);
        Assert.True(untracked.IsUntracked);
        Assert.Contains("+first", await repo.DiffAsync(untracked, staged: false, Token));

        await repo.StageAsync(untracked, Token);
        var staged = Assert.Single((await repo.StatusAsync(Token)).Changes);
        Assert.True(staged.IsStaged);
        Assert.False(staged.IsUnstaged);
        await repo.UnstageAsync(staged, hasCommit: false, Token);
        Assert.True(Assert.Single((await repo.StatusAsync(Token)).Changes).IsUntracked);
        Assert.Equal("first\n", await File.ReadAllTextAsync(path, Token));
        await repo.StageAsync(null, Token);
        await repo.CommitAsync("Initial commit", Token);
        Assert.Empty((await repo.StatusAsync(Token)).Changes);

        await File.WriteAllTextAsync(path, "index\n", Token);
        await repo.StageAsync(Assert.Single((await repo.StatusAsync(Token)).Changes), Token);
        await File.WriteAllTextAsync(path, "working\n", Token);
        var mixed = Assert.Single((await repo.StatusAsync(Token)).Changes);
        Assert.Equal('M', mixed.Index);
        Assert.Equal('M', mixed.Worktree);
        Assert.Contains("+index", await repo.DiffAsync(mixed, staged: true, Token));
        Assert.Contains("+working", await repo.DiffAsync(mixed, staged: false, Token));

        await repo.UnstageAsync(mixed, hasCommit: true, Token);
        Assert.False(Assert.Single((await repo.StatusAsync(Token)).Changes).IsStaged);
        Assert.Equal("working\n", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task PublishCloneFetchAndFastForwardPullWorkAgainstALocalRemote()
    {
        var remote = Directory.CreateDirectory(Path.Combine(root.FullName, "remote.git")).FullName;
        await RunGitAsync(remote, Token, "init", "--bare", "--initial-branch=main");
        var repo = await CreateRepositoryAsync("publisher");
        var file = Path.Combine(repo.Root, "README.md");
        await File.WriteAllTextAsync(file, "First commit\n", Token);
        await repo.StageAsync(null, Token);
        await repo.CommitAsync("Publish first commit", Token);
        await repo.AddRemoteAsync("origin", remote, Token);
        Assert.Equal(["origin"], await repo.RemotesAsync(Token));
        await repo.PushAsync(await repo.StatusAsync(Token), "origin", Token);
        Assert.Equal("origin/main", (await repo.StatusAsync(Token)).Upstream);

        var clonePath = Path.Combine(root.FullName, "clone");
        await GitRepository.CloneAsync(remote, clonePath, Token);
        var clone = Assert.IsType<GitRepository>(await GitRepository.DiscoverAsync(clonePath, Token));
        Assert.Equal("First commit\n", (await File.ReadAllTextAsync(Path.Combine(clonePath, "README.md"), Token)).ReplaceLineEndings("\n"));
        await clone.CreateBranchAsync("feature/navigation", Token);
        Assert.Equal("feature/navigation", (await clone.StatusAsync(Token)).Branch);
        var branches = await clone.BranchesAsync(Token);
        Assert.Contains(branches, branch => branch is { Name: "origin/main", IsRemote: true });
        await clone.SwitchAsync(Assert.Single(branches, branch => branch is { Name: "main", IsRemote: false }), Token);

        await File.WriteAllTextAsync(file, "Second commit\n", Token);
        await repo.StageAsync(null, Token);
        await repo.CommitAsync("Update README", Token);
        await repo.PushAsync(await repo.StatusAsync(Token), remote: null, Token);

        await clone.FetchAsync("origin", Token);
        Assert.Equal(1, (await clone.StatusAsync(Token)).Behind);
        var before = await clone.HeadAsync(Token);
        await clone.PullAsync(remote: null, "main", Token);
        var after = Assert.IsType<string>(await clone.HeadAsync(Token));
        Assert.NotEqual(before, after);
        Assert.Equal(["README.md"], await clone.ChangedPathsAsync(before, after, Token));
        Assert.Equal("Second commit\n", (await File.ReadAllTextAsync(Path.Combine(clonePath, "README.md"), Token)).ReplaceLineEndings("\n"));
        Assert.Contains("Update README", await clone.HistoryAsync(Token));
        Assert.Equal(0, (await clone.StatusAsync(Token)).Behind);
    }

    [Fact]
    public async Task RenameDiffIncludesBothPathsAndDiscoveryWorksFromASubdirectory()
    {
        var repo = await CreateRepositoryAsync("renames");
        var nested = Directory.CreateDirectory(Path.Combine(repo.Root, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "old name.txt"), "Contents\n", Token);
        await repo.StageAsync(null, Token);
        await repo.CommitAsync("Initial file", Token);
        await RunGitAsync(repo.Root, Token, "mv", "--", "src/old name.txt", "src/new name.txt");
        var found = Assert.IsType<GitRepository>(await GitRepository.DiscoverAsync(nested, Token));
        Assert.Equal(repo.Root, found.Root);
        var rename = Assert.Single((await found.StatusAsync(Token)).Changes);
        Assert.Equal("src/new name.txt", rename.Path);
        Assert.Equal("src/old name.txt", rename.OriginalPath);
        Assert.Equal('R', rename.Index);
        var diff = await found.DiffAsync(rename, staged: true, Token);
        Assert.Contains("rename from src/old name.txt", diff);
        Assert.Contains("rename to src/new name.txt", diff);
    }

    private async Task<GitRepository> CreateRepositoryAsync(string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;
        var repo = await GitRepository.InitAsync(path, Token);
        await ConfigureIdentityAsync(path, Token);
        return repo;
    }

    internal static async Task ConfigureIdentityAsync(string path, CancellationToken token)
    {
        await RunGitAsync(path, token, "config", "user.name", "Sharpcode Tests");
        await RunGitAsync(path, token, "config", "user.email", "sharpcode@example.test");
        await RunGitAsync(path, token, "config", "commit.gpgsign", "false");
        await RunGitAsync(path, token, "config", "core.autocrlf", "false");
    }

    internal static async Task RunGitAsync(string path, CancellationToken token, params string[] args)
    {
        using var process = Process.Start(Workspace.Command("git", path, args))!;
        var errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await errors);
    }

    internal static void DeleteFixture(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly)
                    file.IsReadOnly = false;
        directory.Delete(recursive: true);
    }

    public void Dispose() => DeleteFixture(root);
}
