using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

if (args.Length < 3) throw new ArgumentException("source-folder, export-root, pause-phase required");
var sourceFolder = Path.GetFullPath(args[0]); var binding = new ExportBinding(sourceFolder, Path.GetFullPath(args[1]));
var photo = new SourceCatalog().Enumerate(sourceFolder, default).First();
var comparer = new FolderComparer(); var store = new ExportStore(comparer);
using var writer = new ExportWriter(binding, [sourceFolder]);
store.Phase = phase =>
{
    if (phase != args[2]) return;
    File.WriteAllText(Path.Combine(binding.ExportRoot, ".photoscreener", "fault-reached.txt"), phase);
    Thread.Sleep(Timeout.Infinite);
};
if (args[2] == "deleted" || args[2] == "deleting")
    store.Remove(photo, binding, comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Copies, default);
else store.Pick(photo, binding, () => true, default);
