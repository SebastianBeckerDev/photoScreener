using System.Threading.Channels;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public sealed record ComparisonWork(IReadOnlyList<(Photo Photo, ExportBinding Binding)> Rows, IReadOnlyList<(Photo Photo, ExportBinding Binding)> BoundSources, Func<IReadOnlyList<(Photo Photo, Observation Observation)>, Task> Publish, CancellationToken ContextToken);
public sealed class ComparisonWorker : IAsyncDisposable
{
    private readonly Channel<ComparisonWork> queue = Channel.CreateBounded<ComparisonWork>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public ComparisonWorker(FolderComparer comparer) => worker = Task.Run(async () =>
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stop.Token))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, work.ContextToken);
                var token = cancellation.Token;
                try
                {
                    var indexes = new Dictionary<string, ExportIndex>(PathGuard.Comparer);
                    foreach (var (photo, _) in work.BoundSources) comparer.ProtectSource(photo);
                    var boundByRoot = work.BoundSources.GroupBy(p => p.Binding.ExportRoot, PathGuard.Comparer).ToDictionary(g => g.Key, g => (IReadOnlyList<Photo>)g.Select(p => p.Photo).ToArray(), PathGuard.Comparer);
                    var batch = new List<(Photo, Observation)>(32);
                    foreach (var (photo, binding) in work.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!indexes.TryGetValue(binding.ExportRoot, out var index)) indexes[binding.ExportRoot] = index = comparer.Index(binding, token);
                        var bound = boundByRoot.GetValueOrDefault(binding.ExportRoot) ?? [];
                        batch.Add((photo, comparer.Compare(photo, index, bound, token)));
                        if (batch.Count == 32) { await work.Publish(batch.ToArray()); batch.Clear(); }
                    }
                    if (batch.Count > 0) await work.Publish(batch.ToArray());
                    token.ThrowIfCancellationRequested();
                    await work.Publish([]); // An empty batch marks completion, including empty folders.
                }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
    });
    public void Submit(ComparisonWork work) => queue.Writer.TryWrite(work);
    public async ValueTask DisposeAsync() { stop.Cancel(); queue.Writer.TryComplete(); await worker; stop.Dispose(); }
}
