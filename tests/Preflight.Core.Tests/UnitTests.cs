using System.Net;
using System.Net.Sockets;
using System.Text;
using Preflight.Core.Ai;
using Preflight.Core.Discovery;
using Preflight.Core.Git;
using Preflight.Core.Manifest;
using Preflight.Core.Migrations;
using Preflight.Core.Smoke;

namespace Preflight.Core.Tests;

public class RedactorTests
{
    [Theory]
    [InlineData("DB_PASSWORD=hunter2", "hunter2")]
    [InlineData("APP_KEY=base64:abcdef", "abcdef")]
    [InlineData("export STRIPE_SECRET=sk_live_123", "sk_live_123")]
    [InlineData("DATABASE_URL=mysql://app:s3cr3t@db:3306/shop", "s3cr3t")]
    [InlineData("'password' => 'p@ss',", "p@ss")]
    [InlineData("SECRET_KEY = 'django-xyz'", "django-xyz")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----", "AAAA")]
    public void Secrets_are_blanked(string input, string secret)
    {
        var output = Redactor.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains(Redactor.Mask, output);
    }

    [Fact]
    public void Non_secrets_and_key_names_survive()
    {
        var output = Redactor.Redact("DB_DATABASE=shop\nDB_HOST=localhost\nDB_PASSWORD=x1");
        Assert.Contains("DB_DATABASE=shop", output);
        Assert.Contains("DB_HOST=localhost", output);
        Assert.Contains("DB_PASSWORD=", output);
        Assert.DoesNotContain("x1", output);
    }
}

public class ManifestTests
{
    public static SiteManifest Valid() => new()
    {
        SiteId = "voorbeeld-nl",
        Domain = "voorbeeld.nl",
        Server = new ServerSettings
        {
            SshHost = "h", SshUser = "u", LivePath = "/home/u/domains/voorbeeld.nl/public_html",
            StagingPath = "/home/u/.staging/voorbeeld.nl", StagingUrl = "https://staging.voorbeeld.nl",
            ProductionUrl = "https://voorbeeld.nl",
        },
        Repository = new RepositorySettings { Url = "git@github.com:a/b.git" },
        SmokeTests = new SmokeTestSettings { Endpoints = ["/", "/contact"] },
    };

    [Fact]
    public void Round_trips_with_snake_case_like_the_design()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "site-manifest.json");
        Valid().Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"schema_version\": \"2.0\"", json);
        Assert.Contains("\"live_path\"", json);
        Assert.Contains("\"disallowed_strings\"", json);
        var back = SiteManifest.Load(path);
        Assert.Equal("/home/u/.staging/voorbeeld.nl", back.Server.StagingPath);
        Assert.Equal(["/", "/contact"], back.SmokeTests.Endpoints);
    }

    [Fact]
    public void Loads_the_example_from_the_functional_design()
    {
        const string json = """
            {"schema_version":"2.0","site_id":"mijn-project","domain":"voorbeeld.nl",
             "server":{"ssh_host":"server.mijn.host","ssh_user":"u12345p678","ssh_port":22,
               "live_path":"/home/u12345p678/domains/voorbeeld.nl/public_html",
               "staging_path":"/home/u12345p678/.staging/voorbeeld.nl",
               "staging_url":"https://staging.voorbeeld.nl","production_url":"https://voorbeeld.nl"},
             "repository":{"provider":"github","url":"git@github.com:gebruiker/voorbeeld.git","production_branch":"main","staging_branch":"staging"},
             "database":{"strategy":"incremental_sql","migrations_dir":"database/migrations","tracking_table":"_schema_migrations"},
             "smoke_tests":{"endpoints":["/","/over-ons","/diensten","/contact"],"assert_status":200,
               "disallowed_strings":["Fatal error","Stack trace:","SQLSTATE","500 Internal Server Error"]}}
            """;
        using var d = new TempDir();
        var p = Path.Combine(d.Path, "m.json");
        File.WriteAllText(p, json);
        var m = SiteManifest.Load(p);
        Assert.Equal(4, m.SmokeTests.Endpoints.Count);
        Assert.Equal("_schema_migrations", m.Database.TrackingTable);
    }

    [Fact]
    public void Rejects_staging_equal_to_live()
    {
        var m = Valid();
        m.Server.StagingPath = m.Server.LivePath;
        Assert.Contains("staging_path must differ", Assert.Throws<InvalidDataException>(m.Validate).Message);
    }

    [Fact]
    public void Rejects_unsafe_tracking_table_and_relative_paths()
    {
        var m = Valid();
        m.Database.TrackingTable = "x`; DROP TABLE users; --";
        m.Server.LivePath = "relative/path";
        var msg = Assert.Throws<InvalidDataException>(m.Validate).Message;
        Assert.Contains("tracking_table", msg);
        Assert.Contains("live_path", msg);
    }
}

public class MigrationPlanTests
{
    [Fact]
    public void Only_new_files_are_pending_in_order()
    {
        var applied = new Dictionary<string, string> { ["001_init.sql"] = "aa" };
        var plan = MigrationRunner.Plan([("cc", "003_c.sql"), ("aa", "001_init.sql"), ("bb", "002_b.sql")], applied);
        Assert.Equal(["002_b.sql", "003_c.sql"], plan.Pending.Select(p => p.Name));
        Assert.Equal(["001_init.sql"], plan.AlreadyApplied);
    }

    [Fact]
    public void Edited_applied_migration_is_refused()
    {
        var applied = new Dictionary<string, string> { ["001_init.sql"] = "aa" };
        Assert.Throws<InvalidDataException>(() => MigrationRunner.Plan([("zz", "001_init.sql")], applied));
    }

    [Fact]
    public void Credentials_from_env_and_database_url()
    {
        var a = MigrationRunner.ParseCredentials("DB_HOST=db\nDB_PORT=3307\nDB_DATABASE=shop\nDB_USERNAME=app\nDB_PASSWORD=\"p w\"");
        Assert.Equal(new DbCredentials("db", 3307, "shop", "app", "p w"), a);
        var b = MigrationRunner.ParseCredentials("DATABASE_URL=mysql://app:p%40ss@127.0.0.1:3306/shop?serverVersion=8");
        Assert.Equal(new DbCredentials("127.0.0.1", 3306, "shop", "app", "p@ss"), b);
        Assert.Null(MigrationRunner.ParseCredentials("APP_NAME=x"));
    }
}

public class DiscoveryTests
{
    [Theory]
    [InlineData("composer.json\n\"laravel/framework\": \"^11\"", "laravel")]
    [InlineData("\"statamic/cms\": \"^5\" laravel/framework", "statamic")]
    [InlineData("\"symfony/framework-bundle\"", "symfony")]
    [InlineData("\"drupal/core\"", "drupal")]
    [InlineData("manage.py\nrequirements.txt", "django")]
    [InlineData("package.json\n\"express\": \"^4\"", "node-express")]
    [InlineData("index.php\nconfig.php", "php")]
    [InlineData("index.html\nstyle.css", "static")]
    [InlineData("wp-config.php\nwp-content", "wordpress")]
    public void Detects_framework(string listing, string expected) =>
        Assert.Equal(expected, DiscoveryAgent.DetectFramework(listing));

    [Fact]
    public void Same_repo_across_url_styles()
    {
        Assert.True(DiscoveryAgent.SameRepo("git@github.com:Acme/Site.git", "https://github.com/acme/site"));
        Assert.True(DiscoveryAgent.SameRepo("ssh://git@gitea.local:2222/acme/site.git", "git@gitea.local:acme/site.git"));
        Assert.False(DiscoveryAgent.SameRepo("git@github.com:acme/site.git", "git@github.com:acme/other.git"));
    }

    [Fact]
    public void Domain_from_webroot()
    {
        Assert.Equal("voorbeeld.nl", DiscoveryAgent.DomainFromPath("/home/u/domains/voorbeeld.nl/public_html"));
        Assert.Equal("shop.example.com", DiscoveryAgent.DomainFromPath("/var/www/shop.example.com"));
        Assert.Null(DiscoveryAgent.DomainFromPath("/home/u/public_html"));
    }

    [Fact]
    public void Routes_from_laravel_and_pages_skip_parameters()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(Path.Combine(d.Path, "routes"));
        File.WriteAllText(Path.Combine(d.Path, "routes/web.php"),
            "Route::get('/over-ons', fn()=>1); Route::view('contact', 'c'); Route::get('/blog/{slug}', X::class); Route::post('/form', Y::class);");
        Directory.CreateDirectory(Path.Combine(d.Path, "pages"));
        File.WriteAllText(Path.Combine(d.Path, "pages/diensten.vue"), "");
        File.WriteAllText(Path.Combine(d.Path, "pages/[id].vue"), "");
        var routes = DiscoveryAgent.ExtractLocalRoutes(d.Path);
        Assert.Equal(["/", "/contact", "/diensten", "/over-ons"], routes);
    }
}

public class SmokeTesterTests
{
    private static (string url, Task server, CancellationTokenSource cts) Serve(Func<string, (int, string)> handler)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(cts.Token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using var _c = c;
                    var s = c.GetStream();
                    var buf = new byte[4096];
                    var n = await s.ReadAsync(buf);
                    var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                    var (status, body) = handler(path);
                    if (status == 0) { await Task.Delay(5000); return; }
                    var b = Encoding.UTF8.GetBytes(body);
                    await s.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} X\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n"));
                    await s.WriteAsync(b);
                });
            }
            l.Stop();
        });
        return ($"http://127.0.0.1:{port}", task, cts);
    }

    [Fact]
    public async Task Status_body_and_timeout_are_checked()
    {
        var (url, _, cts) = Serve(p => p switch
        {
            "/" => (200, "<h1>ok</h1>"),
            "/broken" => (500, "boom"),
            "/fatal" => (200, "<b>Fatal error</b>: Uncaught Error"),
            "/slow" => (0, ""),
            _ => (404, "nf"),
        });
        using (cts)
        {
            var settings = new SmokeTestSettings { Endpoints = ["/", "/broken", "/fatal", "/slow", "/missing"], TimeoutSeconds = 1 };
            var results = (await new SmokeTester(SmokeTester.CreateDefaultClient()).RunAsync(url, settings))
                .ToDictionary(r => r.Route);
            Assert.True(results["/"].Passed);
            Assert.False(results["/broken"].Passed);
            Assert.Equal(500, results["/broken"].Status);
            Assert.False(results["/fatal"].Passed);
            Assert.Contains("Fatal error", results["/fatal"].Failure);
            Assert.False(results["/slow"].Passed);
            Assert.Contains("timeout", results["/slow"].Failure);
            Assert.False(results["/missing"].Passed);
            cts.Cancel();
        }
    }
}

public class GitPreflightTests
{
    [Fact]
    public async Task Clean_dirty_and_unpushed_are_reported()
    {
        using var d = new TempDir();
        var (_, clone) = GitHelper.CreateRepo(d.Path);
        var git = new GitPreflight(clone);
        var st = await git.StatusAsync();
        Assert.True(st.Clean);
        Assert.Equal(0, st.UnpushedCommits);

        File.WriteAllText(Path.Combine(clone, "b.txt"), "x");
        Assert.Equal(["b.txt"], (await git.StatusAsync()).DirtyFiles);
        GitHelper.Git(clone, "add", ".");
        GitHelper.Git(clone, "commit", "-qm", "b");
        st = await git.StatusAsync();
        Assert.True(st.Clean);
        Assert.Equal(1, st.UnpushedCommits);
    }

    [Fact]
    public async Task Dry_run_merge_detects_conflicts_without_touching_worktree()
    {
        using var d = new TempDir();
        var (_, clone) = GitHelper.CreateRepo(d.Path);
        var git = new GitPreflight(clone);
        Assert.True((await git.DryRunMergeAsync("main", "staging")).CanMerge);

        File.WriteAllText(Path.Combine(clone, "a.txt"), "staging\n");
        GitHelper.Git(clone, "commit", "-qam", "s");
        GitHelper.Git(clone, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(clone, "a.txt"), "main\n");
        GitHelper.Git(clone, "commit", "-qam", "m");
        var head = GitHelper.Git(clone, "rev-parse", "HEAD");

        var check = await git.DryRunMergeAsync("main", "staging");
        Assert.False(check.CanMerge);
        Assert.Equal(["a.txt"], check.ConflictingFiles);
        Assert.Equal(head, GitHelper.Git(clone, "rev-parse", "HEAD"));
        Assert.True((await git.StatusAsync()).Clean);
    }

    [Fact]
    public async Task Merge_and_push_updates_remote_main_but_not_the_checkout()
    {
        using var d = new TempDir();
        var (origin, clone) = GitHelper.CreateRepo(d.Path);
        File.WriteAllText(Path.Combine(clone, "feature.txt"), "f");
        GitHelper.Git(clone, "add", ".");
        GitHelper.Git(clone, "commit", "-qm", "feature");
        GitHelper.Git(clone, "push", "-q");
        var branchBefore = GitHelper.Git(clone, "branch", "--show-current");

        var commit = await new GitPreflight(clone).MergeAndPushAsync("main", "staging");

        Assert.Equal(commit, GitHelper.Git(origin, "rev-parse", "main"));
        Assert.Equal("f", GitHelper.Git(origin, "show", "main:feature.txt"));
        Assert.Equal(branchBefore, GitHelper.Git(clone, "branch", "--show-current"));
        Assert.DoesNotContain("preflight-merge", GitHelper.Git(clone, "worktree", "list"));
    }
}
