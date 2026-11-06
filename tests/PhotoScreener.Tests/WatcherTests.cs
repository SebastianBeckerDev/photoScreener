using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class WatcherTests
{
    [Fact] public async Task ExternalCreationAndDeletionProduceReconciliationHints()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-watch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var watcher = new FolderWatchers(); watcher.Watch(root);
            var path = Path.Combine(root, "a.JPG"); File.WriteAllText(path, "abc");
            await WaitForDirty(); File.Delete(path); await WaitForDirty();
            async Task WaitForDirty()
            {
                for (var i = 0; i < 100; i++) { if (watcher.TakeDirty().Contains(root)) return; await Task.Delay(20); }
                Assert.Fail("External filesystem change did not produce a bounded watcher hint.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
