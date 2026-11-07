using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.TestFixtures;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class PhotoInformationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "photoscreener-info-" + Guid.NewGuid().ToString("N"));
    public PhotoInformationTests() => System.IO.Directory.CreateDirectory(root);
    private Photo Photo(byte[] bytes, string name)
    {
        var path = Path.Combine(root, name); File.WriteAllBytes(path, bytes);
        return new SourceCatalog().Enumerate(root, default).Single(photo => photo.Path == path);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EmbeddedCaptureAndGpsDataAreReadWithoutChangingSource(bool png)
    {
        var bytes = png ? PhotoExifFixture.Png(southWest: true) : PhotoExifFixture.Jpeg([0xff, 0xd8, 0xff, 0xd9]);
        var photo = Photo(bytes, png ? "a.png" : "a.JPG"); var result = new PhotoInformationReader().Read(photo, default);
        Assert.Equal("2024-05-06 12:34:56 +02:00", result.CaptureTime);
        Assert.Contains("50", result.FocalLength); Assert.Contains("1/250", result.Exposure); Assert.Equal("400", result.Iso);
        Assert.Contains("2.8", result.Aperture); Assert.Equal("TestCam Model X", result.Camera); Assert.Equal("Lens 50mm", result.Lens);
        Assert.Equal(png ? -47.5 : 47.5, result.Latitude); Assert.Equal(png ? -8.25 : 8.25, result.Longitude);
        Assert.Contains("500", result.Altitude); Assert.Null(result.Notice); Assert.Equal(bytes, File.ReadAllBytes(photo.Path));
    }
    [Fact] public void MissingOrBrokenExifDoesNotInventCaptureTimeOrIso()
    {
        var missing = Photo(PhotoExifFixture.Png(false), "missing.png");
        var reader = new PhotoInformationReader(); var result = reader.Read(missing, default);
        Assert.Null(result.CaptureTime); Assert.Null(result.Iso); Assert.Null(result.Latitude); Assert.NotNull(result.Notice);
        var broken = Photo([0xff, 0xd8, 0xff, 0xe1, 0xff, 0xff, 69, 120, 105, 102, 0, 0, 0xff, 0xd9], "broken.JPG");
        Assert.NotNull(reader.Read(broken, default).Notice);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); Assert.Throws<OperationCanceledException>(() => reader.Read(missing, canceled.Token));
    }
    [Fact] public async Task OptionalInformationWorkerSurvivesADeletedPreviouslyCachedSource()
    {
        var photo = Photo(PhotoExifFixture.Png(), "a.png"); await using var worker = new PhotoInformationWorker();
        async Task<PhotoInformation> Read(Photo requested)
        {
            var completed = new TaskCompletionSource<PhotoInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
            worker.Submit(new(requested, result => completed.TrySetResult(result)));
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal("400", (await Read(photo)).Iso); File.Delete(photo.Path);
        Assert.Contains("unavailable", (await Read(photo)).Notice);
        var next = Photo(PhotoExifFixture.Png(false), "b.png"); Assert.Null((await Read(next)).Iso);
    }
    public void Dispose() => System.IO.Directory.Delete(root, true);
}
