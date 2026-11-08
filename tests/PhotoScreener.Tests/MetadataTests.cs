using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class MetadataTests
{
    [Fact] public void RelativeMetadataSurvivesCopyingANestedExportTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-relative-meta-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "original", "Sommerurlaub"); Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "a.JPG"), "abc");
            var photo = new SourceCatalog().Enumerate(source, default).Single();
            var binding = new ExportBinding(source, Path.Combine(source, "Export", "Luminar", "Again Exported"));
            var comparer = new FolderComparer();
            using (var owner = new ExportWriter(binding, [source])) new ExportStore(comparer).Pick(photo, binding, () => true, default);
            var store = new ReviewMetadataStore();
            store.Write(binding, new(photo.Name, photo.Revision, ReviewFilter.Picked, [ReviewMetadataStore.RevisionKey(photo)]), [source]);
            var metadataPath = Path.Combine(binding.ExportRoot, ".photoscreener", "review.json");
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath)))
            {
                Assert.Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
                Assert.Equal("../../../..", json.RootElement.GetProperty("Folders").EnumerateObject().Single().Name);
            }
            var copy = Path.Combine(root, "copied", "Renamed holiday");
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
                Directory.CreateDirectory(Path.Combine(copy, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(copy, Path.GetRelativePath(source, file)));
            Directory.Delete(Path.Combine(root, "original"), true);
            var copiedBinding = new ExportBinding(copy, Path.Combine(copy, "Export", "Luminar", "Again Exported"));
            var recovered = store.Read(copiedBinding)!;
            Assert.Equal(photo.Name, recovered.CursorName); Assert.Equal(ReviewFilter.Picked, recovered.Filter);
            Assert.Single(recovered.ReviewedRevisions);
            var copiedPhoto = new SourceCatalog().Enumerate(copy, default).Single();
            Assert.True(new FolderComparer().Compare(copiedPhoto, comparer.Index(copiedBinding, default), [copiedPhoto], default).Picked);
            store.Write(copiedBinding, recovered, [copy]);
            Assert.Single(System.Text.Json.JsonSerializer.Deserialize<ReviewMetadata>(File.ReadAllText(Path.Combine(copiedBinding.ExportRoot, ".photoscreener", "review.json")))!.Folders);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void LegacyAbsoluteKeysMigrateWithoutLosingSharedFolderHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-meta-migration-" + Guid.NewGuid().ToString("N"));
        var one = Path.Combine(root, "one"); var two = Path.Combine(root, "two"); var target = Path.Combine(root, "exports");
        Directory.CreateDirectory(one); Directory.CreateDirectory(two); Directory.CreateDirectory(Path.Combine(target, ".photoscreener"));
        try
        {
            var first = new FolderReview("a.JPG", null, ReviewFilter.Picked, ["first"]);
            var second = new FolderReview("b.JPG", null, ReviewFilter.Unpicked, ["second"]);
            var path = Path.Combine(target, ".photoscreener", "review.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new ReviewMetadata(1, new Dictionary<string, FolderReview> { [one] = first, [two] = second })));
            var store = new ReviewMetadataStore(); var binding = new ExportBinding(one, target);
            Assert.Equal("a.JPG", store.Read(binding)!.CursorName);
            store.Write(binding, first with { CursorName = "updated.JPG" }, [one, two]);
            var saved = System.Text.Json.JsonSerializer.Deserialize<ReviewMetadata>(File.ReadAllText(path))!;
            Assert.Equal(2, saved.SchemaVersion); Assert.Equal(2, saved.Folders.Count);
            Assert.Contains("../../one", saved.Folders.Keys); Assert.Contains("../../two", saved.Folders.Keys);
            Assert.Equal("updated.JPG", store.Read(binding)!.CursorName);
            Assert.Equal("b.JPG", store.Read(new(two, target))!.CursorName);
            Assert.Null(store.Read(new(root, target))); // A shared target must not restore another folder's cursor.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void MissingCorruptAndRemovedMetadataCannotLoseCompletedPicks()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-meta-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.JPG"), "abc"); var photo = new SourceCatalog().Enumerate(root, default).Single();
            var binding = new ExportBinding(root, Path.Combine(root, "export")); var metadata = new ReviewMetadataStore();
            Assert.Null(metadata.Read(binding)); Assert.False(Directory.Exists(binding.ExportRoot));
            var comparer = new FolderComparer(); using (var owner = new ExportWriter(binding, [root])) new ExportStore(comparer).Pick(photo, binding, () => true, default);
            var review = new FolderReview(photo.Name, photo.Revision, ReviewFilter.Picked, [ReviewMetadataStore.RevisionKey(photo)]);
            metadata.Write(binding, review, [root]); var recovered = metadata.Read(binding)!;
            Assert.Equal(review.CursorRevision, recovered.CursorRevision); Assert.Equal(ReviewFilter.Picked, recovered.Filter); Assert.Single(recovered.ReviewedRevisions);
            var file = Path.Combine(binding.ExportRoot, ".photoscreener", "review.json"); File.WriteAllText(file, "corrupt");
            Assert.Throws<System.Text.Json.JsonException>(() => metadata.Read(binding));
            File.WriteAllText(file, "{\"SchemaVersion\":1,\"Folders\":null}");
            Assert.Throws<InvalidDataException>(() => metadata.Read(binding));
            Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
            Directory.Delete(Path.GetDirectoryName(file)!, true);
            Assert.Null(metadata.Read(binding)); Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task MetadataMaintenanceDoesNotFightMutationOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-meta-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.JPG"), "abc"); var photo = new SourceCatalog().Enumerate(root, default).Single(); var binding = new ExportBinding(root, Path.Combine(root, "export"));
            var comparer = new FolderComparer(); var metadata = new ReviewMetadataStore();
            await using var coordinator = new MutationCoordinator(comparer); coordinator.Register([(photo, binding)]);
            string? failure = null; coordinator.MaintenanceFailed += message => failure = message;
            coordinator.Submit(new(photo, binding, true, false, 1));
            coordinator.RunIdle("review", () => metadata.Write(binding, new(photo.Name, photo.Revision, ReviewFilter.All, []), [root]));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(failure); Assert.NotNull(metadata.Read(binding)); Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
        }
        finally { Directory.Delete(root, true); }
    }
}
