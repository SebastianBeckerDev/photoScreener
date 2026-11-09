using System.Text.Json;

namespace PhotoScreener.Infrastructure;

// Optional user-local preferences, independent of photo/export storage.
public sealed class SessionSettingsStore(string? filePath = null)
{
    private readonly object gate = new();
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoScreener", "session-v1.json");
    private sealed record Settings(int SchemaVersion, string LastFolder, string[]? RecentSources = null, string[]? RecentExports = null,
        bool DirectoriesVisible = true, bool InfoVisible = true, bool StripVisible = true, double DirectoryWidth = 260, double InfoWidth = 230);

    public sealed record Preferences(string[] RecentSources, string[] RecentExports, bool DirectoriesVisible, bool InfoVisible,
        bool StripVisible, double DirectoryWidth, double InfoWidth);

    private Settings? ReadSettings()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 64 * 1024) return null;
            var settings = JsonSerializer.Deserialize<Settings>(file);
            return settings?.SchemaVersion == 1 ? settings : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }
    private static string[] Recent(IEnumerable<string>? paths)
    {
        var result = new List<string>(10);
        foreach (var path in paths ?? [])
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
                var canonical = PathGuard.Canonical(path);
                if (!result.Contains(canonical, PathGuard.Comparer)) result.Add(canonical);
                if (result.Count == 10) break;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        }
        return result.ToArray();
    }

    public Preferences ReadPreferences()
    {
        lock (gate)
        {
            var settings = ReadSettings() ?? new(1, "");
            return new(Recent(settings.RecentSources), Recent(settings.RecentExports), settings.DirectoriesVisible, settings.InfoVisible,
                settings.StripVisible, double.IsFinite(settings.DirectoryWidth) ? Math.Clamp(settings.DirectoryWidth, 180, 420) : 260,
                double.IsFinite(settings.InfoWidth) ? Math.Clamp(settings.InfoWidth, 180, 360) : 230);
        }
    }
    public void RememberExport(string folder)
    {
        lock (gate)
        {
            var settings = ReadSettings() ?? new(1, "");
            WriteSettings(settings with { RecentExports = Recent(new[] { PathGuard.Canonical(folder) }.Concat(settings.RecentExports ?? [])) });
        }
    }
    public void RememberLayout(Preferences preferences)
    {
        lock (gate)
        {
            var settings = ReadSettings() ?? new(1, "");
            WriteSettings(settings with { DirectoriesVisible = preferences.DirectoriesVisible, InfoVisible = preferences.InfoVisible,
                StripVisible = preferences.StripVisible, DirectoryWidth = Math.Clamp(preferences.DirectoryWidth, 180, 420), InfoWidth = Math.Clamp(preferences.InfoWidth, 180, 360) });
        }
    }

    public string? ReadLastFolder()
    {
        try
        {
            var settings = ReadSettings();
            return settings?.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(settings.LastFolder) && Path.IsPathFullyQualified(settings.LastFolder)
                ? PathGuard.Canonical(settings.LastFolder) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public void RememberFolder(string folder)
    {
        lock (gate)
        {
            var canonical = PathGuard.Canonical(folder);
            var settings = ReadSettings() ?? new(1, "");
            WriteSettings(settings with { LastFolder = canonical, RecentSources = Recent(new[] { canonical }.Concat(settings.RecentSources ?? [])) });
        }
    }
    private void WriteSettings(Settings settings)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
