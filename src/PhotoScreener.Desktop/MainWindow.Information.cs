using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;

public partial class MainWindow
{
    private readonly PhotoInformationWorker information = new();
    private long informationGeneration;
    private void InitializeInformation()
    {
        this.FindControl<Button>("InfoToggleButton")!.Click += (_, _) =>
        {
            InfoPanel.IsVisible = !InfoPanel.IsVisible;
            if (InfoPanel.IsVisible && current != null) { LoadInformation(current); RequestPreview(); }
            InfoToggleButton.Content = InfoPanel.IsVisible ? "›" : "‹";
            SaveLayout();
            Surface.Focus();
        };
        Images.HistogramReady += (photo, context, histogram) => Dispatcher.UIThread.Post(() =>
        {
            if (closing || context != ImageContext || current?.Photo != photo) return;
            Histogram.Values = histogram; Text("HistogramStatus", histogram == null ? "Histogram unavailable" : "Preview luminance · black → white");
        }, DispatcherPriority.Background);
    }
    private void ClearInformation()
    {
        informationGeneration++;
        foreach (var field in new[] { "CaptureTimeValue", "FocalLengthValue", "ExposureValue", "IsoValue", "ApertureValue", "CameraValue", "LensValue", "LocationValue" }) Text(field, "Unknown");
        Text("InfoNotice", ""); Histogram.Values = null; Text("HistogramStatus", "Histogram unavailable");
    }
    private void LoadInformation(PhotoRow row)
    {
        ClearInformation(); var generation = informationGeneration;
        Text("InfoNotice", "Reading photo information…");
        information.Submit(new(row.Photo, result => Dispatcher.UIThread.Post(() =>
        {
            if (closing || generation != informationGeneration || current?.Photo != row.Photo) return;
            Text("CaptureTimeValue", result.CaptureTime ?? "Unknown");
            Text("FocalLengthValue", result.FocalLength ?? "Unknown");
            Text("ExposureValue", result.Exposure ?? "Unknown");
            Text("IsoValue", result.Iso ?? "Unknown");
            Text("ApertureValue", result.Aperture ?? "Unknown");
            Text("CameraValue", result.Camera ?? "Unknown");
            Text("LensValue", result.Lens ?? "Unknown");
            Text("LocationValue", result.Latitude.HasValue && result.Longitude.HasValue
                ? result.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture) + ", " + result.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture) + (result.Altitude == null ? "" : " · " + result.Altitude)
                : "Unknown");
            Text("InfoNotice", result.Notice ?? "Embedded photo metadata");
        }, DispatcherPriority.Background)));
    }
}
