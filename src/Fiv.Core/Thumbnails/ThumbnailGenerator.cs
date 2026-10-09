using Fiv.Imaging;

namespace Fiv.Thumbnails;

/// <summary>
/// A small upright BGRA8 picture of an image, plus what was learned about the image on the way.
/// <see cref="Width"/> and <see cref="Height"/> are the image's size as stored in the file.
/// </summary>
public sealed class Thumbnail(DecodedImage pixels, int width, int height, Orientation orientation, bool embedded) : IDisposable
{
    public DecodedImage Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public Orientation Orientation { get; } = orientation;

    /// <summary>Taken from the thumbnail the camera embedded: quick, small, and to be replaced by a proper one.</summary>
    public bool Embedded { get; } = embedded;

    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Makes thumbnails from the cheapest source a file offers: the thumbnail embedded in a JPEG's
/// EXIF block (no decode of the image at all), a reduced-size JPEG decode, or, for everything
/// else, a decode through WIC that averages down as it goes.
/// </summary>
public static unsafe class ThumbnailGenerator
{
    /// <summary>The long side of the thumbnails that are kept on disk. Smaller ones are made from these.</summary>
    public const int MasterSize = 256;

    /// <summary>An embedded thumbnail smaller than this is not worth showing even briefly.</summary>
    const int MinEmbeddedSize = 96;

    /// <summary>
    /// The thumbnail embedded by the camera, if the file has one: decoded, with the black bars
    /// cameras add to fit their fixed 4:3 thumbnail removed, and turned upright. Null if there is none.
    /// </summary>
    public static Thumbnail? FromEmbedded(FileBytes file, in ImageInfo info) =>
        info.IsJpeg ? FromEmbedded(file.Data, file.Length, info) : null;

    /// <summary>The same from the first part of a file; enough if it reaches past the JPEG's metadata to its frame header.</summary>
    public static Thumbnail? FromEmbedded(byte* data, nuint length)
    {
        bool jpeg = length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        if (!jpeg || !JpegDecoder.TryReadSize(data, length, out int width, out int height)) return null;
        return FromEmbedded(data, length, new ImageInfo(true, width, height, Exif.ReadJpegOrientation(data, length)));
    }

    static Thumbnail? FromEmbedded(byte* data, nuint length, in ImageInfo info)
    {
        if (!Exif.TryFindJpegThumbnail(data, length, out int offset, out int size)) return null;
        DecodedImage? decoded;
        try
        {
            decoded = JpegDecoder.Decode(data + offset, (nuint)size);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (decoded == null) return null;
        using (decoded)
        {
            if (Math.Max(decoded.Width, decoded.Height) < MinEmbeddedSize) return null;
            // Keep the part of the thumbnail that has the image's own aspect ratio.
            double aspect = (double)info.Width / info.Height;
            int cropWidth = decoded.Width, cropHeight = decoded.Height;
            if ((double)decoded.Width / decoded.Height > aspect * 1.02) cropWidth = Math.Max(1, (int)Math.Round(decoded.Height * aspect));
            else if ((double)decoded.Width / decoded.Height < aspect / 1.02) cropHeight = Math.Max(1, (int)Math.Round(decoded.Width / aspect));
            (int width, int height) = Resampler.FitWithin(cropWidth, cropHeight, MasterSize);
            using DecodedImage reduced = Resampler.ToBgra(decoded, width, height, 0, 1,
                (decoded.Width - cropWidth) / 2, (decoded.Height - cropHeight) / 2, cropWidth, cropHeight);
            return new Thumbnail(Resampler.Upright(reduced, info.Orientation), info.Width, info.Height, info.Orientation, embedded: true);
        }
    }

    /// <summary>
    /// A thumbnail of at most <see cref="MasterSize"/> pixels made from the image itself.
    /// Null if <paramref name="cancel"/> was set while a JPEG was being decoded.
    /// </summary>
    public static Thumbnail? FromImage(FileBytes file, in ImageInfo info, int* cancel = null)
    {
        if (info.IsJpeg)
        {
            try
            {
                (int width, int height) = Resampler.FitWithin(info.Width, info.Height, MasterSize);
                int denominator = JpegDecoder.ChooseDenominator(info.Width, info.Height, width, height);
                DecodedImage? decoded = JpegDecoder.Decode(file.Data, file.Length, denominator, JpegDecodeOptions.FastUpsample, cancel);
                if (decoded == null) return null;
                using (decoded)
                using (DecodedImage reduced = Resampler.ToBgra(decoded, width, height))
                    return new Thumbnail(Resampler.Upright(reduced, info.Orientation), info.Width, info.Height, info.Orientation, embedded: false);
            }
            catch (InvalidDataException)
            {
                // A JPEG libjpeg-turbo rejects (CMYK, 12-bit): the Windows codec may still read it.
            }
        }

        DecodedImage? small = WicDecoder.DecodeReduced(file.Data, file.Length, MasterSize);
        if (small != null) return new Thumbnail(small, small.SourceWidth, small.SourceHeight, Orientation.Normal, embedded: false);

        // More than 8 bits per channel: decode in full and reduce over the range of values the
        // image actually uses, as the main view shows it.
        using DecodedImage full = WicDecoder.Decode(file.Data, file.Length);
        (float low, float high) = ImageStatistics.ValueRange(full);
        (int w, int h) = Resampler.FitWithin(full.Width, full.Height, MasterSize);
        return new Thumbnail(Resampler.ToBgra(full, w, h, low, high), full.Width, full.Height, Orientation.Normal, embedded: false);
    }

    /// <summary>A thumbnail of the given size made from a stored one (or any BGRA8 image): reduced if larger, as is otherwise.</summary>
    public static DecodedImage Fit(DecodedImage master, int size)
    {
        (int width, int height) = Resampler.FitWithin(master.Width, master.Height, size);
        if (width == master.Width && height == master.Height && master.Layout == PixelLayout.Bgra8)
        {
            master.AddReference();
            return master;
        }
        return Resampler.ToBgra(master, width, height);
    }
}
