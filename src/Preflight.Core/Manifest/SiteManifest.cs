using System.Text.Json;
using System.Text.Json.Serialization;

namespace Preflight.Core.Manifest;

/// <summary>Contents of <c>site-manifest.json</c> (schema 2.0 from the functional design, §4).</summary>
public sealed class SiteManifest
{
    public string SchemaVersion { get; set; } = "2.0";
    public string SiteId { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Framework { get; set; } = "unknown";
    public ServerSettings Server { get; set; } = new();
    public RepositorySettings Repository { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public SmokeTestSettings SmokeTests { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SiteManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<SiteManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Empty manifest: {path}");
        manifest.Validate();
        return manifest;
    }

    public void Save(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Throws when the manifest is unsafe or incomplete to deploy with.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != "2.0") errors.Add($"unsupported schema_version '{SchemaVersion}'");
        if (string.IsNullOrWhiteSpace(SiteId)) errors.Add("site_id is required");
        if (string.IsNullOrWhiteSpace(Domain)) errors.Add("domain is required");
        if (string.IsNullOrWhiteSpace(Server.SshHost)) errors.Add("server.ssh_host is required");
        if (string.IsNullOrWhiteSpace(Server.SshUser)) errors.Add("server.ssh_user is required");
        if (Server.SshPort is < 1 or > 65535) errors.Add("server.ssh_port is out of range");
        if (!IsAbsolute(Server.LivePath)) errors.Add("server.live_path must be an absolute path");
        if (!IsAbsolute(Server.StagingPath)) errors.Add("server.staging_path must be an absolute path");
        if (Server.LivePath.TrimEnd('/') == Server.StagingPath.TrimEnd('/'))
            errors.Add("server.staging_path must differ from server.live_path");
        if (!Uri.TryCreate(Server.StagingUrl, UriKind.Absolute, out _)) errors.Add("server.staging_url is not a URL");
        if (!Uri.TryCreate(Server.ProductionUrl, UriKind.Absolute, out _)) errors.Add("server.production_url is not a URL");
        if (string.IsNullOrWhiteSpace(Repository.Url)) errors.Add("repository.url is required");
        if (Repository.ProductionBranch == Repository.StagingBranch)
            errors.Add("repository.staging_branch must differ from production_branch");
        if (Database.Strategy is not ("incremental_sql" or "none"))
            errors.Add($"database.strategy '{Database.Strategy}' is not supported (incremental_sql or none)");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Database.TrackingTable, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
            errors.Add("database.tracking_table is not a valid identifier");
        if (SmokeTests.Endpoints.Count == 0) errors.Add("smoke_tests.endpoints must contain at least one route");
        if (SmokeTests.Endpoints.Any(e => !e.StartsWith('/'))) errors.Add("smoke_tests.endpoints must start with '/'");
        if (errors.Count > 0) throw new InvalidDataException("Invalid site manifest: " + string.Join("; ", errors));
    }

    private static bool IsAbsolute(string p) => !string.IsNullOrWhiteSpace(p) && p.StartsWith('/') && !p.Contains("..");
}

public sealed class ServerSettings
{
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public int SshPort { get; set; } = 22;
    /// <summary>Local private key file. Key-based auth only (§2).</summary>
    public string? SshKeyPath { get; set; }
    public string LivePath { get; set; } = "";
    public string StagingPath { get; set; } = "";
    public string StagingUrl { get; set; } = "";
    public string ProductionUrl { get; set; } = "";
    /// <summary>Remote backup root; defaults to ~/.backups/&lt;domain&gt;.</summary>
    public string? BackupPath { get; set; }
}

public sealed class RepositorySettings
{
    public string Provider { get; set; } = "github";
    public string Url { get; set; } = "";
    public string ProductionBranch { get; set; } = "main";
    public string StagingBranch { get; set; } = "staging";
    /// <summary>Local working copy (chosen when the site is added).</summary>
    public string? LocalPath { get; set; }
}

public sealed class DatabaseSettings
{
    public string Strategy { get; set; } = "incremental_sql";
    public string MigrationsDir { get; set; } = "database/migrations";
    public string TrackingTable { get; set; } = "_schema_migrations";
    /// <summary>Remote env file (relative to each webroot) with DB_* credentials. Values are read on use, never stored.</summary>
    public string EnvFile { get; set; } = ".env";
}

public sealed class SmokeTestSettings
{
    public List<string> Endpoints { get; set; } = ["/"];
    public int AssertStatus { get; set; } = 200;
    public List<string> DisallowedStrings { get; set; } =
        ["Fatal error", "Stack trace:", "SQLSTATE", "500 Internal Server Error"];
    public int TimeoutSeconds { get; set; } = 20;
}
