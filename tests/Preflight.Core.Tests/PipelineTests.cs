using System.Net;
using System.Net.Sockets;
using System.Text;
using Preflight.Core.Manifest;
using Preflight.Core.Pipeline;
using Preflight.Core.Shell;

namespace Preflight.Core.Tests;

/// <summary>Starts a throwaway MariaDB on 127.0.0.1 for the end-to-end tests.</summary>
public sealed class MariaDbFixture : IDisposable
{
    public int Port { get; } = 33061 + Random.Shared.Next(0, 500);
    public bool Available { get; }
    public string? Reason { get; }
    private readonly string _dir = Directory.CreateTempSubdirectory("pf-mariadb-").FullName;
    private System.Diagnostics.Process? _proc;

    public MariaDbFixture()
    {
        if (Which("mariadbd") is null || Which("mariadb-install-db") is null || Which("mysqldump") is null)
        {
            Reason = "mariadb-server / mysqldump not installed";
            return;
        }
        var data = Path.Combine(_dir, "data");
        Run($"mariadb-install-db --no-defaults --datadir={data} --auth-root-authentication-method=normal --skip-test-db --user=root >/dev/null");
        _proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mariadbd",
            $"--no-defaults --datadir={data} --port={Port} --bind-address=127.0.0.1 --socket={_dir}/s.sock --user=root --skip-grant-tables=0")
        { RedirectStandardError = true, RedirectStandardOutput = true });
        for (var i = 0; i < 60 && !Run($"mysql --no-defaults -h127.0.0.1 -P{Port} -uroot -e 'select 1' >/dev/null 2>&1", false); i++)
            Thread.Sleep(500);
        Run($"mysql --no-defaults -h127.0.0.1 -P{Port} -uroot -e \"" +
            "CREATE DATABASE live_db; CREATE DATABASE staging_db; " +
            "CREATE USER 'app'@'%' IDENTIFIED BY 'p\\\"w''d'; GRANT ALL ON *.* TO 'app'@'%'; FLUSH PRIVILEGES;\"");
        Available = true;
    }

    public string Sql(string db, string sql)
    {
        var r = LocalProcess.RunAsync("mysql", ["--no-defaults", "-h127.0.0.1", $"-P{Port}", "-uroot", "-N", "-B", db, "-e", sql], "/").Result;
        if (!r.Ok) throw new InvalidOperationException(r.StdErr);
        return r.StdOut.Trim();
    }

    private static bool Run(string cmd, bool throwOnError = true)
    {
        var r = LocalProcess.RunAsync("bash", ["-c", cmd], "/").Result;
        if (!r.Ok && throwOnError) throw new InvalidOperationException($"{cmd}: {r.StdErr}");
        return r.Ok;
    }

    private static string? Which(string exe) =>
        Environment.GetEnvironmentVariable("PATH")!.Split(':').Select(p => Path.Combine(p, exe)).FirstOrDefault(File.Exists);

    public void Dispose()
    {
        try { _proc?.Kill(true); _proc?.WaitForExit(5000); } catch { /* ignore */ }
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}

/// <summary>Serves files of a webroot over HTTP; "/" → index.html, "/x" → x.html; missing → 404.</summary>
public sealed class WebrootServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public string Url { get; }
    public Func<string, int?>? ForceStatus { get; set; }

    public WebrootServer(string root)
    {
        _l.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = c;
                    var s = c.GetStream();
                    var buf = new byte[4096];
                    var n = await s.ReadAsync(buf);
                    var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                    var file = Path.Combine(root, path == "/" ? "index.html" : path.Trim('/') + ".html");
                    var status = ForceStatus?.Invoke(path) ?? (File.Exists(file) ? 200 : 404);
                    var body = Encoding.UTF8.GetBytes(File.Exists(file) ? File.ReadAllText(file) : "not found");
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await s.WriteAsync(body);
                });
            }
        });
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

/// <summary>
/// End-to-end runs of the 3-step pipeline: real git origin, real shell standing in for the server,
/// real HTTP, real MariaDB. Only the SSH transport is replaced (LocalBashShell runs the same commands).
/// </summary>
public sealed class PipelineTests : IClassFixture<MariaDbFixture>, IDisposable
{
    private readonly MariaDbFixture _db;
    private readonly TempDir _tmp = new();
    private readonly string _origin, _work, _live, _staging, _backups;
    private readonly WebrootServer _liveWeb, _stagingWeb;
    private readonly List<LogLine> _log = [];

    public PipelineTests(MariaDbFixture db)
    {
        _db = db;
        (_origin, _work) = GitHelper.CreateRepo(_tmp.Path);
        // Site content + a migration, on staging.
        File.WriteAllText(Path.Combine(_work, "index.html"), "<h1>v1</h1>");
        File.WriteAllText(Path.Combine(_work, "contact.html"), "<form></form>");
        File.WriteAllText(Path.Combine(_work, ".gitignore"), ".env\n");
        Directory.CreateDirectory(Path.Combine(_work, "database/migrations"));
        File.WriteAllText(Path.Combine(_work, "database/migrations/001_customers.sql"),
            "CREATE TABLE IF NOT EXISTS customers (id INT PRIMARY KEY, name VARCHAR(50));");
        GitHelper.Git(_work, "add", ".");
        GitHelper.Git(_work, "commit", "-qm", "site v1");
        GitHelper.Git(_work, "push", "-q");
        // Live = clone of main (as it is on the server today).
        _live = Path.Combine(_tmp.Path, "live");
        GitHelper.Git(_tmp.Path, "clone", "-q", "-b", "main", _origin, _live);
        _staging = Path.Combine(_tmp.Path, "staging");
        _backups = Path.Combine(_tmp.Path, "backups");
        Directory.CreateDirectory(_staging);
        WriteEnv(_live, "live_db");
        WriteEnv(_staging, "staging_db");
        _liveWeb = new WebrootServer(_live);
        _stagingWeb = new WebrootServer(_staging);
    }

    private void WriteEnv(string dir, string database) =>
        File.WriteAllText(Path.Combine(dir, ".env"),
            $"DB_HOST=127.0.0.1\nDB_PORT={_db.Port}\nDB_DATABASE={database}\nDB_USERNAME=app\nDB_PASSWORD='p\"w'd'\n");

    private DeployPipeline Pipeline(bool withDb = true)
    {
        var m = ManifestTests.Valid();
        m.Server.LivePath = _live;
        m.Server.StagingPath = _staging;
        m.Server.BackupPath = _backups;
        m.Server.StagingUrl = _stagingWeb.Url;
        m.Server.ProductionUrl = _liveWeb.Url;
        m.Repository.Url = _origin;
        m.Repository.LocalPath = _work;
        m.Database.Strategy = withDb ? "incremental_sql" : "none";
        m.SmokeTests.Endpoints = ["/", "/contact"];
        m.SmokeTests.TimeoutSeconds = 5;
        var p = new DeployPipeline(m, _ => Task.FromResult<IRemoteShell>(new LocalBashShell()), Smoke.SmokeTester.CreateDefaultClient());
        p.Log += _log.Add;
        return p;
    }

    private string LogText => string.Join('\n', _log.Select(l => $"{l.Level}: {l.Message}"));

    [SkippableFact]
    public async Task Full_flow_staging_review_promote()
    {
        Skip.IfNot(_db.Available, _db.Reason);
        _db.Sql("live_db", "CREATE TABLE orders (id INT PRIMARY KEY); INSERT INTO orders VALUES (1),(2);");
        var p = Pipeline();

        Assert.False(p.CanPromote);
        var staged = await p.DeployToStagingAsync();
        Assert.True(staged.Success, LogText);
        Assert.Equal("<h1>v1</h1>", File.ReadAllText(Path.Combine(_staging, "index.html")));
        Assert.Equal("customers", _db.Sql("staging_db", "SHOW TABLES LIKE 'customers'"));
        Assert.Equal("", _db.Sql("live_db", "SHOW TABLES LIKE 'customers'"));   // live untouched by staging
        Assert.False(p.CanPromote);                                              // review still required

        p.MarkReviewed();
        Assert.True(p.CanPromote);
        var promoted = await p.PromoteAsync();
        Assert.True(promoted.Success, LogText);

        Assert.Equal("<h1>v1</h1>", File.ReadAllText(Path.Combine(_live, "index.html")));
        Assert.Equal("customers", _db.Sql("live_db", "SHOW TABLES LIKE 'customers'"));
        Assert.Equal("2", _db.Sql("live_db", "SELECT COUNT(*) FROM orders"));     // existing live rows preserved
        Assert.Equal("001_customers.sql", _db.Sql("live_db", "SELECT filename FROM _schema_migrations"));
        Assert.Equal("v1", GitHelper.Git(_origin, "show", "main:index.html").Replace("<h1>", "").Replace("</h1>", ""));
        Assert.Single(Directory.GetDirectories(_backups));                       // snapshot made
        Assert.Empty(Directory.GetFiles("/tmp", ".preflight-*.cnf"));             // option files removed

        // Second staging run: nothing new to migrate, idempotent.
        Assert.True((await p.DeployToStagingAsync()).Success, LogText);
        Assert.Contains("0 applied, 1 already present", LogText);
    }

    [SkippableFact]
    public async Task Failed_live_smoke_test_rolls_back_files_and_database()
    {
        Skip.IfNot(_db.Available, _db.Reason);
        _db.Sql("live_db", "CREATE TABLE orders (id INT PRIMARY KEY); INSERT INTO orders VALUES (1);");
        var p = Pipeline();
        Assert.True((await p.DeployToStagingAsync()).Success, LogText);
        p.MarkReviewed();

        _liveWeb.ForceStatus = path => path == "/contact" ? 500 : null;   // e.g. missing PHP module on live only
        var result = await p.PromoteAsync();

        Assert.False(result.Success);
        Assert.Equal(StepState.Failed, p.PromoteState);
        Assert.False(File.Exists(Path.Combine(_live, "index.html")));                       // files restored
        Assert.Equal("", _db.Sql("live_db", "SHOW TABLES LIKE 'customers'"));               // migration undone
        Assert.Equal("1", _db.Sql("live_db", "SELECT COUNT(*) FROM orders"));               // data back
        Assert.Equal("", _db.Sql("live_db", "SHOW TABLES LIKE '_schema_migrations'"));
        Assert.True(File.Exists(Path.Combine(_live, ".env")));
        Assert.Contains("Rolled back to snapshot", LogText);
        Assert.Contains("already contains the merge", LogText);
    }

    [SkippableFact]
    public async Task Staging_env_pointing_at_live_database_is_refused()
    {
        Skip.IfNot(_db.Available, _db.Reason);
        WriteEnv(_staging, "live_db");
        var p = Pipeline();
        var r = await p.DeployToStagingAsync();
        Assert.False(r.Success);
        Assert.Contains("LIVE database", r.Summary);
        Assert.Equal("", _db.Sql("live_db", "SHOW TABLES LIKE '_schema_migrations'"));
    }

    [SkippableFact]
    public async Task Failing_migration_stops_staging_and_keeps_promote_locked()
    {
        Skip.IfNot(_db.Available, _db.Reason);
        File.WriteAllText(Path.Combine(_work, "database/migrations/002_broken.sql"), "INSERT INTO nope VALUES (1);");
        GitHelper.Git(_work, "add", ".");
        GitHelper.Git(_work, "commit", "-qm", "broken migration");
        GitHelper.Git(_work, "push", "-q");
        var p = Pipeline();
        var r = await p.DeployToStagingAsync();
        Assert.False(r.Success);
        Assert.Contains("002_broken.sql", r.Summary);
        Assert.False(p.CanReview);
        Assert.False(p.CanPromote);
        Assert.Equal("001_customers.sql", _db.Sql("staging_db", "SELECT GROUP_CONCAT(filename) FROM _schema_migrations"));
    }

    [Fact]
    public async Task Merge_conflict_stops_before_the_server_is_touched()
    {
        GitHelper.Git(_work, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "main change\n");
        GitHelper.Git(_work, "commit", "-qam", "m");
        GitHelper.Git(_work, "push", "-q");
        GitHelper.Git(_work, "checkout", "-q", "staging");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "staging change\n");
        GitHelper.Git(_work, "commit", "-qam", "s");
        GitHelper.Git(_work, "push", "-q");

        var p = Pipeline(withDb: false);
        var r = await p.DeployToStagingAsync();
        Assert.False(r.Success);
        Assert.Contains("a.txt", r.Summary);
        Assert.False(Directory.Exists(Path.Combine(_staging, ".git")));
    }

    [Fact]
    public async Task Dirty_or_unpushed_local_repo_is_refused()
    {
        File.WriteAllText(Path.Combine(_work, "x.txt"), "x");
        Assert.Contains("uncommitted", (await Pipeline(false).DeployToStagingAsync()).Summary);
        GitHelper.Git(_work, "add", ".");
        GitHelper.Git(_work, "commit", "-qm", "x");
        Assert.Contains("not pushed", (await Pipeline(false).DeployToStagingAsync()).Summary);
    }

    [Fact]
    public async Task Promote_is_refused_when_staging_moved_after_the_run()
    {
        var p = Pipeline(withDb: false);
        Assert.True((await p.DeployToStagingAsync()).Success, LogText);
        p.MarkReviewed();
        File.WriteAllText(Path.Combine(_work, "late.txt"), "late");
        GitHelper.Git(_work, "add", ".");
        GitHelper.Git(_work, "commit", "-qm", "late");
        GitHelper.Git(_work, "push", "-q");
        var r = await p.PromoteAsync();
        Assert.False(r.Success);
        Assert.Contains("changed since staging passed", r.Summary);
        Assert.False(File.Exists(Path.Combine(_live, "index.html")));
    }

    public void Dispose()
    {
        _liveWeb.Dispose();
        _stagingWeb.Dispose();
        _tmp.Dispose();
    }
}
