using System.Threading.Channels;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public sealed record ComparisonWork(IReadOnlyList<(Photo Photo, ExportBinding Binding)> Rows, IReadOnlyList<(Photo Photo, ExportBinding Binding)> BoundSources, Action<Photo, Observation> Complete, CancellationToken ContextToken);
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
                    foreach (var (photo, binding) in work.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!indexes.TryGetValue(binding.ExportRoot, out var index)) indexes[binding.ExportRoot] = index = comparer.Index(binding, token);
                        var bound = work.BoundSources.Where(p => PathGuard.Comparer.Equals(p.Binding.ExportRoot, binding.ExportRoot)).Select(p => p.Photo).ToArray();
                        work.Complete(photo, comparer.Compare(photo, index, bound, token));
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
    });
    public void Submit(ComparisonWork work) => queue.Writer.TryWrite(work);
    public async ValueTask DisposeAsync() { stop.Cancel(); queue.Writer.TryComplete(); await worker; stop.Dispose(); }
}
