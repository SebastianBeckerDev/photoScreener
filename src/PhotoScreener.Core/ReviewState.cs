namespace PhotoScreener.Core;

public enum PickStatus { Checking, Picked, Unpicked, Copying, Removing, Error, Outdated, Unavailable, Ambiguous }
public enum ReviewFilter { All, Picked, Unpicked }
public sealed record PhotoRevision(long Length, long ModifiedTicks, string Identity)
{
    public string? ChangeToken { get; init; }
}
public sealed record Photo(string Path, string Folder, string Name, PhotoRevision Revision)
{
    public IReadOnlyList<string> SourceDirectoryIdentities { get; init; } = [];
}
public sealed record ExportBinding(string SourceFolder, string ExportRoot);
public sealed record AssociatedCopy(string Path, PhotoRevision Revision, string Sha256);
public sealed record Observation(PickStatus Status, IReadOnlyList<AssociatedCopy> Copies, string? Sha256 = null, string? Error = null)
{
    public bool? Picked => Status is PickStatus.Picked ? true : Status is PickStatus.Unpicked or PickStatus.Outdated ? false : null;
}

// UI-owned requested state. An unknown observation keeps parity, rather than guessing a deletion.
public sealed class PickIntent
{
    public bool? Observed { get; private set; }
    public bool? Requested { get; private set; }
    public long Sequence { get; private set; }
    public bool UnknownParity { get; private set; }
    public bool Pending => UnknownParity || (Requested.HasValue && Requested != Observed);
    public void Toggle(long sequence)
    {
        Sequence = sequence;
        if (Requested.HasValue) Requested = !Requested.Value;
        else UnknownParity = !UnknownParity;
    }
    public void Observe(bool? picked)
    {
        // Unavailable storage retains the last confirmed observation; it does not establish empty.
        if (!picked.HasValue) return;
        var wasPending = Pending;
        Observed = picked;
        if (!Requested.HasValue && picked.HasValue)
        {
            Requested = picked.Value ^ UnknownParity;
            UnknownParity = false;
        }
        else if (!wasPending) Requested = picked;
    }
    public void AdoptRequest(bool requested, long sequence)
    {
        if (sequence < Sequence) return;
        Requested = requested; Sequence = sequence; UnknownParity = false;
    }
    public void Complete(bool observed, long sequence)
    {
        Observed = observed;
        if (sequence == Sequence) Requested = observed;
    }
    public void Cancel() { Requested = Observed; UnknownParity = false; }
}

public static class ReviewRules
{
    public static bool Matches(ReviewFilter filter, bool? picked) => filter == ReviewFilter.All ||
        (filter == ReviewFilter.Picked ? picked == true : picked == false);
    public static int ChooseAfterFilter(int current, int count, Func<int, bool> matches)
    {
        for (var i = Math.Max(0, current); i < count; i++) if (matches(i)) return i;
        for (var i = Math.Min(current - 1, count - 1); i >= 0; i--) if (matches(i)) return i;
        return -1;
    }
}
