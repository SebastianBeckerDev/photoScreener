using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class SessionSettingsTests
{
    [Fact]
    public void IndependentRecentRolesAndLayoutPreserveLastFolderAndOfflineEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionSettingsStore(Path.Combine(root, "session.json"));
            for (var i = 0; i < 12; i++) { store.RememberFolder(Path.Combine(root, "source" + i)); store.RememberExport(Path.Combine(root, "target" + i)); }
            var last = store.ReadLastFolder();
            var preferences = store.ReadPreferences();
            Assert.Equal(10, preferences.RecentSources.Length); Assert.Equal(10, preferences.RecentExports.Length);
            Assert.EndsWith("source11", preferences.RecentSources[0]); Assert.EndsWith("target11", preferences.RecentExports[0]);
            store.RememberLayout(preferences with { DirectoriesVisible = false, InfoVisible = false, StripVisible = false, DirectoryWidth = 310 });
            store.RememberExport(Path.Combine(root, "target5"));
            var restored = new SessionSettingsStore(Path.Combine(root, "session.json")).ReadPreferences();
            Assert.False(restored.DirectoriesVisible); Assert.False(restored.InfoVisible); Assert.False(restored.StripVisible);
            Assert.Equal(310, restored.DirectoryWidth); Assert.Equal(last, store.ReadLastFolder());
            Assert.Equal(10, restored.RecentExports.Distinct().Count()); Assert.EndsWith("target5", restored.RecentExports[0]);
            Assert.All(restored.RecentSources.Concat(restored.RecentExports), path => Assert.False(Directory.Exists(path)));
            File.WriteAllText(Path.Combine(root, "session.json"), "{\"SchemaVersion\":1,\"LastFolder\":\"\",\"RecentSources\":[\"relative\"],\"DirectoryWidth\":99999}");
            Assert.Empty(store.ReadPreferences().RecentSources); Assert.Equal(420, store.ReadPreferences().DirectoryWidth);
        }
        finally { Directory.Delete(root, true); }
    }
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
