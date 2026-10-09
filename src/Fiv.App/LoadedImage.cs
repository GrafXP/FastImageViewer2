using Fiv.Imaging;
using Fiv.Render;

namespace Fiv.App;

/// <summary>
/// Everything held for the image on display: the decoded pixels (kept for the pixel readout),
/// the textures, and what the loader reported. Touched only on the UI thread, except that a
/// worker reads <see cref="Full"/> and <see cref="Mips"/> while <see cref="Uploading"/> is set.
/// </summary>
internal sealed unsafe class LoadedImage(string path, int generation)
{
    public string Path { get; } = path;
    public int Generation { get; } = generation;

    /// <summary>Size of the stored image in pixels, before the orientation is applied.</summary>
    public int Width, Height;
    public Orientation Orientation = Orientation.Normal;
    public long FileSize;
    public string? Error;

    public DecodedImage? Preview, Full;
    public MipChain? Mips;
    public GpuImage? GpuPreview, GpuFull;
    public (float Min, float Max) ValueRange = (0, 1);
    public LoadResult? PreviewInfo, FullInfo;
    public double UploadMs;

    /// <summary>Stopwatch timestamps of the first frame presented with the preview and with the full image; 0 until then.</summary>
    public long PreviewFrame, FullFrame;

    public bool Uploading;
    bool _disposeRequested;

    public bool HasSize => Width > 0 && Height > 0;
    public double UprightWidth => Orientation.SwapsAxes() ? Height : Width;
    public double UprightHeight => Orientation.SwapsAxes() ? Width : Height;
    public GpuImage? BestGpu => GpuFull ?? GpuPreview;
    public PixelLayout? Layout => (Full ?? Preview)?.Layout;

    /// <summary>Frees everything, or does so when the running upload has finished.</summary>
    public void Dispose()
    {
        if (Uploading)
        {
            _disposeRequested = true;
            return;
        }
        GpuPreview?.Dispose();
        GpuFull?.Dispose();
        Preview?.Dispose();
        Full?.Dispose();
        Mips?.Dispose();
        GpuPreview = GpuFull = null;
        Preview = Full = null;
        Mips = null;
    }

    /// <summary>Called on the UI thread when the upload worker is done. Returns false if the image was dropped meanwhile.</summary>
    public bool UploadFinished()
    {
        Uploading = false;
        if (!_disposeRequested) return true;
        Dispose();
        return false;
    }

    /// <summary>
    /// The stored samples at an image position, as the file has them: R, G, B, A for colour,
    /// one value for grayscale. False outside the image or before anything is decoded.
    /// </summary>
    public bool TryReadPixel(int x, int y, out PixelValue value)
    {
        value = default;
        DecodedImage? image = Full ?? Preview;
        if (image == null || x < 0 || y < 0 || x >= Width || y >= Height) return false;
        // A preview answers with the texel that covers the position.
        int px = Math.Min(image.Width - 1, (int)((long)x * image.Width / Width));
        int py = Math.Min(image.Height - 1, (int)((long)y * image.Height / Height));
        byte* p = image.Pixels + (long)py * image.Stride + (long)px * image.Layout.BytesPerPixel();
        value = image.Layout switch
        {
            PixelLayout.Bgra8 => new PixelValue(image.Layout, p[2], p[1], p[0], p[3]),
            PixelLayout.Gray8 => new PixelValue(image.Layout, p[0], 0, 0, 0),
            PixelLayout.Gray16 => new PixelValue(image.Layout, *(ushort*)p, 0, 0, 0),
            PixelLayout.GrayFloat => new PixelValue(image.Layout, *(float*)p, 0, 0, 0),
            PixelLayout.Rgba16 => new PixelValue(image.Layout, ((ushort*)p)[0], ((ushort*)p)[1], ((ushort*)p)[2], ((ushort*)p)[3]),
            _ => default,
        };
        value = value with { FromPreview = image != Full };
        return true;
    }
}

/// <summary>Sample values of one pixel: R, G, B, A for colour layouts, only <see cref="R"/> for grayscale ones.</summary>
internal readonly record struct PixelValue(PixelLayout Layout, float R, float G, float B, float A)
{
    public bool FromPreview { get; init; }

    public string Format() => Layout switch
    {
        PixelLayout.Bgra8 => A == 255 ? $"R {R:0} G {G:0} B {B:0}" : $"R {R:0} G {G:0} B {B:0} A {A:0}",
        PixelLayout.Rgba16 => $"R {R:0} G {G:0} B {B:0} A {A:0}",
        PixelLayout.GrayFloat => $"{R:G6}",
        _ => $"{R:0}",
    };
}
