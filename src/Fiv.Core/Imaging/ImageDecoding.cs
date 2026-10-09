namespace Fiv.Imaging;

/// <summary>What is known about a file before it is decoded.</summary>
public readonly record struct ImageInfo(bool IsJpeg, int Width, int Height, Orientation Orientation);

/// <summary>
/// The two decodes the viewer runs per image: a viewport-sized preview where the format has a
/// cheap one (JPEG), and the full image with its mip chain. Both can be stopped through a cancel
/// flag and then return nothing.
/// </summary>
public static unsafe class ImageDecoding
{
    /// <summary>How far a preview may be magnified to fill the viewport. It is on screen only until the full decode lands.</summary>
    public const double PreviewUpscale = 1.5;

    public static ImageInfo Probe(FileBytes file)
    {
        bool jpeg = file.Length > 3 && file.Data[0] == 0xFF && file.Data[1] == 0xD8 && file.Data[2] == 0xFF;
        if (jpeg && JpegDecoder.TryReadSize(file.Data, file.Length, out int width, out int height))
            return new ImageInfo(true, width, height, Exif.ReadJpegOrientation(file.Data, file.Length));
        return new ImageInfo(false, 0, 0, Orientation.Normal);
    }

    /// <summary>
    /// The reduction (1, 2, 4, 8) at which a preview for a viewport of the given size is decoded.
    /// 1 means there is no cheaper preview than the full image.
    /// </summary>
    public static int PreviewDenominator(in ImageInfo info, int viewWidth, int viewHeight)
    {
        if (!info.IsJpeg) return 1;
        (int boxWidth, int boxHeight) = info.Orientation.SwapsAxes() ? (viewHeight, viewWidth) : (viewWidth, viewHeight);
        double fit = Math.Min(1.0, Math.Min((double)boxWidth / info.Width, (double)boxHeight / info.Height)) / PreviewUpscale;
        return JpegDecoder.ChooseDenominator(info.Width, info.Height, (int)Math.Ceiling(info.Width * fit), (int)Math.Ceiling(info.Height * fit));
    }

    /// <summary>Decodes the preview. Null if there is none for this file (see <see cref="PreviewDenominator"/>) or the decode was cancelled.</summary>
    public static DecodedImage? DecodePreview(FileBytes file, in ImageInfo info, int viewWidth, int viewHeight, int* cancel)
    {
        int denominator = PreviewDenominator(info, viewWidth, viewHeight);
        return denominator == 1 ? null : JpegDecoder.Decode(file.Data, file.Length, denominator, JpegDecodeOptions.None, cancel);
    }

    /// <summary>
    /// Decodes the whole image. Null if cancelled. A JPEG that libjpeg-turbo rejects (CMYK, 12-bit)
    /// gets a second chance with the Windows codec.
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
        return WicDecoder.Decode(file.Data, file.Length);
    }
}
