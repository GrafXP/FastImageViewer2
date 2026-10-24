using System.Runtime.InteropServices;
using Fiv.Imaging;

namespace Fiv.Surface;

/// <summary>Which value of a pixel is its height.</summary>
public enum HeightSource
{
    /// <summary>The luma of the colour (Rec. 709 weights); the value itself for a grey image.</summary>
    Brightness,
    Red,
    Green,
    Blue,
    Alpha,
}

/// <summary>
/// The heights of the 3D height map: a value per cell of a grid laid over the image, which is
/// the image reduced by averaging to at most <see cref="MaxSize"/> cells on its long side, and
/// smoothed if asked. The values are the image's own, in the units the GPU samples it in (0..1
/// for 8- and 16-bit, the stored values for float): the display controls make heights of them in
/// the shader, as they make brightness of the samples. The grid is in the stored orientation, as
/// the image's textures are, and its source size is the image's, so it lies over the image as a
/// reduced-size preview does.
/// </summary>
public static unsafe class HeightField
{
    /// <summary>
    /// Cells on the long side at most. Finer than the mesh needs to be: the light is worked out
    /// per screen pixel from these, so they show more detail than the vertices could.
    /// </summary>
    public const int MaxSize = 2048;

    /// <summary>
    /// The grid of an image, as a GrayFloat image. <paramref name="smoothing"/> is a radius in
    /// cells: three box blurs of it, close to a Gaussian of that spread. Float samples that are
    /// not finite (no data) are left out of their cell's average; a cell with none at all gets
    /// the smallest value of the grid. Null if cancelled.
    /// </summary>
    public static DecodedImage? Build(DecodedImage image, HeightSource source, int smoothing, int maxSize = MaxSize, CancellationToken cancel = default)
    {
        (int width, int height) = Resampler.FitWithin(image.Width, image.Height, Math.Max(1, maxSize));
        var grid = new DecodedImage(width, height, PixelLayout.GrayFloat, image.SourceWidth, image.SourceHeight);
        try
        {
            var options = new ParallelOptions { CancellationToken = cancel };
            Reduce(image, source, grid, options);
            FillGaps(grid);
            if (smoothing > 0) Smooth(grid, smoothing, options);
            return grid;
        }
        catch (OperationCanceledException)
        {
            grid.Dispose();
            return null;
        }
        catch
        {
            grid.Dispose();
            throw;
        }
    }

    /// <summary>Weights of R, G, B and A in the value of a colour pixel.</summary>
    static (float R, float G, float B, float A) Weights(HeightSource source) => source switch
    {
        HeightSource.Red => (1, 0, 0, 0),
        HeightSource.Green => (0, 1, 0, 0),
        HeightSource.Blue => (0, 0, 1, 0),
        HeightSource.Alpha => (0, 0, 0, 1),
        _ => (0.2126f, 0.7152f, 0.0722f, 0),
    };

    /// <summary>One row of the image as the values heights are made of; NaN where a float sample is not finite.</summary>
    static void LoadValues(DecodedImage image, int y, HeightSource source, float* values)
    {
        byte* p = image.Pixels + (long)y * image.Stride;
        int count = image.Width;
        (float r, float g, float b, float a) = Weights(source);
        bool alpha = source == HeightSource.Alpha; // a grey image is opaque
        switch (image.Layout)
        {
            case PixelLayout.Bgra8:
                for (int x = 0; x < count; x++, p += 4) values[x] = (r * p[2] + g * p[1] + b * p[0] + a * p[3]) * (1f / 255);
                break;
            case PixelLayout.Rgba16:
            {
                ushort* q = (ushort*)p;
                for (int x = 0; x < count; x++, q += 4) values[x] = (r * q[0] + g * q[1] + b * q[2] + a * q[3]) * (1f / 65535);
                break;
            }
            case PixelLayout.Gray8:
                for (int x = 0; x < count; x++) values[x] = alpha ? 1 : p[x] * (1f / 255);
                break;
            case PixelLayout.Gray16:
                for (int x = 0; x < count; x++) values[x] = alpha ? 1 : ((ushort*)p)[x] * (1f / 65535);
                break;
            case PixelLayout.GrayFloat:
                for (int x = 0; x < count; x++)
                {
                    float v = ((float*)p)[x];
                    values[x] = alpha ? 1 : float.IsFinite(v) ? v : float.NaN;
                }
                break;
        }
    }

    /// <summary>Per worker: a row of the image as values, and the sums of a row of the grid.</summary>
    sealed class Scratch(int imageWidth, int gridWidth) : IDisposable
    {
        public readonly float* Row = (float*)NativeMemory.Alloc((nuint)imageWidth, sizeof(float));
        public readonly double* Sum = (double*)NativeMemory.Alloc((nuint)gridWidth, sizeof(double));
        public readonly double* Weight = (double*)NativeMemory.Alloc((nuint)gridWidth, sizeof(double));

        public void Dispose()
        {
            NativeMemory.Free(Row);
            NativeMemory.Free(Sum);
            NativeMemory.Free(Weight);
        }
    }

    /// <summary>Every cell the average of the pixels it covers, in proportion to how much of each it covers; NaN where none is finite.</summary>
    static void Reduce(DecodedImage image, HeightSource source, DecodedImage grid, ParallelOptions options)
    {
        int width = grid.Width, height = grid.Height;
        var columns = new Resampler.Cover[width];
        double xStep = (double)image.Width / width, yStep = (double)image.Height / height;
        for (int x = 0; x < width; x++) columns[x] = Resampler.Cover.Of(x * xStep, (x + 1) * xStep, image.Width);

        // By rows of the grid. A row of the image on the border between two of them is read by both.
        Parallel.For(0, height, options, () => new Scratch(image.Width, width), (y, _, scratch) =>
        {
            new Span<double>(scratch.Sum, width).Clear();
            new Span<double>(scratch.Weight, width).Clear();
            Resampler.Cover rows = Resampler.Cover.Of(y * yStep, (y + 1) * yStep, image.Height);
            for (int sy = rows.First; sy <= rows.Last; sy++)
            {
                LoadValues(image, sy, source, scratch.Row);
                float rowWeight = rows.Weight(sy);
                for (int x = 0; x < width; x++)
                {
                    Resampler.Cover span = columns[x];
                    double sum = 0, weight = 0;
                    for (int sx = span.First; sx <= span.Last; sx++)
                    {
                        float v = scratch.Row[sx];
                        if (float.IsNaN(v)) continue;
                        float w = span.Weight(sx);
                        sum += v * w;
                        weight += w;
                    }
                    scratch.Sum[x] += sum * rowWeight;
                    scratch.Weight[x] += weight * rowWeight;
                }
            }
            float* output = (float*)(grid.Pixels + (long)y * grid.Stride);
            for (int x = 0; x < width; x++) output[x] = scratch.Weight[x] > 0 ? (float)(scratch.Sum[x] / scratch.Weight[x]) : float.NaN;
            return scratch;
        }, scratch => scratch.Dispose());
    }

    /// <summary>Cells without a value get the smallest value there is: no data lies at the bottom, not in the air.</summary>
    static void FillGaps(DecodedImage grid)
    {
        var values = new Span<float>(grid.Pixels, grid.Width * grid.Height);
        float lowest = float.PositiveInfinity;
        foreach (float v in values)
            if (v < lowest) lowest = v; // NaN compares false
        if (float.IsPositiveInfinity(lowest)) lowest = 0;
        foreach (ref float v in values)
            if (float.IsNaN(v)) v = lowest;
    }

    /// <summary>Three box blurs along the rows, then three down the columns (along the rows of the grid turned over).</summary>
    static void Smooth(DecodedImage grid, int radius, ParallelOptions options)
    {
        int width = grid.Width, height = grid.Height;
        float* values = (float*)grid.Pixels;
        float* turned = (float*)NativeMemory.Alloc((nuint)width * (nuint)height, sizeof(float));
        try
        {
            BlurRows(values, width, height, radius, options);
            Transpose(values, width, height, turned, options);
            BlurRows(turned, height, width, radius, options);
            Transpose(turned, height, width, values, options);
        }
        finally
        {
            NativeMemory.Free(turned);
        }
    }

    static void BlurRows(float* values, int width, int height, int radius, ParallelOptions options)
    {
        Parallel.For(0, height, options, () => (nint)NativeMemory.Alloc((nuint)width, sizeof(float)), (y, _, copy) =>
        {
            float* line = values + (long)y * width;
            for (int pass = 0; pass < 3; pass++) BoxLine(line, (float*)copy, width, radius);
            return copy;
        }, copy => NativeMemory.Free((void*)copy));
    }

    /// <summary>A box blur of one line, its ends extended. The sum runs in double, so that a long line of large values does not drift.</summary>
    static void BoxLine(float* line, float* copy, int count, int radius)
    {
        new Span<float>(line, count).CopyTo(new Span<float>(copy, count));
        double sum = 0;
        for (int i = -radius; i <= radius; i++) sum += copy[Math.Clamp(i, 0, count - 1)];
        double scale = 1.0 / (2 * radius + 1);
        for (int x = 0; x < count; x++)
        {
            line[x] = (float)(sum * scale);
            sum += copy[Math.Min(x + radius + 1, count - 1)] - copy[Math.Max(x - radius, 0)];
        }
    }

    /// <summary>Writes the grid of <paramref name="width"/> by <paramref name="height"/> values into <paramref name="to"/> with rows and columns swapped.</summary>
    static void Transpose(float* from, int width, int height, float* to, ParallelOptions options)
    {
        Parallel.For(0, height, options, y =>
        {
            float* row = from + (long)y * width;
            for (int x = 0; x < width; x++) to[(long)x * height + y] = row[x];
        });
    }
}
