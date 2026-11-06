using System.Collections.Concurrent;

namespace PhotoScreener.Infrastructure;
public sealed record FolderHint(string Folder, bool PhotosChanged);
// Watchers are bounded hints. Open/Refresh and overflow always reconcile through the comparer.
public sealed class FolderWatchers : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(PathGuard.Comparer);
    private readonly ConcurrentDictionary<string, bool> dirty = new(PathGuard.Comparer);
    public void Watch(string folder)
    {
        if (watchers.ContainsKey(folder) || !Directory.Exists(folder)) return;
        PathGuard.ValidateNoLinks(folder);
        if (watchers.Count >= 64) { var old = watchers.First(); old.Value.Dispose(); watchers.Remove(old.Key); }
        var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 8192 };
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (_, e) => { if (SourceCatalog.IsImage(e.OldFullPath)) dirty.AddOrUpdate(folder, true, (_, _) => true); Changed(_, e); };
        watcher.Error += (_, _) => dirty[folder] = true;
        watcher.EnableRaisingEvents = true; watchers[folder] = watcher;
        void Changed(object? sender, FileSystemEventArgs e)
        {
            var photo = SourceCatalog.IsImage(e.FullPath);
            if (photo || Path.GetFileName(e.FullPath).Equals("export", StringComparison.OrdinalIgnoreCase)) dirty.AddOrUpdate(folder, photo, (_, existing) => existing || photo);
        }
    }
    public IReadOnlyList<string> TakeDirty() => TakeHints().Select(h => h.Folder).ToArray();
    public IReadOnlyList<FolderHint> TakeHints()
    {
        var result = new List<FolderHint>();
        foreach (var key in dirty.Keys) if (dirty.TryRemove(key, out var photos)) result.Add(new(key, photos));
        return result;
    }
    public void Dispose() { foreach (var watcher in watchers.Values) watcher.Dispose(); watchers.Clear(); }
}
