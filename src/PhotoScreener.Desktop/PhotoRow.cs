using CommunityToolkit.Mvvm.ComponentModel;
using PhotoScreener.Core;

namespace PhotoScreener.Desktop;
public sealed class PhotoRow(Photo photo, ExportBinding binding) : ObservableObject
{
    public Photo Photo { get; } = photo;
    public ExportBinding Binding { get; } = binding;
    public bool IsExportOnly { get; init; }
    public PickIntent Intent { get; } = new();
    public Observation Observation { get; private set; } = new(PickStatus.Checking, []);
    public string? VerifiedSourceSha256 { get; private set; }
    public string Name => Photo.Name;
    public int SharedFolderCount { get; private set; }
    public string Status => (IsExportOnly ? "Unmatched / older export · preserved" : error ?? (Intent.UnknownParity ? "Checking · change queued" : Intent.Pending ? Intent.Requested == true ? "Copying…" : "Removing…" : Observation.Status.ToString())) + (SharedFolderCount > 1 ? $" · shared across {SharedFolderCount} folders" : "");
    public bool? Picked => Intent.Observed;
    public bool HasError => error != null;
    private string? error;
    public event Action<PhotoRow, bool?, bool?>? PickedChanged;
    public void Toggle(long sequence) { error = null; Intent.Toggle(sequence); Notify(); }
    public void Observe(Observation observation)
    {
        var old = Picked;
        if (observation.Sha256 != null) VerifiedSourceSha256 = observation.Sha256;
        else if (observation.Status != PickStatus.Unpicked || Photo.Revision.ChangeToken == null) VerifiedSourceSha256 = null;
        Observation = observation; Intent.Observe(observation.Picked);
        error = observation.Error == null ? null : observation.Status + " · " + observation.Error;
        if (old != Picked) PickedChanged?.Invoke(this, old, Picked); Notify();
    }
    public void Fail(string message) { error = "Error · " + message; Notify(); }
    public bool SetSharedFolderCount(int count) { if (SharedFolderCount == count) return false; SharedFolderCount = count; OnPropertyChanged(nameof(SharedFolderCount)); Notify(); return true; }
    public void Notify() { OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Picked)); OnPropertyChanged(nameof(HasError)); }
}
