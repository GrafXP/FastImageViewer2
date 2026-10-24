using Fiv.Native;

namespace Fiv.Imaging;

/// <summary>Which decoder a file goes to.</summary>
public enum ImageCodec : byte
{
    /// <summary>
    /// The Windows Imaging Component: PNG, TIFF, BMP, GIF, ICO, JPEG XR and whatever else Windows
    /// has a codec for. A TIFF it does not read after all goes to libtiff.
    /// </summary>
    Wic,

    /// <summary>
    /// libtiff: the TIFFs WIC does not read (10-, 14-, 24-bit samples, 64-bit float, LogLuv,
    /// uncompressed YCbCr) or reads wrongly without saying so (32-bit and signed integers, 16-bit float).
    /// </summary>
    Tiff,
    Jpeg,
    Heif,
    WebP,
    JpegXl,
    Raw,
}

/// <summary>What is known about a file before it is decoded.</summary>
/// <param name="Orientation">
/// How the decoded pixels have to be turned to be upright: from EXIF for a JPEG, from the file for
/// a raw, which is decoded as the sensor has it. The other decoders turn their images upright themselves.
/// </param>
public readonly record struct ImageInfo(bool IsJpeg, int Width, int Height, Orientation Orientation)
{
    public ImageCodec Codec { get; init; } = IsJpeg ? ImageCodec.Jpeg : ImageCodec.Wic;

    /// <summary>A JPEG with more than 8 bits per sample: it can only be decoded in full, so it has no preview.</summary>
    public bool HighPrecision { get; init; }

    /// <summary>Raw: the JPEG the camera embedded, as a range of the file's bytes, and its size; length 0 if it has none.</summary>
    public long PreviewOffset { get; init; }
    public int PreviewLength { get; init; }
    public int PreviewWidth { get; init; }
    public int PreviewHeight { get; init; }
}

/// <summary>
/// The two decodes the viewer runs per image: a viewport-sized preview where the format has a
/// cheap one (JPEG; the JPEG embedded in a raw), and the full image with its mip chain. Both can be
/// stopped through a cancel flag and then return nothing.
/// </summary>
public static unsafe class ImageDecoding
{
    /// <summary>How far a preview may be magnified to fill the viewport. It is on screen only until the full decode lands.</summary>
    public const double PreviewUpscale = 1.5;

    /// <summary>An embedded raw preview smaller than this is not worth showing, even briefly.</summary>
    const int MinRawPreviewSize = 512;

    public static ImageInfo Probe(FileBytes file)
    {
        bool jpeg = file.Length > 3 && file.Data[0] == 0xFF && file.Data[1] == 0xD8 && file.Data[2] == 0xFF;
        if (jpeg && JpegDecoder.TryReadSize(file.Data, file.Length, out int width, out int height, out int precision))
            return new ImageInfo(true, width, height, Exif.ReadJpegOrientation(file.Data, file.Length)) { HighPrecision = precision > 8 };
        var wic = new ImageInfo(false, 0, 0, Orientation.Normal);
        // PNG, GIF and BMP are always Windows' own: fivcodecs.dll is not even loaded for them.
        ImageFileFormat format = ImageProbe.Identify(new ReadOnlySpan<byte>(file.Data, (int)Math.Min(file.Length, 64)));
        if (jpeg || format is ImageFileFormat.Png or ImageFileFormat.Gif or ImageFileFormat.Bmp) return wic;

        FivCodecs.Info native = NativeDecoder.Probe(file.Data, file.Length);
        switch (native.Format)
        {
            case FivCodecs.Format.Heif: return wic with { Codec = ImageCodec.Heif };
            case FivCodecs.Format.WebP: return wic with { Codec = ImageCodec.WebP };
            case FivCodecs.Format.JpegXl: return wic with { Codec = ImageCodec.JpegXl };
            case FivCodecs.Format.Tiff: return wic with { Codec = ImageCodec.Tiff };
            case FivCodecs.Format.Raw:
            {
                var orientation = native.Orientation is >= 1 and <= 8 ? (Orientation)native.Orientation : Orientation.Normal;
                var raw = new ImageInfo(false, native.Width, native.Height, orientation) { Codec = ImageCodec.Raw };
                if (native.PreviewLength > 0 && native.PreviewOffset >= 0 && (ulong)native.PreviewOffset + (ulong)native.PreviewLength <= file.Length &&
                    JpegDecoder.TryReadSize(file.Data + native.PreviewOffset, (nuint)native.PreviewLength, out int previewWidth, out int previewHeight) &&
                    Math.Max(previewWidth, previewHeight) >= MinRawPreviewSize)
                {
                    raw = raw with
                    {
                        PreviewOffset = native.PreviewOffset, PreviewLength = native.PreviewLength,
                        PreviewWidth = previewWidth, PreviewHeight = previewHeight,
                    };
                }
                return raw;
            }
            default:
                return wic;
        }
    }

    public static bool IsTiff(FileBytes file) =>
        ImageProbe.Identify(new ReadOnlySpan<byte>(file.Data, (int)Math.Min(file.Length, 8))) == ImageFileFormat.Tiff;

    /// <summary>
    /// The reduction (1, 2, 4, 8) at which a preview for a viewport of the given size is decoded:
    /// of the file for a JPEG, of the embedded JPEG for a raw. 1 for a JPEG means there is no
    /// cheaper preview than the full image.
    /// </summary>
    public static int PreviewDenominator(in ImageInfo info, int viewWidth, int viewHeight)
    {
        bool raw = info.PreviewLength > 0;
        if ((!info.IsJpeg || info.HighPrecision) && !raw) return 1;
        (int width, int height) = raw ? (info.PreviewWidth, info.PreviewHeight) : (info.Width, info.Height);
        (int boxWidth, int boxHeight) = info.Orientation.SwapsAxes() ? (viewHeight, viewWidth) : (viewWidth, viewHeight);
        double fit = Math.Min(1.0, Math.Min((double)boxWidth / width, (double)boxHeight / height)) / PreviewUpscale;
        return JpegDecoder.ChooseDenominator(width, height, (int)Math.Ceiling(width * fit), (int)Math.Ceiling(height * fit));
    }

    /// <summary>
    /// Whether a preview is decoded before the full image: a JPEG reduced inside the decoder, or
    /// the JPEG a camera embedded in a raw, which decodes in a fraction of the time the raw takes.
    /// </summary>
    public static bool HasPreview(in ImageInfo info, int viewWidth, int viewHeight) =>
        info.PreviewLength > 0 || PreviewDenominator(info, viewWidth, viewHeight) > 1;

    /// <summary>Decodes the preview. Null if there is none for this file (see <see cref="HasPreview"/>) or the decode was cancelled.</summary>
    public static DecodedImage? DecodePreview(FileBytes file, in ImageInfo info, int viewWidth, int viewHeight, int* cancel)
    {
        if (!HasPreview(info, viewWidth, viewHeight)) return null;
        int denominator = PreviewDenominator(info, viewWidth, viewHeight);
        if (info.PreviewLength == 0) return JpegDecoder.Decode(file.Data, file.Length, denominator, JpegDecodeOptions.None, cancel);
        try
        {
            // It stands for the raw: drawn at the raw's size, turned as the raw is.
            return JpegDecoder.Decode(file.Data + info.PreviewOffset, (nuint)info.PreviewLength, denominator, JpegDecodeOptions.None, cancel,
                info.Width, info.Height);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the whole image. Null if cancelled. A JPEG that libjpeg-turbo rejects (CMYK, 12-bit),
    /// or a file the bundled decoders reject, gets a second chance with the Windows codec.
    /// </summary>
    public static DecodedImage? DecodeFull(FileBytes file, in ImageInfo info, int* cancel)
    {
        if (info.IsJpeg)
        {
            try
            {
                return JpegDecoder.Decode(file.Data, file.Length, 1, JpegDecodeOptions.None, cancel);
            }
            catch (InvalidDataException)
            {
            }
        }
        if (cancel != null && *cancel != 0) return null;
        InvalidDataException? failure = null;
        if (NativeFormat(info.Codec) is FivCodecs.Format native)
        {
            try
            {
                return NativeDecoder.Decode(file.Data, file.Length, native, 0, cancel);
            }
            catch (InvalidDataException e)
            {
                failure = e;
            }
        }
        try
        {
            return WicDecoder.Decode(file.Data, file.Length);
        }
        catch (Exception e) when ((failure != null || IsTiff(file)) && e is not OutOfMemoryException)
        {
            if (failure != null) throw failure; // what the bundled decoder had to say is more use than "component not found"
            return NativeDecoder.Decode(file.Data, file.Length, FivCodecs.Format.Tiff, 0, cancel);
        }
    }

    /// <summary>The bundled decoder for the codec, or null if it is not one of theirs.</summary>
    public static FivCodecs.Format? NativeFormat(ImageCodec codec) => codec switch
    {
        ImageCodec.Heif => FivCodecs.Format.Heif,
        ImageCodec.WebP => FivCodecs.Format.WebP,
        ImageCodec.JpegXl => FivCodecs.Format.JpegXl,
        ImageCodec.Raw => FivCodecs.Format.Raw,
        ImageCodec.Tiff => FivCodecs.Format.Tiff,
        _ => null,
    };
}
