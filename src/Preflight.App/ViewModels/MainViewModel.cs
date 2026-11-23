using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Preflight.App.Services;
using Preflight.Core.Git;
using Preflight.Core.Manifest;
using Preflight.Core.Pipeline;
using Preflight.Core.Shell;
using Preflight.Core.Smoke;

namespace Preflight.App.ViewModels;

public sealed record SiteEntry(string ManifestPath, SiteManifest Manifest)
{
    public override string ToString() => Manifest.Domain;
}

public sealed record LogLineVm(string Time, string Level, string Message)
{
    public bool IsError => Level == "FAIL";
    public bool IsWarning => Level == "WARN";
    public bool IsSuccess => Level == "OK";
}

public sealed record SmokeRowVm(string Route, string Status, string Time, string Result, bool Passed)
{
    public bool Failed => !Passed;
}

public sealed record ManifestRowVm(string Key, string Value);

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = SmokeTester.CreateDefaultClient();
    private readonly SiteStore _store;
    private DeployPipeline? _pipeline;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SiteEntry> Sites { get; } = [];
    public ObservableCollection<LogLineVm> Log { get; } = [];
    public ObservableCollection<SmokeRowVm> SmokeRows { get; } = [];
    public ObservableCollection<ManifestRowVm> ManifestRows { get; } = [];

    [ObservableProperty] public partial SiteEntry? SelectedSite { get; set; }
    [ObservableProperty] public partial string SshStatus { get; set; } = "SSH: —";
    [ObservableProperty] public partial bool SshOk { get; set; }
    [ObservableProperty] public partial string GitStatusText { get; set; } = "Git: —";
    [ObservableProperty] public partial bool GitClean { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string StagingCaption { get; set; } = "Add a site to begin";
    [ObservableProperty] public partial string ReviewCaption { get; set; } = "Locked until staging passes";
    [ObservableProperty] public partial string PromoteCaption { get; set; } = "Locked until staging is reviewed";
    [ObservableProperty] public partial bool StagingPassed { get; set; }
    [ObservableProperty] public partial bool StagingFailed { get; set; }
    [ObservableProperty] public partial bool PromotePassed { get; set; }
    [ObservableProperty] public partial bool PromoteFailed { get; set; }
    [ObservableProperty] public partial bool PromoteLocked { get; set; } = true;
    [ObservableProperty] public partial string SmokeSummary { get; set; } = "";
    [ObservableProperty] public partial bool IsDark { get; set; } = true;

    public bool HasSite => SelectedSite is not null;
    public bool HasNoSite => SelectedSite is null;
    public string AiMode => Preflight.Core.Ai.DeepSeekClient.FromEnvironment(Http) is null ? "AI: RULE-BASED" : "AI: DEEPSEEK";

    public MainViewModel() : this(new SiteStore()) { }

    public MainViewModel(SiteStore store)
    {
        _store = store;
        foreach (var path in _store.Load())
        {
            try { Sites.Add(new SiteEntry(path, SiteManifest.Load(path))); }
            catch (Exception e) { Append(LogLevel.Warning, $"Cannot load {path}: {e.Message}"); }
        }
        SelectedSite = Sites.FirstOrDefault();
    }

    public void AddSite(string manifestPath, SiteManifest manifest)
    {
        var existing = Sites.FirstOrDefault(s => s.ManifestPath == manifestPath);
        if (existing is not null) Sites.Remove(existing);
        var entry = new SiteEntry(manifestPath, manifest);
        Sites.Add(entry);
        _store.Save(Sites.Select(s => s.ManifestPath));
        SelectedSite = entry;
    }

    partial void OnSelectedSiteChanged(SiteEntry? value)
    {
        OnPropertyChanged(nameof(HasSite));
        OnPropertyChanged(nameof(HasNoSite));
        _pipeline = null;
        SmokeRows.Clear();
        ManifestRows.Clear();
        SmokeSummary = "";
        if (value is null) { RefreshCommands(); return; }
        var m = value.Manifest;
        foreach (var (k, v) in new[]
                 {
                     ("framework", m.Framework), ("live_path", m.Server.LivePath), ("staging_path", m.Server.StagingPath),
                     ("staging_url", m.Server.StagingUrl), ("production_url", m.Server.ProductionUrl),
                     ("branches", $"{m.Repository.StagingBranch} → {m.Repository.ProductionBranch}"),
                     ("migrations", $"{m.Database.MigrationsDir} ({m.Database.TrackingTable})"),
                     ("routes", string.Join("  ", m.SmokeTests.Endpoints)),
                 })
            ManifestRows.Add(new ManifestRowVm(k, v));

        _pipeline = new DeployPipeline(m, ConnectAsync, Http);
        _pipeline.Log += l => Dispatcher.UIThread.Post(() => Append(l.Level, l.Message, l.Time));
        _pipeline.SmokeResult += r => Dispatcher.UIThread.Post(() => AddSmoke(r));
        _pipeline.StateChanged += () => Dispatcher.UIThread.Post(UpdateStepCaptions);
        UpdateStepCaptions();
        _ = RefreshStatusAsync();
    }

    private async Task<IRemoteShell> ConnectAsync(CancellationToken ct)
    {
        var shell = new SshRemoteShell(SelectedSite!.Manifest.Server);
        await shell.ConnectAsync(ct);
        return shell;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        var site = SelectedSite;
        if (site is null) return;
        try
        {
            var local = site.Manifest.Repository.LocalPath;
            if (local is null || !Directory.Exists(local)) { GitStatusText = "Git: no local folder"; GitClean = false; }
            else
            {
                var st = await new GitPreflight(local).StatusAsync();
                GitClean = st.Clean && st.UnpushedCommits == 0;
                GitStatusText = !st.Clean ? $"Git: {st.DirtyFiles.Count} uncommitted"
                    : st.UnpushedCommits > 0 ? $"Git: {st.UnpushedCommits} unpushed commits" : "Git: Clean";
            }
        }
        catch (Exception e) { GitStatusText = "Git: error"; GitClean = false; Append(LogLevel.Error, e.Message); }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var shell = await ConnectAsync(cts.Token);
            var r = await shell.RunAsync("true", cts.Token);
            SshOk = r.Ok;
            SshStatus = r.Ok ? "SSH connected" : "SSH error";
        }
        catch (Exception e) { SshOk = false; SshStatus = "SSH failed"; Append(LogLevel.Error, $"SSH: {e.Message}"); }
    }

    private bool CanDeploy() => _pipeline is not null && !Busy;
    private bool CanReview() => _pipeline?.CanReview == true && !Busy;
    private bool CanPromote() => _pipeline?.CanPromote == true && !Busy;

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployAsync()
    {
        SmokeRows.Clear();
        await RunAsync(ct => _pipeline!.DeployToStagingAsync(ct));
        await RefreshStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Review()
    {
        var url = SelectedSite!.Manifest.Server.StagingUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Append(LogLevel.Warning, $"Could not open browser ({e.Message}); open {url} yourself."); }
        _pipeline!.MarkReviewed();
    }

    [RelayCommand(CanExecute = nameof(CanPromote))]
    private async Task PromoteAsync()
    {
        SmokeRows.Clear();
        await RunAsync(ct => _pipeline!.PromoteAsync(ct));
        await RefreshStatusAsync();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task<StepResult>> step)
    {
        Busy = true;
        RefreshCommands();
        _cts = new CancellationTokenSource();
        try { await step(_cts.Token); }
        finally
        {
            Busy = false;
            _cts.Dispose();
            _cts = null;
            UpdateStepCaptions();
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        if (Application.Current is { } app) app.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    public string LogText => string.Join(Environment.NewLine, Log.Select(l => $"[{l.Time}] [{l.Level}] {l.Message}"));

    private void UpdateStepCaptions()
    {
        var p = _pipeline;
        if (p is null) return;
        StagingPassed = p.StagingState == StepState.Passed;
        StagingFailed = p.StagingState == StepState.Failed;
        PromotePassed = p.PromoteState == StepState.Passed;
        PromoteFailed = p.PromoteState == StepState.Failed;
        PromoteLocked = !p.CanPromote;
        StagingCaption = p.StagingState switch
        {
            StepState.Running => "Running…",
            StepState.Passed => $"Passed at {p.StagingPassedCommit?[..Math.Min(8, p.StagingPassedCommit.Length)]}",
            StepState.Failed => "Failed — see console",
            _ => "Merge check, pull, migrations, smoke tests",
        };
        ReviewCaption = p.Reviewed ? "Reviewed" : p.CanReview ? "Open staging and check it" : "Locked until staging passes";
        PromoteCaption = p.PromoteState switch
        {
            StepState.Running => "Running…",
            StepState.Passed => "Live and verified",
            StepState.Failed => "Failed — see console",
            _ => p.CanPromote ? "Backup, merge, pull, migrate, verify" : "Locked until staging is reviewed",
        };
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        DeployCommand.NotifyCanExecuteChanged();
        ReviewCommand.NotifyCanExecuteChanged();
        PromoteCommand.NotifyCanExecuteChanged();
    }

    private void AddSmoke(SmokeResult r)
    {
        SmokeRows.Add(new SmokeRowVm(r.Route, r.Status?.ToString() ?? "—", $"{r.ElapsedMs}ms", r.Failure ?? "OK", r.Passed));
        var failed = SmokeRows.Count(s => !s.Passed);
        SmokeSummary = $"{SmokeRows.Count - failed} passed · {failed} failed";
    }

    public void Append(LogLevel level, string message, DateTime? time = null)
    {
        var tag = level switch { LogLevel.Success => "OK", LogLevel.Warning => "WARN", LogLevel.Error => "FAIL", _ => "INFO" };
        Log.Add(new LogLineVm((time ?? DateTime.Now).ToString("HH:mm:ss"), tag, message));
        if (Log.Count > 5000) Log.RemoveAt(0);
    }
}
