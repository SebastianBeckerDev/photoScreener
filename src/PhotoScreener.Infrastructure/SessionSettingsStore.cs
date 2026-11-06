using System.Text.Json;

namespace PhotoScreener.Infrastructure;

// Optional user-local preferences, independent of photo/export storage.
public sealed class SessionSettingsStore(string? filePath = null)
{
    private readonly string path = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoScreener", "session-v1.json");
    private sealed record Settings(int SchemaVersion, string LastFolder);

    public string? ReadLastFolder()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 64 * 1024) return null;
            var settings = JsonSerializer.Deserialize<Settings>(file);
            return settings?.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(settings.LastFolder) && Path.IsPathFullyQualified(settings.LastFolder)
                ? PathGuard.Canonical(settings.LastFolder) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public void RememberFolder(string folder)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new Settings(1, PathGuard.Canonical(folder)));
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
