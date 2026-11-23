using System.Diagnostics;
using System.Text;
using Preflight.Core.Manifest;
using Renci.SshNet;

namespace Preflight.Core.Shell;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public CommandResult EnsureOk(string what) =>
        Ok ? this : throw new ShellCommandException($"{what} failed (exit {ExitCode}): {StdErr.Trim()}", this);
}

public sealed class ShellCommandException(string message, CommandResult result) : Exception(message)
{
    public CommandResult Result { get; } = result;
}

/// <summary>Runs shell commands on the target server.</summary>
public interface IRemoteShell : IAsyncDisposable
{
    Task<CommandResult> RunAsync(string command, CancellationToken ct = default);
    /// <summary>Writes a file with mode 600; used for temporary MySQL option files.</summary>
    Task WriteFileAsync(string remotePath, string content, CancellationToken ct = default);
}

/// <summary>Quotes a value for POSIX sh.</summary>
public static class Sh
{
    public static string Q(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>Runs a command under bash with pipefail, so a failing producer in a pipe fails the command.</summary>
    public static string Bash(string command) => "bash -o pipefail -c " + Q(command);
}

/// <summary>Key-based SSH using SSH.NET.</summary>
public sealed class SshRemoteShell : IRemoteShell
{
    private readonly SshClient _client;

    public SshRemoteShell(ServerSettings server)
    {
        var keyPath = server.SshKeyPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"SSH private key not found: {keyPath}. Preflight only supports key-based SSH.");
        _client = new SshClient(new ConnectionInfo(server.SshHost, server.SshPort, server.SshUser,
            new PrivateKeyAuthenticationMethod(server.SshUser, new PrivateKeyFile(keyPath))))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
    }

    public async Task ConnectAsync(CancellationToken ct = default) => await _client.ConnectAsync(ct);

    public async Task<CommandResult> RunAsync(string command, CancellationToken ct = default)
    {
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(30);
        await cmd.ExecuteAsync(ct);
        return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
    }

    public async Task WriteFileAsync(string remotePath, string content, CancellationToken ct = default)
    {
        // Content goes over stdin-less heredoc-free base64 so no quoting issues; umask keeps it private.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        (await RunAsync($"umask 077 && printf %s {Sh.Q(b64)} | base64 -d > {Sh.Q(remotePath)}", ct))
            .EnsureOk($"write {remotePath}");
    }

    public ValueTask DisposeAsync()
    {
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runs local processes (git) without a shell, so arguments are never re-parsed.</summary>
public static class LocalProcess
{
    public static async Task<CommandResult> RunAsync(string file, IEnumerable<string> args, string workDir,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, await stdout, await stderr);
    }
}
