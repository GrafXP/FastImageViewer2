using System.Buffers;
using System.Runtime.InteropServices;
using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>
/// The brightness of an image at full size and at a half, a quarter, an eighth and so on of it,
/// for the registration to sample at whatever step it works at. The reduced sizes are float
/// planes, each the average of two by two values of the one before; the full size is not
/// copied but read from the stored pixels, a rectangle at a time. For a 24 MP image that is
/// 32 MB and some 10 ms on all cores.
///
/// Positions are in stored pixel coordinates throughout, pixel k covering k .. k + 1.
/// Ownership is counted, as for <see cref="DecodedImage"/>: whoever calls <see cref="AddReference"/>
/// also calls <see cref="Dispose"/>, so that a worker can go on sampling what the UI thread has let go of.
/// </summary>
public sealed unsafe class LumaPyramid : IDisposable
{
    /// <summary>A reduced size is not made smaller than this either way.</summary>
    const int SmallestLevel = 32;

    readonly DecodedImage _image;
    readonly nint[] _levels;      // [0] unused: level 0 is the stored image itself
    readonly int[] _widths, _heights;
    int _references = 1;
    bool _disposed;

    public int Width => _image.Width;
    public int Height => _image.Height;

    /// <summary>The image whose brightness this is.</summary>
    public DecodedImage Image => _image;

    public long ByteCount { get; }

    /// <summary>Builds the reduced sizes on all cores. Keeps the image's pixels alive until disposed.</summary>
    public LumaPyramid(DecodedImage image)
    {
        _image = image;
        image.AddReference();
        var widths = new List<int> { image.Width };
        var heights = new List<int> { image.Height };
        while (Math.Min(widths[^1], heights[^1]) >= 2 * SmallestLevel)
        {
            widths.Add((widths[^1] + 1) / 2);
            heights.Add((heights[^1] + 1) / 2);
        }
        _widths = [.. widths];
        _heights = [.. heights];
        _levels = new nint[_widths.Length];
        long bytes = 0;
        for (int level = 1; level < _levels.Length; level++)
        {
            long size = (long)_widths[level] * _heights[level] * sizeof(float);
            _levels[level] = (nint)NativeMemory.AlignedAlloc((nuint)size, 64);
            bytes += size;
        }
        ByteCount = bytes;
        if (_levels.Length > 1) HalveStored();
        for (int level = 2; level < _levels.Length; level++) HalvePlane(level);
    }

    /// <summary>Level 1: the brightness of two by two stored pixels, averaged.</summary>
    void HalveStored()
    {
        int width = _image.Width, height = _image.Height, outWidth = _widths[1], outHeight = _heights[1];
        float* output = (float*)_levels[1];
        Parallel.For(0, outHeight, () => new float[2 * width], (y, _, rows) =>
        {
            fixed (float* top = rows)
            {
                float* bottom = top + width, target = output + (long)y * outWidth;
                LumaRow(_image, 2 * y, 0, width, top);
                LumaRow(_image, Math.Min(2 * y + 1, height - 1), 0, width, bottom);
                int pairs = width / 2;
                for (int x = 0; x < pairs; x++) target[x] = (top[2 * x] + top[2 * x + 1] + bottom[2 * x] + bottom[2 * x + 1]) * 0.25f;
                if (pairs < outWidth) target[pairs] = (top[width - 1] + bottom[width - 1]) * 0.5f; // an odd column has no neighbour
            }
            return rows;
        }, _ => { });
    }

    void HalvePlane(int level)
    {
        int width = _widths[level - 1], height = _heights[level - 1], outWidth = _widths[level], outHeight = _heights[level];
        float* input = (float*)_levels[level - 1], output = (float*)_levels[level];
        Parallel.For(0, outHeight, y =>
        {
            float* top = input + (long)(2 * y) * width, bottom = input + (long)Math.Min(2 * y + 1, height - 1) * width, target = output + (long)y * outWidth;
            int pairs = width / 2;
            for (int x = 0; x < pairs; x++) target[x] = (top[2 * x] + top[2 * x + 1] + bottom[2 * x] + bottom[2 * x + 1]) * 0.25f;
            if (pairs < outWidth) target[pairs] = (top[width - 1] + bottom[width - 1]) * 0.5f;
        });
    }

    /// <summary>
    /// The brightness of <paramref name="count"/> stored pixels of row <paramref name="y"/> from
    /// <paramref name="x"/> on, in the units of the file: Rec. 709 luma for colour, the value
    /// for grey. A float sample that is not a finite number counts as 0.
    /// </summary>
    static void LumaRow(DecodedImage image, int y, int x, int count, float* luma)
    {
        byte* p = image.Pixels + (long)y * image.Stride + (long)x * image.Layout.BytesPerPixel();
        float wr = DifferenceMath.LumaWeights.X, wg = DifferenceMath.LumaWeights.Y, wb = DifferenceMath.LumaWeights.Z;
        switch (image.Layout)
        {
            case PixelLayout.Bgra8:
                for (int i = 0; i < count; i++, p += 4) luma[i] = p[0] * wb + p[1] * wg + p[2] * wr;
                break;
            case PixelLayout.Gray8:
                for (int i = 0; i < count; i++) luma[i] = p[i];
                break;
            case PixelLayout.Gray16:
                for (int i = 0; i < count; i++) luma[i] = ((ushort*)p)[i];
                break;
            case PixelLayout.GrayFloat:
                for (int i = 0; i < count; i++)
                {
                    float value = ((float*)p)[i];
                    luma[i] = float.IsFinite(value) ? value : 0f;
                }
                break;
            case PixelLayout.Rgba16:
                for (int i = 0; i < count; i++)
                {
                    ushort* q = (ushort*)p + i * 4;
                    luma[i] = q[0] * wr + q[1] * wg + q[2] * wb;
                }
                break;
        }
    }

    /// <summary>
    /// Fills a grid of <paramref name="columns"/> by <paramref name="rows"/> samples, row after
    /// row: sample (i, j) is the brightness at the stored position <paramref name="gridToStored"/>
    /// gives for (i + 0.5, j + 0.5), blended from the four nearest values of the size that fits
    /// the grid's step. Positions outside the image give not-a-number.
    /// </summary>
    public void Sample(float* destination, int columns, int rows, in Affine2 gridToStored)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Affine2 g = gridToStored;
        double step = Math.Sqrt(Math.Abs(g.A * g.D - g.B * g.C)); // stored pixels per grid step
        int level = step >= 2 ? Math.Min(_levels.Length - 1, (int)Math.Floor(Math.Log2(step))) : 0;
        if (level > 0)
        {
            double scale = 1.0 / (1 << level);
            var toPlane = new Affine2(g.A * scale, g.B * scale, g.C * scale, g.D * scale, g.Tx * scale, g.Ty * scale);
            Blend((float*)_levels[level], _widths[level], _heights[level], toPlane, 0, 0, Width * scale, Height * scale, destination, columns, rows);
            return;
        }

        // Full size: the brightness of the stored pixels the grid reaches, worked out into a scratch rectangle first.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int corner = 0; corner < 4; corner++)
        {
            (double x, double y) = g.Apply((corner & 1) * columns, (corner >> 1) * rows);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        int left = (int)Math.Clamp(Math.Floor(minX) - 1, 0, Width), top = (int)Math.Clamp(Math.Floor(minY) - 1, 0, Height);
        int right = (int)Math.Clamp(Math.Ceiling(maxX) + 1, 0, Width), bottom = (int)Math.Clamp(Math.Ceiling(maxY) + 1, 0, Height);
        if (right <= left || bottom <= top)
        {
            new Span<float>(destination, columns * rows).Fill(float.NaN);
            return;
        }
        int width = right - left, height = bottom - top;
        float[] scratch = ArrayPool<float>.Shared.Rent(width * height);
        try
        {
            fixed (float* plane = scratch)
            {
                for (int y = 0; y < height; y++) LumaRow(_image, top + y, left, width, plane + (long)y * width);
                var toPlane = new Affine2(g.A, g.B, g.C, g.D, g.Tx - left, g.Ty - top);
                Blend(plane, width, height, toPlane, -left, -top, Width - left, Height - top, destination, columns, rows);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Samples a plane on a grid. A position counts as inside the image if it lies in
    /// (<paramref name="insideLeft"/>, <paramref name="insideTop"/>) .. (<paramref name="insideRight"/>,
    /// <paramref name="insideBottom"/>) of the plane's coordinates.
    /// </summary>
    static void Blend(float* plane, int width, int height, in Affine2 gridToPlane,
        double insideLeft, double insideTop, double insideRight, double insideBottom, float* destination, int columns, int rows)
    {
        Affine2 m = gridToPlane;
        int lastX = width - 1, lastY = height - 1;
        for (int j = 0; j < rows; j++)
        {
            (double startX, double startY) = m.Apply(0.5, j + 0.5);
            float* output = destination + (long)j * columns;
            for (int i = 0; i < columns; i++)
            {
                double px = startX + i * m.A, py = startY + i * m.B;
                if (!(px >= insideLeft && px < insideRight && py >= insideTop && py < insideBottom))
                {
                    output[i] = float.NaN;
                    continue;
                }
                // Values are at pixel centres: the value k of the plane is at k + 0.5.
                double fx = px - 0.5, fy = py - 0.5;
                int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                float tx = (float)(fx - x0), ty = (float)(fy - y0);
                int x1 = Math.Clamp(x0 + 1, 0, lastX), y1 = Math.Clamp(y0 + 1, 0, lastY);
                x0 = Math.Clamp(x0, 0, lastX);
                y0 = Math.Clamp(y0, 0, lastY);
                float* upper = plane + (long)y0 * width, lower = plane + (long)y1 * width;
                float a = upper[x0] + (upper[x1] - upper[x0]) * tx, b = lower[x0] + (lower[x1] - lower[x0]) * tx;
                output[i] = a + (b - a) * ty;
            }
        }
    }

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        _disposed = true;
        for (int level = 1; level < _levels.Length; level++)
        {
            NativeMemory.AlignedFree((void*)_levels[level]);
            _levels[level] = 0;
        }
        _image.Dispose();
        GC.SuppressFinalize(this);
    }

    ~LumaPyramid()
    {
        for (int level = 1; level < _levels.Length; level++) NativeMemory.AlignedFree((void*)_levels[level]);
    }
}
