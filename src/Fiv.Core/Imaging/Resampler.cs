using System.Runtime.InteropServices;

namespace Fiv.Imaging;

/// <summary>
/// Reduces images by area averaging: every source pixel contributes to the result in proportion
/// to how much of an output pixel it covers. Used for thumbnails, where the output is small and
/// quality matters more than speed.
/// </summary>
public static unsafe class Resampler
{
    /// <summary>The largest size with the image's aspect ratio that fits in a square of <paramref name="max"/>, never larger than the image.</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int max)
    {
        double scale = Math.Min(1.0, (double)max / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Scales the rectangle (<paramref name="cropX"/>, <paramref name="cropY"/>, <paramref name="cropWidth"/>,
    /// <paramref name="cropHeight"/>) of an image of any layout to BGRA8 of exactly the given size.
    /// Grayscale becomes grey BGRA; 16-bit and float grey are mapped so that <paramref name="low"/>
    /// is black and <paramref name="high"/> white (in the units the GPU samples: 0..1 for 16-bit).
    /// Meant for reducing; when asked to enlarge it repeats pixels.
    /// </summary>
    public static DecodedImage ToBgra(DecodedImage source, int width, int height, float low = 0, float high = 1,
        int cropX = 0, int cropY = 0, int cropWidth = 0, int cropHeight = 0)
    {
        if (cropWidth <= 0) cropWidth = source.Width - cropX;
        if (cropHeight <= 0) cropHeight = source.Height - cropY;
        var result = new DecodedImage(width, height, PixelLayout.Bgra8, source.SourceWidth, source.SourceHeight);
        int channels = source.Layout is PixelLayout.Bgra8 or PixelLayout.Rgba16 ? 4 : 1;

        // Per output column: the source columns it covers and how much of the first and last one.
        var columns = new Cover[width];
        double xStep = (double)cropWidth / width;
        for (int x = 0; x < width; x++) columns[x] = Cover.Of(x * xStep, (x + 1) * xStep, cropWidth);

        float* row = (float*)NativeMemory.Alloc((nuint)(cropWidth * channels) * sizeof(float));
        float* reduced = (float*)NativeMemory.Alloc((nuint)(width * channels) * sizeof(float));
        float* sum = (float*)NativeMemory.Alloc((nuint)(width * channels) * sizeof(float));
        try
        {
            double yStep = (double)cropHeight / height;
            int loaded = -1;
            for (int y = 0; y < height; y++)
            {
                Cover rows = Cover.Of(y * yStep, (y + 1) * yStep, cropHeight);
                new Span<float>(sum, width * channels).Clear();
                for (int sy = rows.First; sy <= rows.Last; sy++)
                {
                    if (sy != loaded) // the row on the border between two output rows is reduced once and used twice
                    {
                        LoadRow(source, cropX, cropY + sy, cropWidth, row, low, high);
                        ReduceRow(row, reduced, columns, channels);
                        loaded = sy;
                    }
                    float weight = rows.Weight(sy);
                    for (int i = 0; i < width * channels; i++) sum[i] += reduced[i] * weight;
                }

                byte* output = result.Pixels + (long)y * result.Stride;
                float scale = 1f / rows.Total;
                if (channels == 4)
                {
                    for (int i = 0; i < width * 4; i++) output[i] = (byte)Math.Clamp(sum[i] * scale + 0.5f, 0f, 255f);
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = (byte)Math.Clamp(sum[x] * scale + 0.5f, 0f, 255f);
                        output[x * 4] = output[x * 4 + 1] = output[x * 4 + 2] = value;
                        output[x * 4 + 3] = 255;
                    }
                }
            }
            return result;
        }
        finally
        {
            NativeMemory.Free(row);
            NativeMemory.Free(reduced);
            NativeMemory.Free(sum);
        }
    }

    /// <summary>The source positions [First, Last] one output position averages, with partial weights at both ends.</summary>
    readonly struct Cover
    {
        public readonly int First, Last;
        readonly float _firstWeight, _lastWeight;
        public readonly float Total;

        Cover(int first, int last, float firstWeight, float lastWeight)
        {
            First = first;
            Last = last;
            _firstWeight = firstWeight;
            _lastWeight = lastWeight;
            Total = first == last ? firstWeight : firstWeight + lastWeight + (last - first - 1);
        }

        public static Cover Of(double start, double end, int limit)
        {
            int first = Math.Min(limit - 1, (int)Math.Floor(start));
            int last = Math.Clamp((int)Math.Ceiling(end) - 1, first, limit - 1);
            if (first == last) return new Cover(first, last, 1f, 1f);
            return new Cover(first, last, (float)(first + 1 - start), (float)(end - last));
        }

        public float Weight(int position) => position == First ? _firstWeight : position == Last ? _lastWeight : 1f;
    }

    static void ReduceRow(float* row, float* reduced, Cover[] columns, int channels)
    {
        for (int x = 0; x < columns.Length; x++)
        {
            Cover span = columns[x];
            float scale = 1f / span.Total;
            for (int c = 0; c < channels; c++)
            {
                float sum = 0;
                for (int sx = span.First; sx <= span.Last; sx++) sum += row[sx * channels + c] * span.Weight(sx);
                reduced[x * channels + c] = sum * scale;
            }
        }
    }

    /// <summary>One source row as floats in 0..255: B, G, R, A for colour layouts, one value for grey ones.</summary>
    static void LoadRow(DecodedImage source, int x0, int y, int count, float* row, float low, float high)
    {
        byte* p = source.Pixels + (long)y * source.Stride + (long)x0 * source.Layout.BytesPerPixel();
        float span = MathF.Abs(high - low) > 1e-12f ? high - low : 1f;
        switch (source.Layout)
        {
            case PixelLayout.Bgra8:
                for (int i = 0; i < count * 4; i++) row[i] = p[i];
                break;
            case PixelLayout.Gray8:
                for (int i = 0; i < count; i++) row[i] = p[i];
                break;
            case PixelLayout.Gray16:
            {
                float scale = 255f / (65535f * span), offset = low * 255f / span;
                for (int i = 0; i < count; i++) row[i] = Math.Clamp(((ushort*)p)[i] * scale - offset, 0f, 255f);
                break;
            }
            case PixelLayout.GrayFloat:
            {
                float scale = 255f / span;
                for (int i = 0; i < count; i++)
                {
                    float value = (((float*)p)[i] - low) * scale;
                    row[i] = float.IsFinite(value) ? Math.Clamp(value, 0f, 255f) : 0f;
                }
                break;
            }
            case PixelLayout.Rgba16:
                for (int i = 0; i < count; i++)
                {
                    ushort* q = (ushort*)p + i * 4;
                    row[i * 4] = q[2] * (255f / 65535f);
                    row[i * 4 + 1] = q[1] * (255f / 65535f);
                    row[i * 4 + 2] = q[0] * (255f / 65535f);
                    row[i * 4 + 3] = q[3] * (255f / 65535f);
                }
                break;
        }
    }

    /// <summary>
    /// Turns a BGRA8 image upright: the pixels are rearranged, unlike in the main view, where the
    /// orientation is only a transform. Worth it for thumbnails, which are small and drawn by the thousand.
    /// </summary>
    public static DecodedImage Upright(DecodedImage image, Orientation orientation)
    {
        if (orientation == Orientation.Normal || image.Layout != PixelLayout.Bgra8)
        {
            image.AddReference();
            return image;
        }
        bool swap = orientation.SwapsAxes();
        int width = swap ? image.Height : image.Width, height = swap ? image.Width : image.Height;
        (int sourceWidth, int sourceHeight) = swap ? (image.SourceHeight, image.SourceWidth) : (image.SourceWidth, image.SourceHeight);
        var result = new DecodedImage(width, height, PixelLayout.Bgra8, sourceWidth, sourceHeight);
        // Where each upright pixel comes from: the inverse of the stored-to-upright transform, at pixel centres.
        Affine2 toStored = orientation.ToUpright(image.Width, image.Height).Inverse();
        for (int y = 0; y < height; y++)
        {
            uint* output = (uint*)(result.Pixels + (long)y * result.Stride);
            for (int x = 0; x < width; x++)
            {
                (double sx, double sy) = toStored.Apply(x + 0.5, y + 0.5);
                int ix = Math.Clamp((int)sx, 0, image.Width - 1), iy = Math.Clamp((int)sy, 0, image.Height - 1);
                output[x] = ((uint*)(image.Pixels + (long)iy * image.Stride))[ix];
            }
        }
        return result;
    }
}
