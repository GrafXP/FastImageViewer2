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
/// centred, any orientation) every canvas pixel is exactly one stored pixel; where it scales,
/// the four nearest stored pixels are blended, as on screen.
/// </summary>
public sealed unsafe class CompareSource
{
    public DecodedImage Image { get; }

    /// <summary>Stored pixel coordinates to canvas coordinates.</summary>
    public Affine2 ToCanvas { get; }

    /// <summary>The canvas pixels whose centres the image covers.</summary>
    public CanvasRect Bounds { get; }

    /// <summary>True if every canvas pixel is exactly one stored pixel.</summary>
    public bool PixelAligned { get; }

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

        (double x0, double y0) = storedToCanvas.Apply(0, 0);
        (double x1, double y1) = storedToCanvas.Apply(image.Width, image.Height);
        // A pixel belongs to the image if its centre is inside.
        Bounds = CanvasRect.FromEdges(
            (int)Math.Ceiling(Math.Min(x0, x1) - 0.5), (int)Math.Ceiling(Math.Min(y0, y1) - 0.5),
            (int)Math.Ceiling(Math.Max(x0, x1) - 0.5), (int)Math.Ceiling(Math.Max(y0, y1) - 0.5));

        Affine2 m = _toStored;
        PixelAligned = IsUnit(m.A) && IsUnit(m.B) && IsUnit(m.C) && IsUnit(m.D) && IsWhole(m.Tx) && IsWhole(m.Ty);

        static bool IsUnit(double v) => Math.Abs(v) < 1e-9 || Math.Abs(Math.Abs(v) - 1) < 1e-9;
        static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-6;
    }

    /// <summary>
    /// Reads <paramref name="count"/> pixels of canvas row <paramref name="y"/>, starting at
    /// <paramref name="x"/>, all of which must lie in <see cref="Bounds"/>. Each stored sample is
    /// multiplied by <paramref name="scale"/>. A grayscale image gives its value as red, green
    /// and blue and is opaque.
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
            float w00 = (1 - tx) * (1 - ty) * scale, w10 = tx * (1 - ty) * scale, w01 = (1 - tx) * ty * scale, w11 = tx * ty * scale;
            r[i] = c00[0] * w00 + c10[0] * w10 + c01[0] * w01 + c11[0] * w11;
            g[i] = c00[1] * w00 + c10[1] * w10 + c01[1] * w01 + c11[1] * w11;
            b[i] = c00[2] * w00 + c10[2] * w10 + c01[2] * w01 + c11[2] * w11;
            a[i] = c00[3] * w00 + c10[3] * w10 + c01[3] * w01 + c11[3] * w11;
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
