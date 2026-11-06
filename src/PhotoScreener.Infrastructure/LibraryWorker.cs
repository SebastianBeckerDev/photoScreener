using System.Threading.Channels;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public sealed record LibraryRow(Photo Photo, ExportBinding Binding, Observation Observation);
public sealed record LibraryBatch(string Folder, IReadOnlyList<LibraryRow> Rows, bool FolderDone = false, bool Complete = false, string? Error = null);
public sealed record LibraryWork(string Root, bool ComparePicks, Func<LibraryBatch, Task> Publish, CancellationToken ContextToken, IReadOnlyList<(Photo Photo, ExportBinding Binding)> BoundSources);
public sealed class LibraryWorker : IAsyncDisposable
{
    private readonly Channel<LibraryWork> queue = Channel.CreateBounded<LibraryWork>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public LibraryWorker(ExportBindingResolver resolver, FolderComparer comparer) => worker = Task.Run(async () =>
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stop.Token))
            {
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, work.ContextToken); var token = canceled.Token;
                var stack = new Stack<string>(); stack.Push(work.Root);
                try
                {
                    while (stack.TryPop(out var folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if (resolver.Excluded(folder)) continue;
                        try
                        {
                            PathGuard.ValidateNoLinks(folder);
                            await work.Publish(new(folder, []));
                            if (work.ComparePicks)
                            {
                                var photos = new SourceCatalog().Enumerate(folder, token).ToArray();
                                foreach (var photo in photos) comparer.ProtectSource(photo);
                                var binding = resolver.Resolve(folder); var index = comparer.Index(binding, token);
                                var bound = photos.Concat(work.BoundSources.Where(r => PathGuard.Comparer.Equals(r.Binding.ExportRoot, binding.ExportRoot)).Select(r => r.Photo)).DistinctBy(p => p.Path, PathGuard.Comparer).ToArray();
                                var batch = new List<LibraryRow>(32);
                                foreach (var photo in photos)
                                {
                                    token.ThrowIfCancellationRequested(); batch.Add(new(photo, binding, comparer.Compare(photo, index, bound, token)));
                                    if (batch.Count == 32) { await work.Publish(new(folder, batch.ToArray())); batch.Clear(); }
                                }
                                if (batch.Count > 0) await work.Publish(new(folder, batch.ToArray()));
                                await work.Publish(new(folder, [], FolderDone: true, Error: index.Error));
                            }
                            foreach (var child in Directory.EnumerateDirectories(folder))
                                if (!resolver.Excluded(child) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) stack.Push(child);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                        { await work.Publish(new(folder, [], FolderDone: true, Error: e.Message)); }
                    }
                    await work.Publish(new(work.Root, [], Complete: true));
                }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
    });
    public void Submit(LibraryWork work) => queue.Writer.TryWrite(work);
    public async ValueTask DisposeAsync() { stop.Cancel(); queue.Writer.TryComplete(); await worker; stop.Dispose(); }
}
