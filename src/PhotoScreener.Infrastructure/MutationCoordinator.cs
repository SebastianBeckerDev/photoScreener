using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public sealed record MutationRequest(Photo Photo, ExportBinding Binding, bool? Desired, bool UnknownParity, long Sequence, string? Sha256 = null);
public sealed record MutationResult(IReadOnlyList<Photo> Photos, Observation Observation, bool? Desired, long Sequence, string? Error = null, bool Canceled = false);

public sealed class MutationCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, MutationRequest> pending = new(PathGuard.Comparer);
    private readonly Dictionary<string, ExportWriter> writers = new(PathGuard.Comparer);
    private readonly Dictionary<string, Action> maintenance = new(PathGuard.Comparer);
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private CancellationTokenSource? activeCancellation;
    private readonly FolderComparer comparer;
    private readonly ExportStore store;
    private readonly Task worker;
    private readonly Dictionary<string, ExportBinding> sourceBindings = new(PathGuard.Comparer);
    private ExportBinding[] bindingSnapshot = [];
    private string[] sourceFolders = [];
    private bool sourceSnapshotDirty = true;
    private int writersReleasing;
    private TaskCompletionSource idle = CompletedIdle();
    private bool maintenanceActive;
    public event Action<MutationResult>? Completed;
    public event Action<string>? MaintenanceFailed;
    public event Action<Exception>? MaintenanceError;
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public long CopiedBytes => store.CopiedBytes;
    public Action<string>? Phase { get => store.Phase; set => store.Phase = value; }
    public MutationCoordinator(FolderComparer comparer)
    { this.comparer = comparer; store = new(comparer); worker = Task.Run(Loop); }
    public void Register(IReadOnlyList<(Photo Photo, ExportBinding Binding)> photos) { foreach (var entry in photos) RegisterPhoto(entry.Photo, entry.Binding); }
    public void RegisterBinding(ExportBinding binding) { lock (gate) { if (sourceBindings.GetValueOrDefault(binding.SourceFolder) == binding) return; sourceBindings[binding.SourceFolder] = binding; sourceSnapshotDirty = true; } }
    public void RegisterPhoto(Photo photo, ExportBinding binding) { comparer.ProtectSource(photo); RegisterBinding(binding); }
    public bool Submit(MutationRequest request)
    {
        lock (gate)
        {
            if (!pending.ContainsKey(request.Photo.Path) && pending.Count >= 1024) return false;
            if (pending.TryGetValue(request.Photo.Path, out var previous) && previous.Sequence > request.Sequence) return true;
            if (pending.Count == 0 && idle.Task.IsCompleted) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[request.Photo.Path] = request;
        }
        Wake(); return true;
    }
    public Task DrainAsync() { lock (gate) return idle.Task; }
    public bool RunIdle(string key, Action action)
    {
        lock (gate)
        {
            if (!maintenance.ContainsKey(key) && maintenance.Count >= 32) return false;
            if (idle.Task.IsCompleted) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            maintenance[key] = action;
        }
        Wake(); return true;
    }
    public void CancelAll()
    {
        lock (gate) { pending.Clear(); maintenance.Clear(); activeCancellation?.Cancel(); if (activeCancellation == null && !maintenanceActive && writersReleasing == 0) idle.TrySetResult(); }
        Wake();
    }
    private void Wake() { if (signal.CurrentCount == 0) try { signal.Release(); } catch (SemaphoreFullException) { } }
    private async Task Loop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await signal.WaitAsync(stop.Token);
                MutationRequest? request; ExportBinding[] knownBindings;
                lock (gate)
                {
                    request = pending.Values.MinBy(r => r.Sequence);
                    if (sourceSnapshotDirty)
                    {
                        bindingSnapshot = sourceBindings.Values.ToArray();
                        sourceFolders = sourceBindings.Keys.ToArray();
                        sourceSnapshotDirty = false;
                    }
                    knownBindings = bindingSnapshot;
                    if (request != null) activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                }
                if (request == null)
                {
                    Action? action = null;
                    lock (gate) if (maintenance.FirstOrDefault() is { Key: not null } entry) { action = entry.Value; maintenance.Remove(entry.Key); maintenanceActive = true; }
                    ReleaseIdleWriters(false);
                    try { action?.Invoke(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { MaintenanceError?.Invoke(e); MaintenanceFailed?.Invoke(e.Message); }
                    finally { lock (gate) maintenanceActive = false; }
                    ReleaseIdleWriters();
                    lock (gate) if (pending.Count > 0 || maintenance.Count > 0) Wake();
                    continue;
                }
                var token = activeCancellation!.Token;
                var affected = new List<Photo> { request.Photo }; Observation observation = new(PickStatus.Checking, []);
                bool? desired = request.Desired; var sequence = request.Sequence; string? error = null; var canceled = false;
                try
                {
                    PathGuard.ValidateBinding(request.Binding, sourceFolders);
                    // Files are authoritative even if a watcher/Refresh has not run.
                    var index = comparer.Index(request.Binding, token);
                    var sameTarget = AssociationSources.Read(request.Photo, index, knownBindings, token);
                    observation = comparer.Compare(request.Photo, index, sameTarget, token);
                    if (!observation.Picked.HasValue) throw new IOException(observation.Error ?? "Association is not yet safe to change.");
                    var sameName = sameTarget.Where(p => p.Name == request.Photo.Name).ToArray();
                    var digest = observation.Sha256 ?? request.Sha256;
                    if (sameName.Length > 1) digest ??= comparer.Hash(request.Photo.Path, request.Photo.Revision, token);
                    if (digest != null)
                    {
                        foreach (var other in sameName)
                        {
                            if (other.Path == request.Photo.Path) continue;
                            if (comparer.Hash(other.Path, other.Revision, token) != digest) continue;
                            affected.Add(other);
                        }
                    }
                    MutationRequest latest;
                    lock (gate) latest = Latest(affected) ?? request;
                    sequence = latest.Sequence; desired = latest.Desired ?? (observation.Picked.Value ^ latest.UnknownParity);
                    if (desired != observation.Picked)
                    {
                        if (!writers.ContainsKey(request.Binding.ExportRoot))
                        {
                            writers[request.Binding.ExportRoot] = new(request.Binding, sourceFolders, comparer.ProtectedDirectoryIdentities);
                            store.CleanupOwnedStaging(request.Binding);
                        }
                        if (desired == true)
                        {
                            var copy = store.Pick(request.Photo, request.Binding, () =>
                            {
                                lock (gate)
                                {
                                    var intent = Latest(affected);
                                    return intent != null && (intent.Desired ?? (observation.Picked.Value ^ intent.UnknownParity));
                                }
                            }, token);
                            observation = new(PickStatus.Picked, observation.Copies.Append(copy).DistinctBy(c => c.Path, PathGuard.Comparer).ToArray(), copy.Sha256);
                        }
                        else
                        {
                            // Recheck name ownership immediately before destructive work.
                            observation = comparer.Compare(request.Photo, index, AssociationSources.Read(request.Photo, index, knownBindings, token), token);
                            if (observation.Picked != true) throw new IOException(observation.Error ?? "Export association changed before removal; retry.");
                            store.Remove(request.Photo, request.Binding, observation.Copies, token);
                            var fresh = comparer.Index(request.Binding, token);
                            observation = comparer.Compare(request.Photo, fresh, AssociationSources.Read(request.Photo, fresh, knownBindings, token), token);
                            if (observation.Picked != false) throw new IOException(observation.Error ?? "Export copies changed during removal; retry to remove the remaining current copies.");
                        }
                    }
                }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { error = e.Message; }
                if (error != null || canceled)
                {
                    // Failures can occur after some duplicates were deleted or a final file was published.
                    var fresh = comparer.Index(request.Binding, CancellationToken.None);
                    try { observation = comparer.Compare(request.Photo, fresh, AssociationSources.Read(request.Photo, fresh, knownBindings, CancellationToken.None), CancellationToken.None); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { observation = new(PickStatus.Unavailable, [], Error: e.Message); }
                }
                lock (gate)
                {
                    foreach (var photo in affected)
                        if (pending.TryGetValue(photo.Path, out var intent) && intent.Sequence <= sequence) pending.Remove(photo.Path);
                    activeCancellation.Dispose(); activeCancellation = null;
                }
                Completed?.Invoke(new(affected, observation, desired, sequence, error, canceled));
                ReleaseIdleWriters();
                lock (gate) if (pending.Count > 0 || maintenance.Count > 0) Wake();
            }
        }
        catch (OperationCanceledException) { }
        finally { foreach (var writer in writers.Values) writer.Dispose(); writers.Clear(); lock (gate) idle.TrySetResult(); }
    }
    private MutationRequest? Latest(IReadOnlyList<Photo> photos) => photos.Select(p => pending.GetValueOrDefault(p.Path) is { } r && r.Photo.Revision == p.Revision ? r : null).Where(r => r != null).MaxBy(r => r!.Sequence);
    private void ReleaseIdleWriters(bool acknowledgeIdle = true)
    {
        List<ExportWriter> released = [];
        lock (gate)
        {
            foreach (var root in writers.Keys.Where(root => !pending.Values.Any(r => PathGuard.Comparer.Equals(r.Binding.ExportRoot, root))).ToArray()) { released.Add(writers[root]); writers.Remove(root); }
            writersReleasing += released.Count;
        }
        foreach (var writer in released)
        {
            try { writer.Dispose(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { MaintenanceError?.Invoke(e); MaintenanceFailed?.Invoke(e.Message); }
            finally { lock (gate) writersReleasing--; }
        }
        lock (gate) if (acknowledgeIdle && pending.Count == 0 && maintenance.Count == 0 && activeCancellation == null && !maintenanceActive && writersReleasing == 0) idle.TrySetResult();
    }
    private static TaskCompletionSource CompletedIdle() { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion; }
    public async ValueTask DisposeAsync() { stop.Cancel(); CancelAll(); await worker; stop.Dispose(); signal.Dispose(); }
}
