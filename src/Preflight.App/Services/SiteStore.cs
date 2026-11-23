using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Preflight.App.Services;

/// <summary>Remembers which site-manifest.json files are linked, in the user's app-data folder.</summary>
public sealed class SiteStore
{
    private readonly string _file;

    public SiteStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Preflight");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "sites.json");
    }

    public List<string> Load()
    {
        if (!File.Exists(_file)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? []; }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<string> manifestPaths) =>
        File.WriteAllText(_file, JsonSerializer.Serialize(manifestPaths.Distinct().ToList()));
}
