using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;

public sealed class PhotoInformationReader
{
    private static string? Limit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 256 ? value.Trim() : value[..253].Trim() + "…";
    public PhotoInformation Read(Photo photo, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (PathGuard.Revision(photo.Path) != photo.Revision) throw new IOException("Source changed; refresh the folder.");
            using var file = PlatformFileSystem.OpenOwned(photo.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (PlatformFileSystem.Identity(file.SafeFileHandle, out _) != photo.Revision.Identity) throw new IOException("Source changed before reading photo information.");
            using var budget = new MetadataStream(file, token);
            var directories = ImageMetadataReader.ReadMetadata(budget);
            token.ThrowIfCancellationRequested();
            if (PathGuard.Revision(photo.Path) != photo.Revision) throw new IOException("Source changed while reading photo information.");
            var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var primary = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            var time = exif?.GetString(ExifDirectoryBase.TagDateTimeOriginal);
            time = DateTime.TryParseExact(time, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured)
                ? captured.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null;
            var offset = exif?.GetString(0x9011);
            if (!string.IsNullOrWhiteSpace(time) && !string.IsNullOrWhiteSpace(offset)) time += " " + offset;
            var location = gps?.GetGeoLocation();
            var coordinate = location.GetValueOrDefault();
            var validGps = location.HasValue && double.IsFinite(coordinate.Latitude) && double.IsFinite(coordinate.Longitude) && Math.Abs(coordinate.Latitude) <= 90 && Math.Abs(coordinate.Longitude) <= 180;
            var model = primary?.GetDescription(ExifDirectoryBase.TagModel);
            var make = primary?.GetDescription(ExifDirectoryBase.TagMake);
            var camera = string.IsNullOrWhiteSpace(model) ? make : string.IsNullOrWhiteSpace(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : make + " " + model;
            return new(Limit(time), Limit(exif?.GetDescription(ExifDirectoryBase.TagFocalLength)), Limit(exif?.GetDescription(ExifDirectoryBase.TagExposureTime)),
                Limit(exif?.GetDescription(ExifDirectoryBase.TagIsoEquivalent)), Limit(exif?.GetDescription(ExifDirectoryBase.TagFNumber)), Limit(camera),
                Limit(exif?.GetDescription(0xA434)), validGps ? coordinate.Latitude : null, validGps ? coordinate.Longitude : null,
                validGps ? Limit(gps?.GetDescription(GpsDirectory.TagAltitude)) : null,
                exif == null && primary == null && gps == null ? "No embedded capture metadata" : directories.Any(directory => directory.HasError) ? "Some embedded metadata could not be read" : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ImageProcessingException or MetadataException or System.ComponentModel.Win32Exception or ArgumentException or FormatException or OverflowException)
        { return new(Notice: Limit("Photo information unavailable · " + e.Message)); }
    }

    private sealed class MetadataStream(Stream inner, CancellationToken token) : Stream
    {
        private long bytesRead;
        private void Check(int bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytesRead + bytes > 8L * 1024 * 1024) throw new IOException("Embedded metadata exceeds its read budget.");
        }
        public override int Read(byte[] buffer, int offset, int count) { Check(count); var read = inner.Read(buffer, offset, count); bytesRead += read; return read; }
        public override int Read(Span<byte> buffer) { Check(buffer.Length); var read = inner.Read(buffer); bytesRead += read; return read; }
        public override int ReadByte() { Check(1); var value = inner.ReadByte(); if (value >= 0) bytesRead++; return value; }
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
