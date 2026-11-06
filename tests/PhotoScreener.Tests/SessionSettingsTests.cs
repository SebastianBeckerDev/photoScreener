using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class SessionSettingsTests
{
    [Fact]
    public void LastFolderSurvivesMissingDriveAndMalformedPreferencesAreOptional()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings", "session.json");
            var folder = Path.Combine(root, "disconnected-drive", "photos");
            var store = new SessionSettingsStore(path);
            Assert.Null(store.ReadLastFolder());
            store.RememberFolder(folder);
            Assert.Equal(folder, new SessionSettingsStore(path).ReadLastFolder());
            Assert.False(Directory.Exists(folder));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            foreach (var content in new[] { "broken", "null", "{\"SchemaVersion\":1,\"LastFolder\":\"relative\"}", "{\"SchemaVersion\":2,\"LastFolder\":\"ignored\"}" })
            { File.WriteAllText(path, content); Assert.Null(store.ReadLastFolder()); }
            store.RememberFolder(root);
            Assert.Equal(root, store.ReadLastFolder());
            File.WriteAllBytes(path, new byte[64 * 1024 + 1]);
            Assert.Null(store.ReadLastFolder());
        }
        finally { Directory.Delete(root, true); }
    }
}
