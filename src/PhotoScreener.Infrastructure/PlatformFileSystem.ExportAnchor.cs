using Microsoft.Win32.SafeHandles;

namespace PhotoScreener.Infrastructure;
public static partial class PlatformFileSystem
{
    private static readonly object exportAnchorGate = new();
    private static readonly Dictionary<string, MacExportAnchor> exportAnchors = new(PathGuard.Comparer);
    [ThreadStatic] private static MacExportAnchor? activeExport;
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private static readonly IDisposable emptyLease = new EmptyLease();

    // A writer retains a physical export directory. macOS operations resolve children relative
    // to that descriptor, so replacing an ancestor cannot redirect a writable path into sources.
    public static IDisposable AnchorExportRoot(string root, IEnumerable<string> sources, IReadOnlyList<string>? rememberedSourceIdentities = null)
    {
        var forbidden = new HashSet<string>(rememberedSourceIdentities ?? []);
        foreach (var folder in sources.Distinct(PathGuard.Comparer))
            for (var ancestor = PathGuard.Canonical(folder); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor)) forbidden.Add(DirectoryIdentity(ancestor));
        if (!OperatingSystem.IsMacOS())
        {
            if (forbidden.Contains(DirectoryIdentity(root))) throw new IOException("Export aliases a known source directory or ancestor.");
            return emptyLease;
        }
        var canonical = PathGuard.Canonical(root);
        var directory = OpenDirectoryAbsolute(canonical);
        try
        {
            var identity = Identity(directory, out _);
            if (forbidden.Contains(identity)) throw new IOException("Export aliases a known source directory or ancestor.");
            var anchor = new MacExportAnchor(canonical, directory, identity, forbidden);
            lock (exportAnchorGate)
            {
                if (exportAnchors.ContainsKey(canonical)) throw new IOException("Another writer owns this export root.");
                exportAnchors.Add(canonical, anchor);
            }
            return anchor;
        }
        catch { directory.Dispose(); throw; }
    }
    // Scopes never span await: copy/remove/metadata operations execute on the serialized worker.
    public static IDisposable EnterExportRoot(string root)
    {
        if (!OperatingSystem.IsMacOS()) return emptyLease;
        MacExportAnchor anchor;
        lock (exportAnchorGate)
            if (!exportAnchors.TryGetValue(PathGuard.Canonical(root), out anchor!)) throw new IOException("Export mutation requires an owned directory descriptor.");
        anchor.CheckAlive();
        var previous = activeExport; activeExport = anchor;
        return new ExportScope(previous);
    }
    private sealed class ExportScope(MacExportAnchor? previous) : IDisposable
    { public void Dispose() => activeExport = previous; }
    private sealed class MacExportAnchor(string root, SafeFileHandle directory, string identity, HashSet<string> forbidden) : IDisposable
    {
        public string Root => root;
        public void CheckAlive()
        {
            using var current = OpenDirectoryAbsolute(root);
            if (Identity(current, out _) != identity) throw new IOException("Export root was moved or substituted; mutation stopped.");
        }
        private void ValidateDirectory(SafeFileHandle candidate)
        {
            if (forbidden.Contains(Identity(candidate, out _))) throw new IOException("Export metadata/staging aliases a protected source directory.");
        }
        public SafeFileHandle OpenChildDirectory(string path, bool create)
        {
            CheckAlive();
            var descriptor = OpenAt(directory.DangerousGetHandle().ToInt32(), ".", 0x100100, 0);
            if (descriptor < 0) throw new IOException("Owned export directory unavailable.");
            var current = new SafeFileHandle((IntPtr)descriptor, true);
            try
            {
                var relative = Path.GetRelativePath(root, PathGuard.Canonical(path));
                foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (component == ".") continue;
                    if (component == "..") throw new IOException("Directory escapes its captured export root.");
                    ValidateDirectory(current);
                    var next = OpenAt(current.DangerousGetHandle().ToInt32(), component, 0x100100, 0);
                    if (next < 0 && create && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 2)
                    {
                        if (MakeDirectoryAt(current.DangerousGetHandle().ToInt32(), component, 448) != 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 17) throw new IOException("Cannot create owned export directory.");
                        next = OpenAt(current.DangerousGetHandle().ToInt32(), component, 0x100100, 0);
                    }
                    if (next < 0) throw new IOException("Export directory unavailable or replaced with a link.");
                    current.Dispose(); current = new SafeFileHandle((IntPtr)next, true);
                }
                ValidateDirectory(current); return current;
            }
            catch { current.Dispose(); throw; }
        }
        public void Dispose()
        {
            lock (exportAnchorGate)
                if (exportAnchors.GetValueOrDefault(root) == this) exportAnchors.Remove(root);
            directory.Dispose();
        }
    }
}
