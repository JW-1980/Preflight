# Preflight

A local desktop tool that deploys a site from its Git repository over SSH, in three steps:
**Deploy to Staging & Run Tests → Review Staging → Promote to Production**. It runs on your own machine,
so there are no CI runner costs. It works with any Linux host that has SSH access, any Git host, and Laravel, Statamic,
Symfony, Drupal, Django, Node/Express, plain PHP or static sites. WordPress is not supported.

Built with .NET 10 and Avalonia (Windows, macOS, Linux). MIT licensed.

## What it does

| Step | Actions |
|---|---|
| **1. Deploy to Staging & Run Tests** | Local repo must be clean and pushed → virtual merge `origin/staging` → `origin/main` (`git merge-tree`, nothing touched on conflict) → server checks out the exact staging commit in `~/.staging/<domain>` → new SQL migrations on the **staging** database → parallel HTTP smoke tests (status + forbidden strings such as `Fatal error`, `SQLSTATE`). |
| **2. Review Staging** | Opens the staging URL in your browser and unlocks step 3. |
| **3. Promote to Production** | Refuses if `origin/staging` moved since step 1 → snapshot of live files (`tar`) and database (`mysqldump`) in `~/.backups/<domain>/<timestamp>` → merge staging into main in a temporary worktree and push → `git pull --ff-only` in the live webroot → new migrations on live → smoke tests on live → **automatic rollback** of files and database on any failure. |

### Database: incremental migrations only
Live databases are never overwritten by staging. `database/migrations/*.sql` files are compared with the
`_schema_migrations` table (filename + SHA-256). Only new files run, each with its tracking row in one
transaction. A migration that was edited after it ran is refused. Credentials are read from the webroot's
`.env` (`DB_*` keys or `DATABASE_URL`) at run time, passed to `mysql` via a temporary mode-600 option file, and
never stored in the manifest. Step 1 refuses to run if the staging `.env` points at the live database.

## Known limits (read before relying on it)
- **DDL is not transactional in MySQL/MariaDB.** `CREATE`/`ALTER` commit implicitly; a failing file only
  rolls back its data changes. Keep one schema change per migration file.
- **A rollback restores the database snapshot.** Orders, sign-ups and other writes made on live between the snapshot and
  the rollback are lost. Preflight warns about this in the console. It is not "within 2 seconds" for large sites, because
  the time depends on the size of the files and the database.
- If promotion fails **after** the push, `origin/main` already contains the merge. Preflight tells you, and you must revert it.
- The staging **subdomain** and the **staging database** can't be created over SSH on shared hosting. Discovery lists them
  as manual steps.

## Adding a site (discovery)
"+ Add site" asks for the local Git folder and SSH (key only). The discovery agent:
1. lists webroots (`~/domains/*/public_html`, `~/public_html`, `/var/www/*`) and picks the one whose `git remote` matches yours;
2. reads `composer.json`, `package.json`, `.env`, `config.php` to identify the framework;
3. creates `~/.staging/<domain>` and checks whether `staging.<domain>` resolves;
4. collects GET routes from `routes/*.php`, Symfony `#[Route]`, Express `.get()` and `pages/`;
5. writes `site-manifest.json` into your repository.

With `DEEPSEEK_API_KEY` set, DeepSeek interprets the layout and picks representative routes. **Secrets are blanked
before sending** (passwords, keys, tokens, `APP_KEY`, credentials in URLs, private keys); key names stay visible.
Everything the model proposes is checked (the webroot must exist, the domain must be valid, routes must start with `/`).
Without a key, rule-based detection is used.

## Build & run
```
dotnet build
dotnet run --project src/Preflight.App
dotnet test
# optional real-SSH run (SSH server on this machine, same filesystem):
# PREFLIGHT_SSH_TEST=user@127.0.0.1:2222:/path/to/key dotnet test
```
Requirements: .NET 10 SDK, `git` ≥ 2.38 locally; `git`, `bash`, `tar`, `mysql`/`mysqldump` on the server.

## Windows installer
```
installer/build-windows-installer.sh
```
This produces `artifacts/Preflight-<version>-win-x64-setup.exe`: a self-contained installer, so the target PC needs no .NET.
It needs the .NET 10 SDK and NSIS 3 (`makensis`), and runs on Linux, macOS or Windows.
- It installs per user into `%LOCALAPPDATA%\Programs\Preflight`, without administrator rights. It adds a Start menu shortcut,
  an optional desktop shortcut, and an entry in *Apps & features*.
- Supports Windows 10/11 x64. ARM64 PCs run it through x64 emulation.
- The installer is **not code-signed**, so Windows SmartScreen shows "unknown publisher" until it is signed with a certificate.
- Uninstalling keeps your linked-site list in `%APPDATA%\Preflight`.

## Layout
- `src/Preflight.Core`: manifest, git checks, SSH (SSH.NET), migrations, smoke tests, snapshots, discovery, DeepSeek client, pipeline
- `src/Preflight.App`: Avalonia UI (light/dark design tokens)
- `tests/Preflight.Core.Tests`: xUnit tests against real git repositories, real HTTP and a real shell
- `design/`: Google Stitch designs (main window, add-site dialog, icon)
