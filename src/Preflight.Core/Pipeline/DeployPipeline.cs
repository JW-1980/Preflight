using Preflight.Core.Backup;
using Preflight.Core.Git;
using Preflight.Core.Manifest;
using Preflight.Core.Migrations;
using Preflight.Core.Shell;
using Preflight.Core.Smoke;

namespace Preflight.Core.Pipeline;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogLine(DateTime Time, LogLevel Level, string Message);

public enum StepState { Idle, Running, Passed, Failed }

public sealed record StepResult(bool Success, string Summary);

/// <summary>The 3-button workflow (§5). Promote is only allowed after a passed staging run and a review.</summary>
public sealed class DeployPipeline(SiteManifest manifest, Func<CancellationToken, Task<IRemoteShell>> connect, HttpClient http)
{
    public event Action<LogLine>? Log;
    public event Action<SmokeResult>? SmokeResult;
    public event Action? StateChanged;

    public StepState StagingState { get; private set; } = StepState.Idle;
    public bool Reviewed { get; private set; }
    public StepState PromoteState { get; private set; } = StepState.Idle;
    /// <summary>origin/staging commit that passed staging; promotion is refused if staging moved since.</summary>
    public string? StagingPassedCommit { get; private set; }

    public bool CanReview => StagingState == StepState.Passed;
    public bool CanPromote => StagingState == StepState.Passed && Reviewed && PromoteState != StepState.Running;

    private GitPreflight Git => new(manifest.Repository.LocalPath
        ?? throw new InvalidOperationException("repository.local_path is not set"));

    private void Emit(LogLevel l, string m) => Log?.Invoke(new LogLine(DateTime.Now, l, m));
    private void Set(Action a) { a(); StateChanged?.Invoke(); }

    // ---------- Step 1 ----------
    public async Task<StepResult> DeployToStagingAsync(CancellationToken ct = default)
    {
        Set(() => { StagingState = StepState.Running; Reviewed = false; StagingPassedCommit = null; });
        try
        {
            var repo = manifest.Repository;
            var git = Git;
            Emit(LogLevel.Info, "Pre-flight: checking local repository");
            var status = await git.StatusAsync(ct);
            if (!status.Clean) return Fail(StepStateTarget.Staging, $"Local repository has uncommitted changes: {string.Join(", ", status.DirtyFiles.Take(10))}");
            if (status.UnpushedCommits > 0) return Fail(StepStateTarget.Staging, $"{status.UnpushedCommits} commit(s) not pushed; the server can only pull pushed code.");

            await git.FetchAsync(ct);
            Emit(LogLevel.Info, $"Pre-flight: dry-run merge origin/{repo.StagingBranch} → origin/{repo.ProductionBranch}");
            var merge = await git.DryRunMergeAsync($"origin/{repo.ProductionBranch}", $"origin/{repo.StagingBranch}", ct);
            if (!merge.CanMerge)
                return Fail(StepStateTarget.Staging, "Merge conflicts — server not touched. Conflicting files: " + string.Join(", ", merge.ConflictingFiles));
            Emit(LogLevel.Success, "No merge conflicts");
            var stagingCommit = (await LocalProcess.RunAsync("git", ["rev-parse", $"origin/{repo.StagingBranch}"], repo.LocalPath!, ct))
                .EnsureOk("rev-parse").StdOut.Trim();

            await using var shell = await connect(ct);
            var p = manifest.Server.StagingPath;
            Emit(LogLevel.Info, $"Pull {repo.StagingBranch} into {p}");
            var hasGit = (await shell.RunAsync($"test -d {Sh.Q(p + "/.git")}", ct)).Ok;
            var cmd = hasGit
                ? $"cd {Sh.Q(p)} && git fetch --prune origin && git checkout -B {Sh.Q(repo.StagingBranch)} {Sh.Q("origin/" + repo.StagingBranch)} && git reset --hard {Sh.Q(stagingCommit)}"
                : $"mkdir -p {Sh.Q(p)} && cd {Sh.Q(p)} && git init -q && git remote add origin {Sh.Q(repo.Url)} && git fetch origin {Sh.Q(repo.StagingBranch)} && git checkout -B {Sh.Q(repo.StagingBranch)} {Sh.Q(stagingCommit)}";
            (await shell.RunAsync(cmd, ct)).EnsureOk("pull to staging");
            Emit(LogLevel.Success, $"Staging at {stagingCommit[..Math.Min(8, stagingCommit.Length)]}");

            if (manifest.Database.Strategy == "incremental_sql")
            {
                var runner = new MigrationRunner(shell, m => Emit(LogLevel.Info, m));
                var stagingDb = await runner.ReadCredentialsAsync($"{p}/{manifest.Database.EnvFile}", ct);
                var liveDb = await TryReadCredentialsAsync(runner, $"{manifest.Server.LivePath}/{manifest.Database.EnvFile}", ct);
                if (liveDb is not null && SameDatabase(stagingDb, liveDb))
                    return Fail(StepStateTarget.Staging, "Staging .env points at the LIVE database. Configure a separate staging database first.");
                var outcome = await runner.RunAsync(p, manifest.Database.MigrationsDir, manifest.Database.TrackingTable, stagingDb, ct);
                Emit(LogLevel.Success, $"Staging migrations: {outcome.Applied.Count} applied, {outcome.AlreadyApplied.Count} already present");
            }

            if (!await SmokeAsync(manifest.Server.StagingUrl, ct))
                return Fail(StepStateTarget.Staging, "Smoke tests failed on staging");

            Set(() => { StagingState = StepState.Passed; StagingPassedCommit = stagingCommit; });
            Emit(LogLevel.Success, "Staging passed. Review it, then promote.");
            return new StepResult(true, "Staging passed");
        }
        catch (OperationCanceledException) { return Fail(StepStateTarget.Staging, "Cancelled"); }
        catch (Exception e) { return Fail(StepStateTarget.Staging, e.Message); }
    }

    // ---------- Step 2 ----------
    public void MarkReviewed()
    {
        if (!CanReview) throw new InvalidOperationException("Staging has not passed yet.");
        Set(() => Reviewed = true);
        Emit(LogLevel.Info, $"Staging reviewed: {manifest.Server.StagingUrl}");
    }

    // ---------- Step 3 ----------
    public async Task<StepResult> PromoteAsync(CancellationToken ct = default)
    {
        if (!CanPromote) throw new InvalidOperationException("Promote is locked until staging passed and was reviewed.");
        Set(() => PromoteState = StepState.Running);
        var repo = manifest.Repository;
        var live = manifest.Server.LivePath;
        Snapshot? snap = null;
        DbCredentials? liveDb = null;
        IRemoteShell? shell = null;
        var pushed = false;
        try
        {
            await Git.FetchAsync(ct);
            var current = (await LocalProcess.RunAsync("git", ["rev-parse", $"origin/{repo.StagingBranch}"], repo.LocalPath!, ct))
                .EnsureOk("rev-parse").StdOut.Trim();
            if (current != StagingPassedCommit)
            {
                Set(() => { StagingState = StepState.Idle; Reviewed = false; });
                return Fail(StepStateTarget.Promote, $"origin/{repo.StagingBranch} changed since staging passed; run step 1 again.");
            }

            shell = await connect(ct);
            var runner = new MigrationRunner(shell, m => Emit(LogLevel.Info, m));
            if (manifest.Database.Strategy == "incremental_sql")
                liveDb = await runner.ReadCredentialsAsync($"{live}/{manifest.Database.EnvFile}", ct);

            var snapshots = new SnapshotService(shell, runner, m => Emit(LogLevel.Info, m));
            var backupRoot = manifest.Server.BackupPath ?? $"$HOME/.backups/{manifest.Domain}";
            if (backupRoot.StartsWith("$HOME"))
                backupRoot = (await shell.RunAsync("printf %s \"$HOME\"", ct)).StdOut.Trim() + backupRoot[5..];
            snap = await snapshots.CreateAsync(backupRoot, live, liveDb, ct);
            Emit(LogLevel.Success, $"Safety snapshot: {snap.Directory}");

            Emit(LogLevel.Info, $"Merge {repo.StagingBranch} → {repo.ProductionBranch} and push");
            var mainCommit = await Git.MergeAndPushAsync(repo.ProductionBranch, repo.StagingBranch, ct);
            pushed = true;
            Emit(LogLevel.Success, $"origin/{repo.ProductionBranch} at {mainCommit[..8]}");

            (await shell.RunAsync($"cd {Sh.Q(live)} && git pull --ff-only origin {Sh.Q(repo.ProductionBranch)}", ct))
                .EnsureOk("git pull on live");
            Emit(LogLevel.Success, "Live code updated");

            if (liveDb is not null)
            {
                var outcome = await runner.RunAsync(live, manifest.Database.MigrationsDir, manifest.Database.TrackingTable, liveDb, ct);
                Emit(LogLevel.Success, $"Live migrations: {outcome.Applied.Count} applied");
            }

            if (!await SmokeAsync(manifest.Server.ProductionUrl, ct))
                throw new InvalidOperationException("Live smoke tests failed");

            Set(() => { PromoteState = StepState.Passed; StagingState = StepState.Idle; Reviewed = false; });
            Emit(LogLevel.Success, "Production promoted and verified.");
            return new StepResult(true, "Promoted");
        }
        catch (Exception e)
        {
            Emit(LogLevel.Error, e is OperationCanceledException ? "Cancelled" : e.Message);
            if (snap is not null && shell is not null)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await new SnapshotService(shell, new MigrationRunner(shell), m => Emit(LogLevel.Warning, m))
                        .RestoreAsync(snap, live, liveDb, CancellationToken.None);
                    Emit(LogLevel.Warning, $"Rolled back to snapshot {snap.Directory} in {sw.Elapsed.TotalSeconds:0.0}s");
                    if (liveDb is not null)
                        Emit(LogLevel.Warning, "Database was restored from the snapshot: live writes made during the promotion are not in it.");
                }
                catch (Exception re)
                {
                    Emit(LogLevel.Error, $"ROLLBACK FAILED: {re.Message}. Restore manually from {snap.Directory}.");
                }
            }
            if (pushed)
                Emit(LogLevel.Warning, $"origin/{repo.ProductionBranch} already contains the merge; revert it before the next promotion.");
            Set(() => PromoteState = StepState.Failed);
            return new StepResult(false, e.Message);
        }
        finally
        {
            if (shell is not null) await shell.DisposeAsync();
        }
    }

    private async Task<bool> SmokeAsync(string baseUrl, CancellationToken ct)
    {
        Emit(LogLevel.Info, $"Smoke tests on {baseUrl} ({manifest.SmokeTests.Endpoints.Count} routes)");
        var results = await new SmokeTester(http).RunAsync(baseUrl, manifest.SmokeTests, r =>
        {
            SmokeResult?.Invoke(r);
            Emit(r.Passed ? LogLevel.Success : LogLevel.Error,
                $"{r.Status?.ToString() ?? "---"} {r.Route} {r.ElapsedMs}ms{(r.Failure is null ? "" : " — " + r.Failure)}");
        }, ct);
        return results.All(r => r.Passed);
    }

    private static async Task<DbCredentials?> TryReadCredentialsAsync(MigrationRunner r, string path, CancellationToken ct)
    {
        try { return await r.ReadCredentialsAsync(path, ct); }
        catch (ShellCommandException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    public static bool SameDatabase(DbCredentials a, DbCredentials b) =>
        string.Equals(a.Database, b.Database, StringComparison.Ordinal) &&
        string.Equals(NormHost(a.Host), NormHost(b.Host), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static string NormHost(string h) => h is "127.0.0.1" or "::1" ? "localhost" : h;

    private enum StepStateTarget { Staging, Promote }

    private StepResult Fail(StepStateTarget t, string message)
    {
        Emit(LogLevel.Error, message);
        Set(() => { if (t == StepStateTarget.Staging) StagingState = StepState.Failed; else PromoteState = StepState.Failed; });
        return new StepResult(false, message);
    }
}
