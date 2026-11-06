using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class LibraryTests
{
    [Fact] public async Task ProgressiveProjectPicksUseEachFoldersBindingAndExcludeExports()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "one"); var b = Path.Combine(root, "two"); var custom = Path.Combine(root, "custom"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "same.JPG"), "abc"); File.WriteAllText(Path.Combine(b, "same.JPG"), "xyz");
            var resolver = new ExportBindingResolver(); resolver.Bind(b, custom, [a, b]); var comparer = new FolderComparer();
            foreach (var folder in new[] { a, b })
            {
                var binding = resolver.Resolve(folder); var photo = new SourceCatalog().Enumerate(folder, default).Single();
                using var owner = new ExportWriter(binding, [a, b]); new ExportStore(comparer).Pick(photo, binding, () => true, default);
            }
            var batches = new List<LibraryBatch>(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var worker = new LibraryWorker(resolver, comparer);
            worker.Submit(new(root, true, batch => { lock (batches) batches.Add(batch); if (batch.Complete) done.TrySetResult(); return Task.CompletedTask; }, default, []));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, batches.SelectMany(b => b.Rows).Count(r => r.Observation.Picked == true));
            Assert.DoesNotContain(batches, batch => PathGuard.Within(batch.Folder, custom) || Path.GetFileName(batch.Folder).Equals("export", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(batches, batch => batch.FolderDone && batch.Folder == a);
            Assert.Contains(batches, batch => batch.FolderDone && batch.Folder == b);
        }
        finally { Directory.Delete(root, true); }
    }
}
