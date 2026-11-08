using System.Security.Cryptography;
using System.Text.Json;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public interface IExportStore
{
    AssociatedCopy Pick(Photo photo, ExportBinding binding, Func<bool> stillWanted, CancellationToken token);
    void Remove(Photo photo, ExportBinding binding, IReadOnlyList<AssociatedCopy> copies, CancellationToken token);
}
public sealed class ExportWriter : IDisposable
{
    private readonly IDisposable pinned;
    private readonly FileStream ownership;
    private readonly IDisposable exportAnchor;
    private int disposed;
    public ExportBinding Binding { get; }
    public ExportWriter(ExportBinding binding, IEnumerable<string> protectedFolders, IReadOnlyList<string>? rememberedSourceIdentities = null, IEnumerable<ExportBinding>? knownBindings = null)
    {
        var sources = protectedFolders.ToArray();
        Binding = binding; PathGuard.ValidateBinding(binding, sources);
        using var createdRoot = PlatformFileSystem.CreateDirectoryPinned(binding.ExportRoot);
        if (knownBindings != null) BindingIdentityGuard.Validate(binding, knownBindings);
        exportAnchor = PlatformFileSystem.AnchorExportRoot(binding.ExportRoot, sources.Append(binding.SourceFolder), rememberedSourceIdentities);
        try
        {
            using var context = PlatformFileSystem.EnterExportRoot(binding.ExportRoot);
            var metadata = Path.Combine(binding.ExportRoot, ".photoscreener");
            using var createdMetadata = PlatformFileSystem.CreateDirectoryPinned(metadata);
            pinned = PlatformFileSystem.PinDirectories(binding.SourceFolder, binding.ExportRoot, metadata);
            try
            {
                var path = Path.Combine(metadata, "writer.lock"); PathGuard.ValidateNoLinks(path);
                ownership = PlatformFileSystem.OpenOwned(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                PlatformFileSystem.ClaimOwnership(ownership);
            }
            catch { ownership?.Dispose(); pinned.Dispose(); throw; }
        }
        catch { exportAnchor.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { PlatformFileSystem.ReleaseOwnership(ownership); }
        finally { try { ownership.Dispose(); } finally { try { pinned.Dispose(); } finally { exportAnchor.Dispose(); } } }
    }
}
public sealed class ExportStore(FolderComparer comparer) : IExportStore
{
    private readonly SourceCatalog source = new();
    public Action<string>? Phase { get; set; }
    public long CopiedBytes { get; private set; }
    public AssociatedCopy Pick(Photo photo, ExportBinding binding, Func<bool> stillWanted, CancellationToken token)
    {
        using var background = PlatformFileSystem.BackgroundWork();
        using var exportContext = PlatformFileSystem.EnterExportRoot(binding.ExportRoot);
        PathGuard.ValidateBinding(binding, [photo.Folder]);
        var staging = Path.Combine(binding.ExportRoot, ".photoscreener", "staging");
        using var createdStaging = PlatformFileSystem.CreateDirectoryPinned(staging);
        using var pin = PlatformFileSystem.PinDirectories(photo.Folder, binding.ExportRoot, staging);
        var job = Guid.NewGuid().ToString("N");
        var part = Path.Combine(staging, $"ps-v1-{job}.part"); var manifest = Path.Combine(staging, $"ps-v1-{job}.json");
        string? partIdentity = null, manifestIdentity = null;
        try
        {
            byte[] md5Digest; string shaDigest;
            using (var input = source.Open(photo))
            using (var output = PlatformFileSystem.OpenOwned(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                partIdentity = PlatformFileSystem.Identity(output.SafeFileHandle, out _);
                using (var owned = PlatformFileSystem.OpenOwned(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    manifestIdentity = PlatformFileSystem.Identity(owned.SafeFileHandle, out _);
                    JsonSerializer.Serialize(owned, new StageManifest("PhotoScreener/staging/v1", job, partIdentity));
                    owned.Flush(true);
                }
                Phase?.Invoke("staging");
                var buffer = new byte[1024 * 1024]; int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (!stillWanted()) throw new OperationCanceledException("Pick was superseded.");
                    output.Write(buffer, 0, read); md5.AppendData(buffer, 0, read); sha.AppendData(buffer, 0, read);
                    CopiedBytes += read;
                    if (comparer.ForegroundPending?.Invoke() == true) Thread.Yield();
                }
                output.Flush(true);
                if (PathGuard.Revision(photo.Path) != photo.Revision) throw new IOException("Source changed while copying.");
                md5Digest = md5.GetHashAndReset(); shaDigest = Convert.ToHexString(sha.GetHashAndReset());
            }
            Phase?.Invoke("verify");
            using var staged = PlatformFileSystem.OpenDeleteCandidate(part);
            if (PlatformFileSystem.Identity(staged.SafeFileHandle, out _) != partIdentity || staged.Length != photo.Revision.Length || HashHandle(staged, token) != shaDigest)
                throw new IOException("Staged copy failed content/identity verification.");
            token.ThrowIfCancellationRequested();
            if (!stillWanted()) throw new OperationCanceledException("Pick was superseded before publication.");
            if (PathGuard.Revision(photo.Path) != photo.Revision) throw new IOException("Source changed before publication; refresh to review its current version.");
            var final = Path.Combine(binding.ExportRoot, LegacyExportName.FromDigest(photo.Name, md5Digest));
            PathGuard.ValidateNoLinks(final);
            if (File.Exists(final)) VerifyExisting(final);
            else
            {
                try { PlatformFileSystem.PublishPinned(staged, part, final); }
                catch (IOException) when (File.Exists(final)) { VerifyExisting(final); }
            }
            Phase?.Invoke("published");
            return new(final, PathGuard.Revision(final), shaDigest);
            void VerifyExisting(string finalPath)
            {
                PathGuard.ValidateExportFile(finalPath, binding);
                using var existing = PlatformFileSystem.OpenReadCandidate(finalPath);
                var identity = PlatformFileSystem.Identity(existing.SafeFileHandle, out _);
                if (identity == photo.Revision.Identity || HashHandle(existing, token) != shaDigest) throw new IOException("Legacy filename collision: existing content differs or aliases the source. No file was overwritten.");
                if (PathGuard.Revision(finalPath).Identity != identity) throw new IOException("Existing destination changed during verification.");
            }
        }
        finally
        {
            if (partIdentity != null && File.Exists(part)) DeleteOwned(part, partIdentity);
            if (manifestIdentity != null && File.Exists(manifest)) DeleteOwned(manifest, manifestIdentity);
        }
    }
    public void Remove(Photo photo, ExportBinding binding, IReadOnlyList<AssociatedCopy> copies, CancellationToken token)
    {
        using var background = PlatformFileSystem.BackgroundWork();
        using var exportContext = PlatformFileSystem.EnterExportRoot(binding.ExportRoot);
        using var pin = PlatformFileSystem.PinDirectories(photo.Folder, binding.ExportRoot);
        using var protectedSource = source.Open(photo);
        foreach (var copy in copies)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(copy.Path)) continue;
            PathGuard.ValidateExportFile(copy.Path, binding);
            using var target = PlatformFileSystem.OpenDeleteCandidate(copy.Path);
            var identity = PlatformFileSystem.Identity(target.SafeFileHandle, out _);
            if (identity != copy.Revision.Identity || identity == photo.Revision.Identity || PathGuard.Revision(copy.Path) != copy.Revision || HashHandle(target, token) != copy.Sha256)
                throw new IOException("Export association was substituted or changed; it was preserved. Refresh before retrying.");
            Phase?.Invoke("deleting"); PlatformFileSystem.DeletePinned(target, copy.Path, identity);
        }
        Phase?.Invoke("deleted");
    }
    public void CleanupOwnedStaging(ExportBinding binding)
    {
        using var exportContext = PlatformFileSystem.EnterExportRoot(binding.ExportRoot);
        var staging = Path.Combine(binding.ExportRoot, ".photoscreener", "staging");
        if (!Directory.Exists(staging)) return;
        PathGuard.ValidateNoLinks(staging); using var pin = PlatformFileSystem.PinDirectories(staging);
        foreach (var manifest in Directory.EnumerateFiles(staging, "ps-v1-*.json"))
        {
            try
            {
                PathGuard.ValidateNoLinks(manifest);
                using var manifestRead = PlatformFileSystem.OpenReadCandidate(manifest);
                if (manifestRead.Length > 4096) continue;
                var manifestIdentity = PlatformFileSystem.Identity(manifestRead.SafeFileHandle, out _);
                var record = JsonSerializer.Deserialize<StageManifest>(manifestRead);
                manifestRead.Dispose();
                if (record == null || record.Kind != "PhotoScreener/staging/v1" || !Guid.TryParseExact(record.Job, "N", out _) || Path.GetFileName(manifest) != $"ps-v1-{record.Job}.json") continue;
                var part = Path.Combine(staging, $"ps-v1-{record.Job}.part");
                if (File.Exists(part)) DeleteOwned(part, record.Identity);
                DeleteOwned(manifest, manifestIdentity);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception) { }
        }
    }
    private static void DeleteOwned(string path, string identity)
    {
        using var stream = PlatformFileSystem.OpenDeleteCandidate(path);
        PlatformFileSystem.DeletePinned(stream, path, identity);
    }
    private static string HashHandle(FileStream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read;
        while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed record StageManifest(string Kind, string Job, string Identity);
}
