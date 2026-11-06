using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class MetadataTests
{
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
