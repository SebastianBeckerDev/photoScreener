using System.Text.Json;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public sealed record FolderReview(string? CursorName, PhotoRevision? CursorRevision, ReviewFilter Filter, IReadOnlyList<string> ReviewedRevisions);
public sealed record ReviewMetadata(int SchemaVersion, IReadOnlyDictionary<string, FolderReview> Folders);
public interface IReviewMetadataStore
{
    FolderReview? Read(ExportBinding binding);
    void Write(ExportBinding binding, FolderReview review, IEnumerable<string> protectedFolders);
}
public sealed class ReviewMetadataStore : IReviewMetadataStore
{
    private int successfulWrites;
    public int SuccessfulWrites => Volatile.Read(ref successfulWrites);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathFor(ExportBinding binding) => Path.Combine(binding.ExportRoot, ".photoscreener", "review.json");
    public FolderReview? Read(ExportBinding binding)
    {
        var path = PathFor(binding); PathGuard.ValidateNoLinks(path);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path); if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Optional review metadata exceeds its size limit.");
        using var stream = PlatformFileSystem.OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var data = JsonSerializer.Deserialize<ReviewMetadata>(stream);
        if (data?.SchemaVersion != 1) return null;
        if (data.Folders == null) throw new InvalidDataException("Optional review metadata has no folder map.");
        var review = data.Folders.GetValueOrDefault(binding.SourceFolder);
        if (review != null && (review.ReviewedRevisions == null || !Enum.IsDefined(review.Filter))) throw new InvalidDataException("Optional review metadata has invalid history or filter.");
        return review;
    }
    public void Write(ExportBinding binding, FolderReview review, IEnumerable<string> protectedFolders)
        => Write(binding, review, protectedFolders, []);
    public void Write(ExportBinding binding, FolderReview review, IEnumerable<string> protectedFolders, IReadOnlyList<string> rememberedSourceIdentities)
    {
        using var background = PlatformFileSystem.BackgroundWork();
        if (!Directory.Exists(binding.ExportRoot)) return;
        using var owner = new ExportWriter(binding, protectedFolders, rememberedSourceIdentities);
        using var exportContext = PlatformFileSystem.EnterExportRoot(binding.ExportRoot);
        var path = PathFor(binding); PathGuard.ValidateNoLinks(path);
        var folders = new Dictionary<string, FolderReview>(PathGuard.Comparer);
        try
        {
            if (File.Exists(path))
            {
                using var existing = PlatformFileSystem.OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (existing.Length > 8 * 1024 * 1024) throw new InvalidDataException("Optional review metadata exceeds its size limit.");
                var old = JsonSerializer.Deserialize<ReviewMetadata>(existing);
                if (old?.SchemaVersion == 1 && old.Folders != null) foreach (var entry in old.Folders) if (entry.Value != null) folders[entry.Key] = entry.Value;
            }
        }
        catch (JsonException) { }
        folders[binding.SourceFolder] = review;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"review-{Guid.NewGuid():N}.tmp"); string? identity = null;
        try
        {
            using (var file = PlatformFileSystem.OpenOwned(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { identity = PlatformFileSystem.Identity(file.SafeFileHandle, out _); JsonSerializer.Serialize(file, new ReviewMetadata(1, folders), Options); if (file.Length > 8 * 1024 * 1024) throw new InvalidDataException("Optional review metadata exceeds its size limit."); file.Flush(true); }
            using var held = PlatformFileSystem.OpenDeleteCandidate(temporary);
            if (PlatformFileSystem.Identity(held.SafeFileHandle, out _) != identity) throw new IOException("Optional metadata staging was substituted.");
            PlatformFileSystem.ReplaceOwnedMetadata(held, temporary, path);
            Interlocked.Increment(ref successfulWrites);
        }
        finally
        {
            if (identity != null && File.Exists(temporary))
            { using var candidate = PlatformFileSystem.OpenDeleteCandidate(temporary); PlatformFileSystem.DeletePinned(candidate, temporary, identity); }
        }
    }
    public static string RevisionKey(Photo photo) => $"{photo.Name}|{photo.Revision.Length}|{photo.Revision.ModifiedTicks}|{photo.Revision.Identity}|{photo.Revision.ChangeToken}";
}
