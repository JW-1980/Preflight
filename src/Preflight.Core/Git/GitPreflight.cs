using Preflight.Core.Shell;

namespace Preflight.Core.Git;

public sealed record GitStatus(bool Clean, IReadOnlyList<string> DirtyFiles, int UnpushedCommits, bool HasUpstream);

public sealed record MergeCheck(bool CanMerge, IReadOnlyList<string> ConflictingFiles, string Detail);

/// <summary>Local git checks (§5 step 1.1) and the production merge (§5 step 3.2).</summary>
public sealed class GitPreflight(string repoPath)
{
    private Task<CommandResult> Git(CancellationToken ct, params string[] args) =>
        LocalProcess.RunAsync("git", args, repoPath, ct);

    public async Task<GitStatus> StatusAsync(CancellationToken ct = default)
    {
        var st = (await Git(ct, "status", "--porcelain=v1")).EnsureOk("git status");
        var dirty = st.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[3..].Trim()).ToList();
        var ahead = await Git(ct, "rev-list", "--count", "@{u}..HEAD");
        var hasUpstream = ahead.Ok;
        var unpushed = hasUpstream && int.TryParse(ahead.StdOut.Trim(), out var n) ? n : 0;
        return new GitStatus(dirty.Count == 0, dirty, unpushed, hasUpstream);
    }

    public async Task FetchAsync(CancellationToken ct = default) =>
        (await Git(ct, "fetch", "--prune", "origin")).EnsureOk("git fetch");

    /// <summary>
    /// Virtual merge of <paramref name="source"/> into <paramref name="target"/> without touching the
    /// working tree, using <c>git merge-tree --write-tree</c> (git 2.38+).
    /// </summary>
    public async Task<MergeCheck> DryRunMergeAsync(string target, string source, CancellationToken ct = default)
    {
        var r = await Git(ct, "merge-tree", "--write-tree", "--name-only", "--no-messages", target, source);
        if (r.ExitCode == 0) return new MergeCheck(true, [], "no conflicts");
        if (r.ExitCode == 1)
        {
            // Output: tree OID line, then conflicted file names.
            var files = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Distinct().ToList();
            return new MergeCheck(false, files, "merge conflicts");
        }
        throw new ShellCommandException($"git merge-tree failed (exit {r.ExitCode}): {r.StdErr.Trim()}", r);
    }

    /// <summary>
    /// Merges origin/<paramref name="source"/> into origin/<paramref name="target"/> in a temporary worktree
    /// and pushes the result, leaving the user's checkout untouched. Returns the new target commit.
    /// </summary>
    public async Task<string> MergeAndPushAsync(string target, string source, CancellationToken ct = default)
    {
        await FetchAsync(ct);
        var wt = Path.Combine(Path.GetTempPath(), "preflight-merge-" + Guid.NewGuid().ToString("N"));
        (await Git(ct, "worktree", "add", "--detach", wt, $"origin/{target}")).EnsureOk("git worktree add");
        try
        {
            var inWt = (string[] a) => LocalProcess.RunAsync("git", a, wt, ct);
            var merge = await inWt(["merge", "--no-ff", "--no-edit", "-m",
                $"Preflight: promote {source} to {target}", $"origin/{source}"]);
            if (!merge.Ok)
            {
                await inWt(["merge", "--abort"]);
                throw new ShellCommandException($"merge {source} into {target} failed: {merge.StdErr.Trim()}{merge.StdOut.Trim()}", merge);
            }
            (await inWt(["push", "origin", $"HEAD:refs/heads/{target}"])).EnsureOk("git push");
            return (await inWt(["rev-parse", "HEAD"])).EnsureOk("git rev-parse").StdOut.Trim();
        }
        finally
        {
            await Git(CancellationToken.None, "worktree", "remove", "--force", wt);
        }
    }

    /// <summary>Migration file names in the local working copy, sorted ordinally.</summary>
    public IReadOnlyList<string> MigrationFiles(string migrationsDir)
    {
        var dir = Path.Combine(repoPath, migrationsDir);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.sql").Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
    }
}
