using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>A rectangle of whole canvas pixels.</summary>
public readonly record struct CanvasRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static CanvasRect FromEdges(int left, int top, int right, int bottom) => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public CanvasRect Intersect(CanvasRect other) =>
        FromEdges(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

/// <summary>
/// One image of a compared pair as the numbers see it: its full-resolution pixels and where they
/// lie on the canvas. Rows are read in canvas coordinates as R, G, B, A floats, whatever the
/// layout and orientation of the stored image. Where the placement moves whole pixels (top-left,
/// centred, any orientation, an alignment by whole pixels) every canvas pixel is exactly one
/// stored pixel; where it scales, turns or moves by a fraction of a pixel, the four nearest
/// stored pixels are blended, as on screen.
/// </summary>
public sealed unsafe class CompareSource
{
    public DecodedImage Image { get; }

    /// <summary>Stored pixel coordinates to canvas coordinates.</summary>
    public Affine2 ToCanvas { get; }

    /// <summary>
    /// The smallest rectangle of canvas pixels that holds every pixel whose centre the image
    /// covers. An image that is not turned covers all of it; of a turned one, <see cref="RowSpan"/>
    /// says which pixels of a row it covers.
    /// </summary>
    public CanvasRect Bounds { get; }

    /// <summary>True if every canvas pixel is exactly one stored pixel.</summary>
    public bool PixelAligned { get; }

    /// <summary>True if the image's edges run along the rows and columns of the canvas, so that it covers the whole of <see cref="Bounds"/>.</summary>
    public bool Upright { get; }

    public bool IsColor => Image.Layout is PixelLayout.Bgra8 or PixelLayout.Rgba16;

    /// <summary>Bits per sample: 8, 16, or 0 for float.</summary>
    public int Depth => DepthOf(Image.Layout);

    /// <summary>The stored value of white and of an opaque pixel: 255, 65535, or 1 for float.</summary>
    public float FullValue => Depth switch
    {
        8 => 255f,
        16 => 65535f,
        _ => 1f,
    };

    public static int DepthOf(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgra8 or PixelLayout.Gray8 => 8,
        PixelLayout.Gray16 or PixelLayout.Rgba16 => 16,
        _ => 0,
    };

    readonly Affine2 _toStored;

    public CompareSource(DecodedImage image, in Affine2 storedToCanvas)
    {
        Image = image;
        ToCanvas = storedToCanvas;
        _toStored = storedToCanvas.Inverse();

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (int corner = 0; corner < 4; corner++)
        {
            (double x, double y) = storedToCanvas.Apply((corner & 1) * image.Width, (corner >> 1) * image.Height);
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        // A pixel belongs to the image if its centre is inside.
        Bounds = CanvasRect.FromEdges(Whole(Math.Ceiling(left - 0.5)), Whole(Math.Ceiling(top - 0.5)), Whole(Math.Ceiling(right - 0.5)), Whole(Math.Ceiling(bottom - 0.5)));

        Affine2 m = _toStored;
        PixelAligned = IsUnit(m.A) && IsUnit(m.B) && IsUnit(m.C) && IsUnit(m.D) && IsWhole(m.Tx) && IsWhole(m.Ty);
        Upright = (Math.Abs(m.B) < 1e-12 && Math.Abs(m.C) < 1e-12) || (Math.Abs(m.A) < 1e-12 && Math.Abs(m.D) < 1e-12);

        static bool IsUnit(double v) => Math.Abs(v) < 1e-9 || Math.Abs(Math.Abs(v) - 1) < 1e-9;
        static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-6;
    }

    static int Whole(double value) => (int)Math.Clamp(value, -1e9, 1e9);

    /// <summary>
    /// The canvas pixels of row <paramref name="y"/> whose centres the image covers: from
    /// <paramref name="left"/> up to, not including, <paramref name="right"/>. None if the two
    /// are equal. An image is a convex shape on the canvas, so there is one such run per row.
    /// </summary>
    public void RowSpan(int y, out int left, out int right)
    {
        left = Bounds.X;
        right = Bounds.Right;
        if (y < Bounds.Y || y >= Bounds.Bottom)
        {
            right = left;
            return;
        }
        if (Upright) return;
        // Along the row, the stored position of pixel x's centre is start + x * step: inside the image while neither coordinate leaves it.
        (double startX, double startY) = _toStored.Apply(0.5, y + 0.5);
        Clip(startX, _toStored.A, Image.Width, ref left, ref right);
        Clip(startY, _toStored.B, Image.Height, ref left, ref right);
        if (right < left) right = left;
    }

    /// <summary>Narrows left .. right to the x for which 0 &lt;= start + x * step &lt; size.</summary>
    static void Clip(double start, double step, double size, ref int left, ref int right)
    {
        if (Math.Abs(step) < 1e-12)
        {
            if (!(start >= 0 && start < size)) right = left;
            return;
        }
        double atZero = -start / step, atSize = (size - start) / step;
        if (step > 0)
        {
            left = Math.Max(left, Whole(Math.Ceiling(atZero)));
            right = Math.Min(right, Whole(Math.Ceiling(atSize)));
        }
        else
        {
            left = Math.Max(left, Whole(Math.Floor(atSize)) + 1);
            right = Math.Min(right, Whole(Math.Floor(atZero)) + 1);
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> pixels of canvas row <paramref name="y"/>, starting at
    /// <paramref name="x"/>. Each stored sample is multiplied by <paramref name="scale"/>. A
    /// grayscale image gives its value as red, green and blue and is opaque. The pixels of a
    /// pixel-aligned image must all lie in <see cref="Bounds"/>; of any other, a pixel the image
    /// does not cover (see <see cref="RowSpan"/>) reads as the image's nearest edge.
    /// </summary>
    public void ReadRow(int y, int x, int count, float* r, float* g, float* b, float* a, float scale)
    {
        DecodedImage image = Image;
        (double sx, double sy) = _toStored.Apply(x + 0.5, y + 0.5);
        if (!PixelAligned)
        {
            ReadRowBlended(sx, sy, count, r, g, b, a, scale);
            return;
        }

        int bytesPerPixel = image.Layout.BytesPerPixel();
        int px = Math.Clamp((int)Math.Floor(sx), 0, image.Width - 1), py = Math.Clamp((int)Math.Floor(sy), 0, image.Height - 1);
        long step = (long)Math.Round(_toStored.A) * bytesPerPixel + (long)Math.Round(_toStored.B) * image.Stride;
        byte* p = image.Pixels + (long)py * image.Stride + (long)px * bytesPerPixel;
        float opaque = FullValue * scale;
        switch (image.Layout)
        {
            case PixelLayout.Bgra8:
                for (int i = 0; i < count; i++, p += step)
                {
                    b[i] = p[0] * scale;
                    g[i] = p[1] * scale;
                    r[i] = p[2] * scale;
                    a[i] = p[3] * scale;
                }
                break;
            case PixelLayout.Gray8:
                for (int i = 0; i < count; i++, p += step)
                {
                    r[i] = g[i] = b[i] = p[0] * scale;
                    a[i] = opaque;
                }
                break;
            case PixelLayout.Gray16:
                for (int i = 0; i < count; i++, p += step)
                {
                    r[i] = g[i] = b[i] = *(ushort*)p * scale;
                    a[i] = opaque;
                }
                break;
            case PixelLayout.GrayFloat:
                for (int i = 0; i < count; i++, p += step)
                {
                    r[i] = g[i] = b[i] = *(float*)p * scale;
                    a[i] = opaque;
                }
                break;
            case PixelLayout.Rgba16:
                for (int i = 0; i < count; i++, p += step)
                {
                    ushort* q = (ushort*)p;
                    r[i] = q[0] * scale;
                    g[i] = q[1] * scale;
                    b[i] = q[2] * scale;
                    a[i] = q[3] * scale;
                }
                break;
        }
    }

    void ReadRowBlended(double sx, double sy, int count, float* r, float* g, float* b, float* a, float scale)
    {
        DecodedImage image = Image;
        float* c00 = stackalloc float[4], c10 = stackalloc float[4], c01 = stackalloc float[4], c11 = stackalloc float[4];
        for (int i = 0; i < count; i++)
        {
            // Sample positions are pixel centres: the stored pixel k is at k + 0.5.
            double fx = sx + i * _toStored.A - 0.5, fy = sy + i * _toStored.B - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = (float)(fx - x0), ty = (float)(fy - y0);
            int x1 = Math.Clamp(x0 + 1, 0, image.Width - 1), y1 = Math.Clamp(y0 + 1, 0, image.Height - 1);
            x0 = Math.Clamp(x0, 0, image.Width - 1);
            y0 = Math.Clamp(y0, 0, image.Height - 1);
            Fetch(x0, y0, c00);
            Fetch(x1, y0, c10);
            Fetch(x0, y1, c01);
            Fetch(x1, y1, c11);
            r[i] = Blend(c00[0], c10[0], c01[0], c11[0], tx, ty) * scale;
            g[i] = Blend(c00[1], c10[1], c01[1], c11[1], tx, ty) * scale;
            b[i] = Blend(c00[2], c10[2], c01[2], c11[2], tx, ty) * scale;
            a[i] = Blend(c00[3], c10[3], c01[3], c11[3], tx, ty) * scale;
        }

        // From one value towards the next by a fraction: four equal values give exactly that value,
        // which four weights that only nearly add up to one would not (an opaque image has to stay opaque).
        static float Blend(float v00, float v10, float v01, float v11, float tx, float ty)
        {
            float upper = v00 + (v10 - v00) * tx, lower = v01 + (v11 - v01) * tx;
            return upper + (lower - upper) * ty;
        }
    }

    /// <summary>One stored pixel as R, G, B, A in stored units.</summary>
    void Fetch(int x, int y, float* color)
    {
        DecodedImage image = Image;
        byte* p = image.Pixels + (long)y * image.Stride + (long)x * image.Layout.BytesPerPixel();
        switch (image.Layout)
        {
            case PixelLayout.Bgra8:
                color[0] = p[2];
                color[1] = p[1];
                color[2] = p[0];
                color[3] = p[3];
                break;
            case PixelLayout.Gray8:
                color[0] = color[1] = color[2] = p[0];
                color[3] = 255;
                break;
            case PixelLayout.Gray16:
                color[0] = color[1] = color[2] = *(ushort*)p;
                color[3] = 65535;
                break;
            case PixelLayout.GrayFloat:
                color[0] = color[1] = color[2] = *(float*)p;
                color[3] = 1;
                break;
            case PixelLayout.Rgba16:
                color[0] = ((ushort*)p)[0];
                color[1] = ((ushort*)p)[1];
                color[2] = ((ushort*)p)[2];
                color[3] = ((ushort*)p)[3];
                break;
        }
    }
}
