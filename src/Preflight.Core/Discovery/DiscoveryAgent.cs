using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Preflight.Core.Ai;
using Preflight.Core.Git;
using Preflight.Core.Manifest;
using Preflight.Core.Shell;

namespace Preflight.Core.Discovery;

public enum FindingKind { Info, Found, Warning, ManualStep }

public sealed record Finding(FindingKind Kind, string Text);

public sealed record DiscoveryInput(string LocalRepoPath, string SshHost, string SshUser, int SshPort, string? SshKeyPath);

/// <summary>
/// Server discovery (§4.1). Facts (paths, git remotes, files) are gathered with fixed commands; the AI
/// (DeepSeek) only proposes the domain, framework and smoke-test routes from a redacted summary.
/// Without an AI key, rule-based detection is used. WordPress is intentionally not supported.
/// </summary>
public sealed partial class DiscoveryAgent(IRemoteShell shell, IAiClient? ai, Action<Finding> report)
{
    public static readonly string[] SupportedFrameworks =
        ["laravel", "statamic", "symfony", "drupal", "django", "node-express", "php", "static"];

    public async Task<SiteManifest> RunAsync(DiscoveryInput input, CancellationToken ct = default)
    {
        var localOrigin = (await LocalProcess.RunAsync("git", ["remote", "get-url", "origin"], input.LocalRepoPath, ct))
            .EnsureOk("read local origin").StdOut.Trim();
        report(new(FindingKind.Info, $"Local repository origin: {RedactUrl(localOrigin)}"));

        // 1. Webroots
        var home = (await shell.RunAsync("printf %s \"$HOME\"", ct)).EnsureOk("read $HOME").StdOut.Trim();
        var roots = (await shell.RunAsync(
            "for d in \"$HOME\"/domains/*/public_html \"$HOME\"/public_html /var/www/*; do [ -d \"$d\" ] && echo \"$d\"; done; true", ct))
            .StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        foreach (var r in roots) report(new(FindingKind.Found, $"Webroot: {r}"));
        if (roots.Count == 0) throw new InvalidOperationException("No webroots found under ~/domains/*/public_html, ~/public_html or /var/www.");

        // Match the webroot whose git origin equals the local origin.
        string? livePath = null;
        foreach (var r in roots)
        {
            var o = await shell.RunAsync($"git -C {Sh.Q(r)} remote get-url origin 2>/dev/null", ct);
            if (o.Ok && SameRepo(o.StdOut.Trim(), localOrigin)) { livePath = r; break; }
        }

        // 2. Framework, per candidate
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"local_origin: {localOrigin}");
        foreach (var r in livePath is null ? roots : [livePath])
        {
            var ls = await shell.RunAsync($"ls -1A {Sh.Q(r)} 2>/dev/null | head -n 60", ct);
            summary.AppendLine($"## webroot {r}\n{ls.StdOut}");
            foreach (var f in new[] { "composer.json", "package.json", ".env", "config.php" })
            {
                var c = await shell.RunAsync($"head -c 4000 {Sh.Q(r + "/" + f)} 2>/dev/null", ct);
                if (c.Ok && c.StdOut.Length > 0) summary.AppendLine($"### {f}\n{c.StdOut}");
            }
        }
        var redacted = Redactor.Redact(summary.ToString());

        var localRoutes = ExtractLocalRoutes(input.LocalRepoPath);
        string framework;
        string? domain = livePath is null ? null : DomainFromPath(livePath);
        List<string> endpoints;

        if (ai is not null)
        {
            report(new(FindingKind.Info, "Asking DeepSeek to interpret the server layout (secrets blanked)…"));
            var answer = await ai.CompleteJsonAsync(SystemPrompt,
                redacted + "\n## routes found in local repository\n" + string.Join('\n', localRoutes), ct);
            (domain, livePath, framework, endpoints) = ValidateAi(answer, roots, domain, livePath, localRoutes);
            report(new(FindingKind.Found, $"AI: framework {framework}, domain {domain}"));
        }
        else
        {
            report(new(FindingKind.Warning, "No DEEPSEEK_API_KEY set — using rule-based detection."));
            framework = DetectFramework(redacted);
            endpoints = localRoutes.Count > 0 ? localRoutes.Take(25).ToList() : ["/"];
        }

        if (livePath is null || domain is null)
            throw new InvalidOperationException(
                "Could not determine which webroot belongs to this repository (no webroot has the same git origin). " +
                "Clone the repository into the live webroot first, or fill in the manifest by hand.");
        if (framework == "wordpress")
            throw new NotSupportedException("WordPress sites are not supported by Preflight.");
        report(new(FindingKind.Found, $"Live webroot: {livePath}"));
        report(new(FindingKind.Found, $"Framework: {framework}"));

        // 3. Staging (zero-touch where possible)
        var stagingPath = $"{home}/.staging/{domain}";
        (await shell.RunAsync($"mkdir -p {Sh.Q(stagingPath)}", ct)).EnsureOk("create staging dir");
        report(new(FindingKind.Found, $"Staging directory: {stagingPath}"));
        var stagingHost = $"staging.{domain}";
        if (await ResolvesAsync(stagingHost))
            report(new(FindingKind.Found, $"Staging subdomain resolves: {stagingHost}"));
        else
            report(new(FindingKind.ManualStep,
                $"Create subdomain {stagingHost} in your hosting panel with document root {stagingPath} (Preflight cannot do this over SSH)."));
        report(new(FindingKind.ManualStep,
            $"Put a .env for a SEPARATE staging database in {stagingPath}. Preflight refuses to migrate staging when it points at the live database."));

        foreach (var e in endpoints) report(new(FindingKind.Found, $"Route: {e}"));

        var siteId = Regex.Replace(domain.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return new SiteManifest
        {
            SiteId = siteId,
            Domain = domain,
            Framework = framework,
            Server = new ServerSettings
            {
                SshHost = input.SshHost, SshUser = input.SshUser, SshPort = input.SshPort, SshKeyPath = input.SshKeyPath,
                LivePath = livePath, StagingPath = stagingPath,
                StagingUrl = $"https://{stagingHost}", ProductionUrl = $"https://{domain}",
                BackupPath = $"{home}/.backups/{domain}",
            },
            Repository = new RepositorySettings { Provider = ProviderOf(localOrigin), Url = localOrigin, LocalPath = input.LocalRepoPath },
            Database = new DatabaseSettings(),
            SmokeTests = new SmokeTestSettings { Endpoints = endpoints },
        };
    }

    private const string SystemPrompt = """
        You inspect a Linux web hosting account for a deployment tool. Secrets are replaced by «redacted».
        Answer ONLY a JSON object: {"live_path": string, "domain": string, "framework": string, "endpoints": [string]}.
        live_path must be one of the listed webroots. framework is one of: laravel, statamic, symfony, drupal,
        django, node-express, php, static, wordpress. endpoints: up to 25 public GET routes without parameters,
        each starting with "/", representative of distinct templates. Never invent paths that have no evidence.
        """;

    private (string domain, string livePath, string framework, List<string> endpoints) ValidateAi(
        JsonObject a, List<string> roots, string? domain, string? livePath, List<string> localRoutes)
    {
        var aiPath = a["live_path"]?.GetValue<string>();
        if (livePath is null)
        {
            if (aiPath is null || !roots.Contains(aiPath))
                throw new InvalidDataException("AI proposed a webroot that does not exist on the server; rejected.");
            livePath = aiPath;
        }
        var aiDomain = a["domain"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        domain ??= aiDomain is not null && DomainRegex().IsMatch(aiDomain) ? aiDomain
            : throw new InvalidDataException("AI did not return a valid domain.");
        var fw = a["framework"]?.GetValue<string>()?.ToLowerInvariant() ?? "php";
        if (fw != "wordpress" && !SupportedFrameworks.Contains(fw)) fw = "php";
        var eps = (a["endpoints"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
            .Where(e => e.StartsWith('/') && !e.Contains('{') && !e.Contains(' ')).Distinct().Take(25).ToList() ?? [];
        if (eps.Count == 0) eps = localRoutes.Count > 0 ? localRoutes.Take(25).ToList() : ["/"];
        if (!eps.Contains("/")) eps.Insert(0, "/");
        return (domain, livePath, fw, eps);
    }

    public static string DetectFramework(string listing)
    {
        bool Has(string s) => listing.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("wp-config.php") || Has("wp-content")) return "wordpress";
        if (Has("statamic/cms")) return "statamic";
        if (Has("laravel/framework") || Has("\nartisan")) return "laravel";
        if (Has("drupal/core")) return "drupal";
        if (Has("symfony/framework-bundle")) return "symfony";
        if (Has("manage.py")) return "django";
        if (Has("\"express\"")) return "node-express";
        if (Has(".php")) return "php";
        return "static";
    }

    /// <summary>GET routes from Laravel/Symfony/Express route files and page templates in the local repo.</summary>
    public static List<string> ExtractLocalRoutes(string repo)
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal) { "/" };
        void Scan(string glob, Regex rx)
        {
            foreach (var f in SafeFiles(repo, glob))
                foreach (Match m in rx.Matches(File.ReadAllText(f)))
                {
                    var r = "/" + m.Groups[1].Value.Trim('/');
                    if (!r.Contains('{') && !r.Contains(':') && !r.Contains('*')) routes.Add(r);
                }
        }
        Scan("routes/*.php", LaravelRoute());
        Scan("src/Controller/*.php", SymfonyRoute());
        Scan("*.js", ExpressRoute());
        foreach (var dir in new[] { "pages", "templates/pages" })
        {
            var d = Path.Combine(repo, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d))
            {
                var n = Path.GetFileNameWithoutExtension(f).Split('.')[0];
                if (n.StartsWith('_') || n.StartsWith('[')) continue;
                routes.Add(n == "index" ? "/" : "/" + n);
            }
        }
        return routes.ToList();
    }

    private static IEnumerable<string> SafeFiles(string repo, string glob)
    {
        var dir = Path.Combine(repo, Path.GetDirectoryName(glob) ?? "");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, Path.GetFileName(glob)) : [];
    }

    private static async Task<bool> ResolvesAsync(string host)
    {
        try { return (await Dns.GetHostAddressesAsync(host)).Length > 0; }
        catch { return false; }
    }

    public static string? DomainFromPath(string path)
    {
        var m = Regex.Match(path, @"/domains/([^/]+)/public_html");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        m = Regex.Match(path, @"^/var/www/([^/]+)$");
        return m.Success && DomainRegex().IsMatch(m.Groups[1].Value) ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static bool SameRepo(string a, string b) => Normalize(a) == Normalize(b);

    private static string Normalize(string url)
    {
        url = url.Trim().ToLowerInvariant();
        if (url.EndsWith(".git")) url = url[..^4];
        var m = Regex.Match(url, @"^(?:[a-z+]+://)?(?:[^@/]+@)?([^/:]+)[:/](?:\d+/)?(.+)$");
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value.Trim('/')}" : url;
    }

    private static string ProviderOf(string url) =>
        url.Contains("github.com") ? "github" : url.Contains("gitlab") ? "gitlab"
        : url.Contains("bitbucket.org") ? "bitbucket" : "git";

    private static string RedactUrl(string url) => Redactor.Redact(url);

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex DomainRegex();
    [GeneratedRegex(@"Route::(?:get|view)\(\s*['""]([^'""]*)['""]")]
    private static partial Regex LaravelRoute();
    [GeneratedRegex(@"#\[Route\(\s*['""]([^'""]*)['""]")]
    private static partial Regex SymfonyRoute();
    [GeneratedRegex(@"\.get\(\s*['""](/[^'""]*)['""]")]
    private static partial Regex ExpressRoute();
}
