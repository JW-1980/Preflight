using Preflight.Core.Shell;

namespace Preflight.Core.Tests;

/// <summary>Real bash on this machine, standing in for the SSH target (same commands, no network).</summary>
public sealed class LocalBashShell : IRemoteShell
{
    public List<string> Commands { get; } = [];

    public async Task<CommandResult> RunAsync(string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return await LocalProcess.RunAsync("bash", ["-c", command], Path.GetTempPath(), ct);
    }

    public async Task WriteFileAsync(string remotePath, string content, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(remotePath, content, ct);
        File.SetUnixFileMode(remotePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("pf-test-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
}

public static class GitHelper
{
    public static string Git(string dir, params string[] args)
    {
        var r = LocalProcess.RunAsync("git", args, dir).GetAwaiter().GetResult();
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.StdErr}");
        return r.StdOut.Trim();
    }

    /// <summary>Bare origin + clone with main and staging branches.</summary>
    public static (string origin, string clone) CreateRepo(string root)
    {
        var origin = System.IO.Path.Combine(root, "origin.git");
        var clone = System.IO.Path.Combine(root, "work");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "--bare", "-q", "-b", "main");
        Git(root, "clone", "-q", origin, clone);
        Git(clone, "config", "user.email", "t@example.com");
        Git(clone, "config", "user.name", "t");
        File.WriteAllText(System.IO.Path.Combine(clone, "a.txt"), "one\n");
        Git(clone, "add", ".");
        Git(clone, "commit", "-qm", "init");
        Git(clone, "push", "-q", "origin", "main");
        Git(clone, "checkout", "-qb", "staging");
        Git(clone, "push", "-q", "-u", "origin", "staging");
        return (origin, clone);
    }
}
