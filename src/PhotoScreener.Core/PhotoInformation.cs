namespace PhotoScreener.Core;

public sealed record PhotoInformation(string? CaptureTime = null, string? FocalLength = null, string? Exposure = null, string? Iso = null,
    string? Aperture = null, string? Camera = null, string? Lens = null, double? Latitude = null, double? Longitude = null,
    string? Altitude = null, string? Notice = null);
