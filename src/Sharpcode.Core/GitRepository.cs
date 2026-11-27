using System.Diagnostics;
using System.Text;

namespace Sharpcode.Core;

public sealed record GitChange(string Path, char Index, char Worktree, string? OriginalPath = null, bool IsConflict = false)
{
    public bool IsUntracked => Worktree == '?';
    public bool IsStaged => Index != '.' && !IsConflict;
    public bool IsUnstaged => Worktree != '.' || IsConflict;
}

public sealed record GitStatus(string? Branch, string? Upstream, bool HasCommit, int Ahead, int Behind, IReadOnlyList<GitChange> Changes);

public sealed record GitBranch(string Name, bool IsRemote);

public sealed class GitRepository
{
    private GitRepository(string root) => Root = root;

    public string Root { get; }

    public static async Task<GitRepository?> DiscoverAsync(string folder, CancellationToken cancellationToken)
    {
        var result = await RunAsync(folder, 8192, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (result.ExitCode != 0 && result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            return null;
        return new(PathIdentity.Resolve(RequireSuccess(result).TrimEnd('\r', '\n')));
    }

    public static async Task<GitRepository> InitAsync(string folder, CancellationToken cancellationToken)
    {
        RequireSuccess(await RunAsync(folder, 8192, cancellationToken, "init", "--initial-branch=main").ConfigureAwait(false));
        return new(PathIdentity.Resolve(folder));
    }

    public static async Task CloneAsync(string address, string destination, CancellationToken cancellationToken)
    {
        ValidateAddress(address);
        RequireSuccess(await RunAsync(Path.GetDirectoryName(destination)!, 64 * 1024, cancellationToken,
            "clone", "--", address, destination).ConfigureAwait(false), allowTruncated: true);
    }

    public async Task<GitStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var output = RequireSuccess(await RunAsync(Root, 24 * 1024 * 1024, cancellationToken,
            "--no-optional-locks", "status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z").ConfigureAwait(false));
        string? branch = null;
        string? upstream = null;
        var hasCommit = false;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitChange>();
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = entry[14..] == "(detached)" ? null : entry[14..];
            else if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
                hasCommit = entry[13..] != "(initial)";
            else if (entry.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                upstream = entry[18..];
            else if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = entry[12..].Split(' ', 2);
                int.TryParse(counts[0], out ahead);
                if (counts.Length == 2 && int.TryParse(counts[1], out var negative))
                    behind = Math.Abs(negative);
            }
            else if (entry.StartsWith("? ", StringComparison.Ordinal))
                changes.Add(new(entry[2..], '.', '?'));
            else if (entry[0] is '1' or '2' or 'u')
            {
                var renamed = entry[0] == '2';
                var conflict = entry[0] == 'u';
                var fields = entry.Split(' ', renamed ? 10 : conflict ? 11 : 9);
                var original = renamed ? entries[++i] : null;
                changes.Add(new(fields[^1], fields[1][0], fields[1][1], original, conflict));
            }
        }
        return new(branch, upstream, hasCommit, ahead, behind, changes);
    }

    public Task StageAsync(GitChange? change, CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken, ["add", "-A", "--", .. Paths(change)]);

    public Task UnstageAsync(GitChange? change, bool hasCommit, CancellationToken cancellationToken) => hasCommit
        ? ExecuteAsync(cancellationToken, ["restore", "--staged", "--", .. Paths(change)])
        : ExecuteAsync(cancellationToken, ["rm", "-rf", "--cached", "--ignore-unmatch", "--", .. Paths(change)]);

    public Task CommitAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Enter a commit message.");
        return ExecuteAsync(cancellationToken, "commit", "-m", message.Trim());
    }

    public async Task<string> DiffAsync(GitChange change, bool staged, CancellationToken cancellationToken)
    {
        var result = await RunAsync(Root, 384 * 1024, cancellationToken, ["diff", "--no-color", "--no-ext-diff", "--no-textconv",
            .. (change.IsUntracked ? new[] { "--no-index", "--", "/dev/null", change.Path }
                : staged ? ["--cached", "--", .. Paths(change)] : ["--", .. Paths(change)])]).ConfigureAwait(false);
        if (result.ExitCode != 0 && (!change.IsUntracked || result.ExitCode != 1 || result.Output.Length == 0))
            RequireSuccess(result);
        return result.Truncated ? result.Output + "\n\nDiff preview truncated." : result.Output;
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(CancellationToken cancellationToken)
    {
        var branches = RequireSuccess(await RunAsync(Root, 1024 * 1024, cancellationToken, "for-each-ref",
            "--format=%(refname)%09%(symref)", "refs/heads", "refs/remotes").ConfigureAwait(false));
        return branches.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2))
            .Where(fields => fields.Length == 2 && fields[1].Length == 0)
            .Select(fields => fields[0].StartsWith("refs/heads/", StringComparison.Ordinal)
                ? new GitBranch(fields[0][11..], false) : new GitBranch(fields[0][13..], true)).ToArray();
    }

    public Task SwitchAsync(GitBranch branch, CancellationToken cancellationToken) => branch.IsRemote
        ? ExecuteAsync(cancellationToken, "switch", "--track", "--", $"refs/remotes/{branch.Name}")
        : ExecuteAsync(cancellationToken, "switch", "--no-guess", "--", branch.Name);

    public async Task CreateBranchAsync(string branch, CancellationToken cancellationToken)
    {
        if (branch.StartsWith('-'))
            throw new ArgumentException("A branch name cannot start with a hyphen.");
        await ExecuteAsync(cancellationToken, "check-ref-format", $"refs/heads/{branch}").ConfigureAwait(false);
        await ExecuteAsync(cancellationToken, "switch", "-c", branch).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> RemotesAsync(CancellationToken cancellationToken)
    {
        var remotes = RequireSuccess(await RunAsync(Root, 64 * 1024, cancellationToken, "remote").ConfigureAwait(false));
        return remotes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public Task AddRemoteAsync(string name, string address, CancellationToken cancellationToken)
    {
        ValidateAddress(address);
        return ExecuteAsync(cancellationToken, "remote", "add", "--", name, address);
    }

    public Task FetchAsync(string remote, CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken, "fetch", "--", remote);

    public Task PullAsync(string? remote, string branch, CancellationToken cancellationToken) => remote is null
        ? ExecuteAsync(cancellationToken, "pull", "--ff-only", "--no-rebase")
        : ExecuteAsync(cancellationToken, "pull", "--ff-only", "--no-rebase", "--", remote, $"refs/heads/{branch}");

    public async Task PushAsync(GitStatus status, string? remote, CancellationToken cancellationToken)
    {
        if (status.Branch is not { } branch || !status.HasCommit)
            throw new IOException("Create a commit on a local branch before pushing.");

        var source = $"refs/heads/{branch}";
        if (remote is not null)
            await ExecuteAsync(cancellationToken, "-c", $"remote.{remote}.mirror=false",
                "push", "--no-follow-tags", "--set-upstream", "--", remote, $"{source}:{source}").ConfigureAwait(false);
        else
        {
            var upstreamRemote = (await ReadAsync(cancellationToken, "config", "--get", $"branch.{branch}.remote").ConfigureAwait(false)).Trim();
            var upstreamRef = (await ReadAsync(cancellationToken, "config", "--get", $"branch.{branch}.merge").ConfigureAwait(false)).Trim();
            if (!upstreamRef.StartsWith("refs/heads/", StringComparison.Ordinal) || upstreamRef.Contains('\n'))
                throw new IOException("The upstream branch is unavailable.");
            await ExecuteAsync(cancellationToken, "-c", $"remote.{upstreamRemote}.mirror=false",
                "push", "--no-follow-tags", "--", upstreamRemote, $"{source}:{upstreamRef}").ConfigureAwait(false);
        }
    }

    public Task<string> HistoryAsync(CancellationToken cancellationToken) =>
        ReadAsync(cancellationToken, "log", "-50", "--format=%h  %s%n    %an · %as%n");

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(string? before, string after, CancellationToken cancellationToken)
    {
        var changed = before is null
            ? await ReadAsync(cancellationToken, "ls-tree", "-r", "--name-only", "-z", after).ConfigureAwait(false)
            : await ReadAsync(cancellationToken, "diff", "--no-ext-diff", "--name-only", "--no-renames", "-z", before, after, "--")
                .ConfigureAwait(false);
        return changed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<string?> HeadAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(Root, 1024, cancellationToken, "rev-parse", "--verify", "--quiet", "HEAD").ConfigureAwait(false);
        return result.ExitCode == 1 ? null : RequireSuccess(result).Trim();
    }

    private static void ValidateAddress(string address)
    {
        if (!Path.IsPathFullyQualified(address) && !IsNetworkAddress(address))
            throw new ArgumentException("Enter an HTTPS or SSH repository URL, or an absolute path to a Git repository.");
    }

    private static bool IsNetworkAddress(string address)
    {
        if (address.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host.Length > 0 &&
                   (uri.Scheme == "https" && uri.UserInfo.Length == 0 || uri.Scheme == "ssh" && !uri.UserInfo.Contains(':'));

        var colon = address.IndexOf(':');
        return colon > 0 && colon < address.Length - 1 && address[colon + 1] != ':' &&
               !address.StartsWith('-') && address[..colon].Contains('@') &&
               !address.Any(char.IsWhiteSpace);
    }

    private static string[] Paths(GitChange? change) => change switch
    {
        null => ["."],
        { OriginalPath: { } original } => [original, change.Path],
        _ => [change.Path]
    };

    private async Task ExecuteAsync(CancellationToken cancellationToken, params string[] arguments) =>
        RequireSuccess(await RunAsync(Root, 64 * 1024, cancellationToken, arguments).ConfigureAwait(false), allowTruncated: true);

    private async Task<string> ReadAsync(CancellationToken cancellationToken, params string[] arguments) =>
        RequireSuccess(await RunAsync(Root, 8 * 1024 * 1024, cancellationToken, arguments).ConfigureAwait(false));

    private static string RequireSuccess(Result result, bool allowTruncated = false)
    {
        if (result.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? $"Git exited with code {result.ExitCode}." : result.Error.Trim());
        if (result.Truncated && !allowTruncated)
            throw new IOException("Git output exceeded the display limit.");
        return result.Output;
    }

    private static async Task<Result> RunAsync(string root, int limit, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = Workspace.Command("git", root,
            ["-c", "core.fsmonitor=false", "-c", "protocol.ext.allow=never", "--literal-pathspecs", .. arguments]);
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new IOException("Git is unavailable on PATH.", exception);
        }
        using var registration = cancellationToken.Register(() => Workspace.Stop(process));
        var stdout = CaptureAsync(process.StandardOutput, limit, cancellationToken);
        var stderr = CaptureAsync(process.StandardError, 8192, cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        var (output, truncated) = await stdout.ConfigureAwait(false);
        return new(process.ExitCode, output, (await stderr.ConfigureAwait(false)).Text, truncated);
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader reader, int limit,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var available = Math.Min(count, limit - output.Length);
            output.Append(buffer, 0, available);
            truncated |= available < count;
        }
        return (output.ToString(), truncated);
    }

    private sealed record Result(int ExitCode, string Output, string Error, bool Truncated);
}
