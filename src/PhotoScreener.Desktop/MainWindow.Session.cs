using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;

public partial class MainWindow
{
    private string? lastSuccessfulFolder;
    private void RememberLastFolder(string folder)
    {
        lastSuccessfulFolder = folder;
        if (sessionSettings != null) mutations?.RunIdle("last-folder", () => sessionSettings.RememberFolder(folder));
    }

    private Task FlushLastFolder() => sessionSettings != null && lastSuccessfulFolder != null
        ? Task.Run(() => sessionSettings.RememberFolder(lastSuccessfulFolder)) : Task.CompletedTask;

    private async Task RestoreLastFolder()
    {
        if (sessionSettings == null) return;
        var request = folderOpenRequest;
        var folder = await Task.Run(sessionSettings.ReadLastFolder);
        if (closing || request != folderOpenRequest) return;
        if (folder == null)
        {
            Text("PreviewMessage", "Open a folder once · It will reopen automatically on your next launch");
            return;
        }
        var available = await Task.Run(() =>
        {
            try
            {
                PathGuard.ValidateNoLinks(folder);
                using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                entries.MoveNext(); // Probe readability; do not scan the whole folder.
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
        });
        if (closing || request != folderOpenRequest) return;
        if (available) await OpenFolder(folder);
        else
        {
            Text("FolderStatus", $"Last folder unavailable: {folder} · Connect the drive and choose Open folder.");
            Text("PreviewMessage", "Last folder unavailable · Connect the drive or open another folder");
            Surface.Focus();
        }
    }
}
