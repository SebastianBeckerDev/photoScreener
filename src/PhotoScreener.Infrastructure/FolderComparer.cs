using System.Security.Cryptography;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public interface IFolderComparer
{
    ExportIndex Index(ExportBinding binding, CancellationToken token);
    Observation Compare(Photo photo, ExportIndex index, IReadOnlyList<Photo> boundSources, CancellationToken token);
}
public sealed record ExportIndex(ExportBinding Binding, IReadOnlyDictionary<string, IReadOnlyList<ExportEntry>> Candidates, string? Error = null);
public sealed record ExportEntry(string Path, string Name, PhotoRevision Revision);

public sealed class FolderComparer : IFolderComparer
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> hashes = [];
    private readonly Queue<string> hashOrder = [];
    private readonly HashSet<string> previouslyAvailable = new(PathGuard.Comparer);
    // Explicit choices resolve natural dotted-name ownership; exports remain authoritative bytes.
    public Dictionary<string, string> ExplicitOwners { get; } = new(PathGuard.Comparer);
    public Func<bool>? ForegroundPending { get; set; }
    private static string Key(string name, long length) => name.ToUpperInvariant() + "|" + length;
    public ExportIndex Index(ExportBinding binding, CancellationToken token)
    {
        var candidates = new Dictionary<string, List<ExportEntry>>();
        try
        {
            PathGuard.ValidateBinding(binding, [binding.SourceFolder]);
            if (!Directory.Exists(binding.SourceFolder)) throw new IOException("Source folder unavailable.");
            if (!Directory.Exists(binding.ExportRoot))
            {
                lock (gate) if (previouslyAvailable.Contains(binding.ExportRoot)) throw new IOException("Previously available export folder is missing; reconnect or choose a target.");
                var ancestor = Path.GetDirectoryName(binding.ExportRoot);
                while (ancestor != null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
                if (ancestor == null) throw new IOException("Export storage unavailable.");
                using var enumerator = Directory.EnumerateFileSystemEntries(ancestor).GetEnumerator(); enumerator.MoveNext();
                return new(binding, new Dictionary<string, IReadOnlyList<ExportEntry>>());
            }
            foreach (var path in Directory.EnumerateFiles(binding.ExportRoot))
            {
                token.ThrowIfCancellationRequested();
                if (!SourceCatalog.IsImage(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var revision = PathGuard.Revision(path); var name = Path.GetFileName(path);
                var entry = new ExportEntry(path, name, revision);
                Add(Key(name, revision.Length), entry);
                var stripped = LegacyExportName.StripSuffix(name);
                if (stripped != name) Add(Key(stripped, revision.Length), entry);
            }
            lock (gate) previouslyAvailable.Add(binding.ExportRoot);
            return new(binding, candidates.ToDictionary(k => k.Key, v => (IReadOnlyList<ExportEntry>)v.Value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(binding, new Dictionary<string, IReadOnlyList<ExportEntry>>(), e.Message); }
        void Add(string key, ExportEntry entry) { if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = []; list.Add(entry); }
    }
    public Observation Compare(Photo photo, ExportIndex index, IReadOnlyList<Photo> boundSources, CancellationToken token)
    {
        if (index.Error != null) return new(PickStatus.Unavailable, [], Error: index.Error);
        try
        {
            if (PathGuard.Revision(photo.Path) != photo.Revision) return new(PickStatus.Outdated, [], Error: "Source revision changed; Refresh to review its current version.");
            if (!index.Candidates.TryGetValue(Key(photo.Name, photo.Revision.Length), out var entries)) return new(PickStatus.Unpicked, []);
            var digest = Hash(photo.Path, photo.Revision, token); var copies = new List<AssociatedCopy>(); var mismatched = false;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                PathGuard.ValidateExportFile(entry.Path, index.Binding);
                if (PathGuard.Revision(entry.Path) != entry.Revision) throw new IOException("Export changed during comparison; retry.");
                using (var handle = File.OpenRead(entry.Path))
                {
                    var identity = PlatformFileSystem.Identity(handle.SafeFileHandle, out var links);
                    if (identity == photo.Revision.Identity || links > 1) throw new IOException("An export aliases another file; it cannot be associated for deletion.");
                }
                if (Hash(entry.Path, entry.Revision, token) != digest) { mismatched = true; continue; }
                string? explicitOwner; lock (gate) ExplicitOwners.TryGetValue(entry.Path, out explicitOwner);
                if (explicitOwner != null && !string.Equals(explicitOwner, photo.Name, StringComparison.Ordinal)) continue;
                if (explicitOwner == null)
                {
                    foreach (var other in boundSources)
                    {
                        if (other.Name == photo.Name || other.Revision.Length != entry.Revision.Length || !LegacyExportName.Candidate(other.Name, entry.Name)) continue;
                        if (Hash(other.Path, other.Revision, token) == digest)
                            return new(PickStatus.Ambiguous, [], digest, "Matching bytes fit different original names. Choose ownership before changing this export.");
                    }
                }
                copies.Add(new(entry.Path, entry.Revision, digest));
            }
            return new(copies.Count > 0 ? PickStatus.Picked : mismatched ? PickStatus.Outdated : PickStatus.Unpicked, copies, digest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(PickStatus.Unavailable, [], Error: e.Message); }
    }
    public string Hash(string path, PhotoRevision revision, CancellationToken token)
    {
        var key = path + "|" + revision;
        lock (gate) if (hashes.TryGetValue(key, out var cached) && PathGuard.Revision(path) == revision) return cached;
        PathGuard.ValidateNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (PathGuard.Revision(path) != revision) throw new IOException("File changed before verification.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, read);
            if (ForegroundPending?.Invoke() == true) Thread.Yield();
        }
        if (PathGuard.Revision(path) != revision) throw new IOException("File changed during verification.");
        var digest = Convert.ToHexString(sha.GetHashAndReset());
        lock (gate)
        {
            if (!hashes.ContainsKey(key)) hashOrder.Enqueue(key);
            hashes[key] = digest;
            while (hashOrder.Count > 4096) hashes.Remove(hashOrder.Dequeue());
        }
        return digest;
    }
    public void SetOwner(string exportPath, string originalName) { lock (gate) ExplicitOwners[exportPath] = originalName; }
}
