using Preflight.Core.Migrations;
using Preflight.Core.Shell;

namespace Preflight.Core.Backup;

public sealed record Snapshot(string Directory, string FilesArchive, string? DatabaseDump);

/// <summary>Safety backup before promotion and rollback after a failed live check (§5 step 3.1/3.4).</summary>
public sealed class SnapshotService(IRemoteShell shell, MigrationRunner db, Action<string>? log = null)
{
    public async Task<Snapshot> CreateAsync(string backupRoot, string livePath, DbCredentials? creds,
        CancellationToken ct = default)
    {
        var dir = $"{backupRoot.TrimEnd('/')}/{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var archive = $"{dir}/files.tar.gz";
        (await shell.RunAsync($"mkdir -p {Sh.Q(dir)} && chmod 700 {Sh.Q(dir)}", ct)).EnsureOk("create backup dir");
        log?.Invoke($"backup files → {archive}");
        (await shell.RunAsync($"tar -C {Sh.Q(livePath)} -czf {Sh.Q(archive)} .", ct)).EnsureOk("tar live files");

        string? dump = null;
        if (creds is not null)
        {
            dump = $"{dir}/database.sql.gz";
            log?.Invoke($"backup database {creds.Database} → {dump}");
            var cnf = await db.WriteOptionFileAsync(creds, ct);
            try
            {
                (await shell.RunAsync(Sh.Bash(
                    $"mysqldump --defaults-extra-file={Sh.Q(cnf)} --single-transaction --routines " +
                    $"--triggers --add-drop-table {Sh.Q(creds.Database)} | gzip > {Sh.Q(dump)}"), ct))
                    .EnsureOk("mysqldump");
            }
            finally { await shell.RunAsync($"rm -f {Sh.Q(cnf)}", CancellationToken.None); }
        }
        return new Snapshot(dir, archive, dump);
    }

    /// <summary>
    /// Restores files and database from the snapshot. Writes to the live database between the snapshot and
    /// the rollback (orders, sign-ups) are lost by the database restore; the caller reports this.
    /// </summary>
    public async Task RestoreAsync(Snapshot snap, string livePath, DbCredentials? creds, CancellationToken ct = default)
    {
        log?.Invoke($"rollback files from {snap.FilesArchive}");
        var staging = $"{livePath.TrimEnd('/')}.preflight-restore";
        (await shell.RunAsync(
            $"rm -rf {Sh.Q(staging)} && mkdir -p {Sh.Q(staging)} && tar -C {Sh.Q(staging)} -xzf {Sh.Q(snap.FilesArchive)} && " +
            $"find {Sh.Q(livePath)} -mindepth 1 -maxdepth 1 -exec rm -rf {{}} + && " +
            $"(cd {Sh.Q(staging)} && find . -mindepth 1 -maxdepth 1 -exec mv {{}} {Sh.Q(livePath)}/ \\;) && rmdir {Sh.Q(staging)}", ct))
            .EnsureOk("restore files");

        if (snap.DatabaseDump is not null && creds is not null)
        {
            log?.Invoke($"rollback database {creds.Database}");
            var cnf = await db.WriteOptionFileAsync(creds, ct);
            try
            {
                // Drop every table first: tables created by the failed promotion are not in the dump.
                var mysql = $"mysql --defaults-extra-file={Sh.Q(cnf)} {Sh.Q(creds.Database)}";
                const string listDrops =
                    "SELECT CONCAT('DROP ', IF(table_type='VIEW','VIEW','TABLE'), ' IF EXISTS `', REPLACE(table_name,'`','``'), '`;') " +
                    "FROM information_schema.tables WHERE table_schema = DATABASE()";
                (await shell.RunAsync(Sh.Bash(
                    $"{{ echo 'SET FOREIGN_KEY_CHECKS=0;'; {mysql} -N -B -e {Sh.Q(listDrops)}; echo 'SET FOREIGN_KEY_CHECKS=1;'; }} | {mysql}"), ct))
                    .EnsureOk("clear database before restore");
                (await shell.RunAsync(Sh.Bash($"gunzip -c {Sh.Q(snap.DatabaseDump)} | {mysql}"), ct))
                    .EnsureOk("restore database");
            }
            finally { await shell.RunAsync($"rm -f {Sh.Q(cnf)}", CancellationToken.None); }
        }
    }
}
