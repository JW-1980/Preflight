using System.Text.RegularExpressions;
using Preflight.Core.Shell;

namespace Preflight.Core.Migrations;

public sealed record DbCredentials(string Host, int Port, string Database, string User, string Password);

public sealed record MigrationOutcome(IReadOnlyList<string> Applied, IReadOnlyList<string> AlreadyApplied);

/// <summary>
/// Incremental SQL migrations (§5, "Optie 2"). Files in the deployed checkout are compared with the
/// tracking table; only new files run, each in its own transaction together with its tracking row.
/// Note: MySQL/MariaDB commit DDL (CREATE/ALTER) implicitly, so ROLLBACK only undoes the data
/// changes of a failed file. Execution stops at the first failing file.
/// </summary>
public sealed class MigrationRunner(IRemoteShell shell, Action<string>? log = null)
{
    private static readonly Regex SafeName = new("^[A-Za-z0-9._-]+\\.sql$");

    /// <summary>Reads DB credentials from an env file on the server (DB_* keys or DATABASE_URL).</summary>
    public async Task<DbCredentials> ReadCredentialsAsync(string envPath, CancellationToken ct = default)
    {
        var r = (await shell.RunAsync($"cat {Sh.Q(envPath)}", ct)).EnsureOk($"read {envPath}");
        return ParseCredentials(r.StdOut)
            ?? throw new InvalidDataException($"No database credentials (DB_* or DATABASE_URL) found in {envPath}");
    }

    public static DbCredentials? ParseCredentials(string envText)
    {
        var env = EnvFile.Parse(envText);
        if (env.TryGetValue("DATABASE_URL", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Scheme is "mysql" or "mariadb")
        {
            var userInfo = u.UserInfo.Split(':', 2);
            return new DbCredentials(u.Host, u.Port > 0 ? u.Port : 3306, Uri.UnescapeDataString(u.AbsolutePath.Trim('/')),
                Uri.UnescapeDataString(userInfo[0]), userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "");
        }
        if (!env.TryGetValue("DB_DATABASE", out var db) || string.IsNullOrEmpty(db)) return null;
        return new DbCredentials(
            env.GetValueOrDefault("DB_HOST", "localhost"),
            int.TryParse(env.GetValueOrDefault("DB_PORT"), out var p) ? p : 3306,
            db,
            env.GetValueOrDefault("DB_USERNAME") ?? env.GetValueOrDefault("DB_USER") ?? "",
            env.GetValueOrDefault("DB_PASSWORD", ""));
    }

    /// <summary>Writes a private MySQL option file and returns its path; caller deletes it.</summary>
    public async Task<string> WriteOptionFileAsync(DbCredentials c, CancellationToken ct = default)
    {
        var path = $"/tmp/.preflight-{Guid.NewGuid():N}.cnf";
        static string Esc(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        await shell.WriteFileAsync(path,
            $"[client]\nhost={Esc(c.Host)}\nport={c.Port}\nuser={Esc(c.User)}\npassword={Esc(c.Password)}\n", ct);
        return path;
    }

    public async Task<MigrationOutcome> RunAsync(string webroot, string migrationsDir, string trackingTable,
        DbCredentials creds, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(trackingTable, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
            throw new ArgumentException("Invalid tracking table name", nameof(trackingTable));
        var dir = $"{webroot.TrimEnd('/')}/{migrationsDir.Trim('/')}";
        var cnf = await WriteOptionFileAsync(creds, ct);
        try
        {
            string Mysql(string extra) =>
                $"mysql --defaults-extra-file={Sh.Q(cnf)} --batch --skip-column-names {extra} {Sh.Q(creds.Database)}";

            (await shell.RunAsync(Mysql("-e " + Sh.Q(
                $"CREATE TABLE IF NOT EXISTS `{trackingTable}` (filename VARCHAR(255) NOT NULL PRIMARY KEY, " +
                "checksum CHAR(64) NOT NULL, applied_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP)")), ct))
                .EnsureOk("create tracking table");

            var appliedRows = (await shell.RunAsync(Mysql("-e " + Sh.Q(
                $"SELECT filename, checksum FROM `{trackingTable}`")), ct)).EnsureOk("read tracking table");
            var applied = appliedRows.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\t')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

            var listing = await shell.RunAsync(
                $"if [ -d {Sh.Q(dir)} ]; then cd {Sh.Q(dir)} && sha256sum -- *.sql 2>/dev/null; fi", ct);
            var files = listing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => (Hash: l[..64], Name: l[66..].Trim()))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

            var plan = Plan(files, applied);
            var done = new List<string>();
            foreach (var (name, hash) in plan.Pending)
            {
                if (!SafeName.IsMatch(name)) throw new InvalidDataException($"Unsafe migration file name: {name}");
                log?.Invoke($"migrate {name}");
                var record = $"INSERT INTO `{trackingTable}` (filename, checksum) VALUES ('{name}', '{hash}');";
                var script = $"{{ printf 'START TRANSACTION;\\n'; cat {Sh.Q(dir + "/" + name)}; " +
                             $"printf '\\n;\\n%s\\nCOMMIT;\\n' {Sh.Q(record)}; }} | {Mysql("")}";
                var r = await shell.RunAsync(script, ct);
                if (!r.Ok)
                    throw new MigrationFailedException(name, r.StdErr.Trim(), done);
                done.Add(name);
            }
            return new MigrationOutcome(done, plan.AlreadyApplied);
        }
        finally
        {
            await shell.RunAsync($"rm -f {Sh.Q(cnf)}", CancellationToken.None);
        }
    }

    public sealed record MigrationPlan(IReadOnlyList<(string Name, string Hash)> Pending, IReadOnlyList<string> AlreadyApplied);

    /// <summary>Pure planning step: which files are new; refuses edited, already-applied files.</summary>
    public static MigrationPlan Plan(IEnumerable<(string Hash, string Name)> files, IReadOnlyDictionary<string, string> applied)
    {
        var pending = new List<(string, string)>();
        var already = new List<string>();
        foreach (var (hash, name) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (applied.TryGetValue(name, out var oldHash))
            {
                if (!string.Equals(oldHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Migration {name} was changed after it was applied (checksum differs). Add a new migration instead.");
                already.Add(name);
            }
            else pending.Add((name, hash));
        }
        return new MigrationPlan(pending, already);
    }
}

public sealed class MigrationFailedException(string file, string error, IReadOnlyList<string> appliedBefore)
    : Exception($"Migration {file} failed and was rolled back: {error}")
{
    public string File { get; } = file;
    public IReadOnlyList<string> AppliedBefore { get; } = appliedBefore;
}

public static class EnvFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ")) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && (val[0] == '"' && val[^1] == '"' || val[0] == '\'' && val[^1] == '\''))
                val = val[1..^1];
            result[key] = val;
        }
        return result;
    }
}
