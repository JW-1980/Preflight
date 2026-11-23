using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Preflight.Core.Ai;
using Preflight.Core.Discovery;
using Preflight.Core.Manifest;
using Preflight.Core.Shell;

namespace Preflight.App.ViewModels;

public sealed record FindingVm(string Icon, string Text, bool IsWarning, bool IsInfo);

public partial class AddSiteViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    [ObservableProperty] public partial string LocalFolder { get; set; } = "";
    [ObservableProperty] public partial string SshHost { get; set; } = "";
    [ObservableProperty] public partial string SshUser { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "22";
    [ObservableProperty] public partial string KeyPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial SiteManifest? Result { get; set; }

    public ObservableCollection<FindingVm> Findings { get; } = [];
    public bool AiEnabled => DeepSeekClient.FromEnvironment(Http) is not null;
    public string AiChip => AiEnabled ? "AI ENABLED" : "RULE-BASED";
    public bool HasResult => Result is not null;

    /// <summary>Set by the dialog: called with the saved manifest path.</summary>
    public Action<string, SiteManifest>? Saved { get; set; }

    partial void OnResultChanged(SiteManifest? value) => OnPropertyChanged(nameof(HasResult));

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        Error = null;
        Result = null;
        Findings.Clear();
        if (!Directory.Exists(Path.Combine(LocalFolder, ".git"))) { Error = "Local Git folder is not a git repository."; return; }
        if (string.IsNullOrWhiteSpace(SshHost) || string.IsNullOrWhiteSpace(SshUser)) { Error = "SSH host and user are required."; return; }
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535) { Error = "Port must be 1–65535."; return; }

        Running = true;
        try
        {
            var server = new ServerSettings { SshHost = SshHost.Trim(), SshUser = SshUser.Trim(), SshPort = port, SshKeyPath = KeyPath };
            await using var shell = new SshRemoteShell(server);
            Add(new Finding(FindingKind.Info, $"Connecting to {SshUser}@{SshHost}:{port}…"));
            await shell.ConnectAsync();
            Add(new Finding(FindingKind.Found, "SSH connection established"));
            var agent = new DiscoveryAgent(shell, DeepSeekClient.FromEnvironment(Http), f => Dispatcher.UIThread.Post(() => Add(f)));
            Result = await agent.RunAsync(new DiscoveryInput(LocalFolder, server.SshHost, server.SshUser, port, KeyPath));
            Add(new Finding(FindingKind.Found, "Discovery complete — review and save the manifest"));
        }
        catch (Exception e)
        {
            Error = e.Message;
            Add(new Finding(FindingKind.Warning, e.Message));
        }
        finally { Running = false; }
    }

    [RelayCommand]
    private void Save()
    {
        if (Result is null) return;
        try
        {
            var path = Path.Combine(LocalFolder, "site-manifest.json");
            Result.Save(path);
            Saved?.Invoke(path, Result);
        }
        catch (Exception e) { Error = e.Message; }
    }

    private void Add(Finding f) => Findings.Add(new FindingVm(
        f.Kind switch { FindingKind.Found => "✓", FindingKind.Warning => "!", FindingKind.ManualStep => "⚠", _ => "›" },
        f.Text, f.Kind is FindingKind.Warning or FindingKind.ManualStep, f.Kind == FindingKind.Info));
}
