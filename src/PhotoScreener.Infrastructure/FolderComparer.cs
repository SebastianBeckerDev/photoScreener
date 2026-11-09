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
    private readonly HashSet<string> protectedDirectoryIdentities = [];
    private string[] protectedDirectorySnapshot = [];
    public IReadOnlyList<string> ProtectedDirectoryIdentities { get { lock (gate) return protectedDirectorySnapshot; } }
    public void ProtectSource(Photo photo)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var identity in photo.SourceDirectoryIdentities) changed |= protectedDirectoryIdentities.Add(identity);
            if (changed) protectedDirectorySnapshot = protectedDirectoryIdentities.ToArray();
        }
    }
    // Explicit choices resolve natural dotted-name ownership; exports remain authoritative bytes.
    public Dictionary<string, string> ExplicitOwners { get; } = new(PathGuard.Comparer);
    public Func<bool>? ForegroundPending { get; set; }
    private long hashReadBytes;
    public long HashReadBytes => Interlocked.Read(ref hashReadBytes);
    private long fullIndexScans, associationIndexScans, associationRevisionReads;
    public long FullIndexScans => Interlocked.Read(ref fullIndexScans);
    public long AssociationIndexScans => Interlocked.Read(ref associationIndexScans);
    public long AssociationRevisionReads => Interlocked.Read(ref associationRevisionReads);
    internal static string Key(string name, long length) => PlatformFileSystem.AssociationName(name).ToUpperInvariant() + "|" + length;
    public ExportIndex Index(ExportBinding binding, CancellationToken token)
    { Interlocked.Increment(ref fullIndexScans); return BuildIndex(binding, token, null); }
    public ExportIndex IndexAssociation(Photo photo, ExportBinding binding, CancellationToken token)
    { Interlocked.Increment(ref associationIndexScans); return BuildIndex(binding, token, photo.Name); }
    private ExportIndex BuildIndex(ExportBinding binding, CancellationToken token, string? originalName)
    {
        var candidates = new Dictionary<string, List<ExportEntry>>();
        try
        {
            PathGuard.ValidateBinding(binding, [binding.SourceFolder]);
            if (Directory.Exists(binding.ExportRoot))
            {
                var directoryIdentity = PlatformFileSystem.DirectoryIdentity(binding.ExportRoot);
                lock (gate) if (protectedDirectoryIdentities.Contains(directoryIdentity)) throw new IOException("Export root aliases a known source directory or ancestor, including a moved source.");
            }
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
            var names = new HashSet<string>(PathGuard.Comparer);
            // Limit revision reads to this original's bare/legacy-suffixed candidates.
            // A literal wildcard in a Mac filename requires a broader name query;
            // Candidate still checks the complete literal filename before opening it.
            var prefix = originalName == null ? "" : Path.GetFileNameWithoutExtension(originalName) + ".";
            var wildcard = prefix.IndexOfAny(['*', '?']);
            if (wildcard >= 0) prefix = prefix[..wildcard];
            // Managed wildcard matching does not apply APFS Unicode equivalence.
            // Query the literal ASCII prefix and check complete names below.
            if (OperatingSystem.IsMacOS())
            {
                prefix = PlatformFileSystem.AssociationName(prefix);
                var nonAscii = prefix.TakeWhile(c => c <= 127).Count();
                prefix = prefix[..nonAscii];
            }
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var path in Directory.EnumerateFiles(binding.ExportRoot, prefix + "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (originalName != null && SourceCatalog.IsNef(originalName) ? !SourceCatalog.IsNef(path) : !SourceCatalog.IsImage(path)) continue;
                var name = Path.GetFileName(path);
                if (originalName != null && !LegacyExportName.Candidate(originalName, name)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var revision = PathGuard.Revision(path);
                if (originalName != null) Interlocked.Increment(ref associationRevisionReads);
                if (!names.Add(name)) throw new IOException("Export image names differ only by case and cannot be safely distinguished in this session: " + name);
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
            var currentRevision = PathGuard.Revision(photo.Path);
            index.Candidates.TryGetValue(Key(photo.Name, photo.Revision.Length), out var entries);
            if (entries?.Any(entry => entry.Revision.Identity == currentRevision.Identity) == true)
                throw new IOException("An export aliases its source; it cannot be associated for deletion.");
            if (currentRevision != photo.Revision) return new(PickStatus.Outdated, [], Error: "Source revision changed; Refresh to review its current version.");
            if (entries == null) return new(PickStatus.Unpicked, []);
            var digest = Hash(photo.Path, photo.Revision, token); var copies = new List<AssociatedCopy>(); var mismatched = false;
            var caseSensitive = PlatformFileSystem.CaseSensitiveNames(photo.Folder);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!LegacyExportName.Candidate(photo.Name, entry.Name, caseSensitive)) continue;
                PathGuard.ValidateExportFile(entry.Path, index.Binding);
                if (PathGuard.Revision(entry.Path) != entry.Revision) throw new IOException("Export changed during comparison; retry.");
                using (var handle = PlatformFileSystem.OpenReadCandidate(entry.Path))
                {
                    var identity = PlatformFileSystem.Identity(handle.SafeFileHandle, out var links);
                    if (identity == photo.Revision.Identity || links > 1) throw new IOException("An export aliases another file; it cannot be associated for deletion.");
                }
                if (Hash(entry.Path, entry.Revision, token) != digest) { mismatched = true; continue; }
                string? explicitOwner; lock (gate) ExplicitOwners.TryGetValue(entry.Path, out explicitOwner);
                if (explicitOwner != null && PlatformFileSystem.AssociationName(explicitOwner) != PlatformFileSystem.AssociationName(photo.Name)) continue;
                if (explicitOwner == null)
                {
                    foreach (var other in boundSources)
                    {
                        if (PlatformFileSystem.AssociationName(other.Name) == PlatformFileSystem.AssociationName(photo.Name) || other.Revision.Length != entry.Revision.Length || !LegacyExportName.Candidate(other.Name, entry.Name, PlatformFileSystem.CaseSensitiveNames(other.Folder))) continue;
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
        using var background = PlatformFileSystem.BackgroundWork();
        var key = path + "|" + revision;
        string? cached; lock (gate) hashes.TryGetValue(key, out cached);
        if (revision.ChangeToken != null && cached != null && PathGuard.Revision(path) == revision) return cached;
        PathGuard.ValidateNoLinks(path);
        using var stream = PlatformFileSystem.OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        if (PlatformFileSystem.Identity(stream.SafeFileHandle, out _) != revision.Identity || PathGuard.Revision(path) != revision) throw new IOException("File changed before verification.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, read);
            Interlocked.Add(ref hashReadBytes, read);
            if (ForegroundPending?.Invoke() == true) Thread.Yield();
        }
        if (PathGuard.Revision(path) != revision) throw new IOException("File changed during verification.");
        var digest = Convert.ToHexString(sha.GetHashAndReset());
        lock (gate)
        {
            if (revision.ChangeToken != null)
            {
                if (!hashes.ContainsKey(key)) hashOrder.Enqueue(key);
                hashes[key] = digest;
            }
            while (hashOrder.Count > 4096) hashes.Remove(hashOrder.Dequeue());
        }
        return digest;
    }
    public void SetOwner(string exportPath, string originalName) { lock (gate) ExplicitOwners[exportPath] = originalName; }
}
