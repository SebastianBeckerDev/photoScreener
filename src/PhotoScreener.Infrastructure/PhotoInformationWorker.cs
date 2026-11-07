using System.Threading.Channels;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;

public sealed record PhotoInformationRequest(Photo Photo, Action<PhotoInformation> Complete);
public sealed class PhotoInformationWorker : IAsyncDisposable
{
    private readonly Channel<PhotoInformationRequest> requests = Channel.CreateBounded<PhotoInformationRequest>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public PhotoInformationWorker() => worker = Task.Run(async () =>
    {
        var reader = new PhotoInformationReader();
        var cache = new Dictionary<(string, PhotoRevision), PhotoInformation>();
        var order = new Queue<(string, PhotoRevision)>();
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(stop.Token))
            {
                using var background = PlatformFileSystem.BackgroundWork();
                var key = (request.Photo.Path, request.Photo.Revision);
                PhotoInformation information;
                try
                {
                    if (request.Photo.Revision.ChangeToken == null || !cache.TryGetValue(key, out information!)) information = reader.Read(request.Photo, stop.Token);
                    else if (PathGuard.Revision(request.Photo.Path) != request.Photo.Revision) information = new(Notice: "Source changed; refresh the folder.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { information = new(Notice: "Photo information unavailable · " + e.Message); }
                if (request.Photo.Revision.ChangeToken != null && (information.Notice == null || information.Notice == "No embedded capture metadata") && !cache.ContainsKey(key))
                {
                    cache[key] = information; order.Enqueue(key);
                    while (order.Count > 256) cache.Remove(order.Dequeue());
                }
                request.Complete(information);
            }
        }
        catch (OperationCanceledException) { }
    });
    public void Submit(PhotoInformationRequest request) => requests.Writer.TryWrite(request);
    public async ValueTask DisposeAsync() { stop.Cancel(); requests.Writer.TryComplete(); await worker; stop.Dispose(); }
}
