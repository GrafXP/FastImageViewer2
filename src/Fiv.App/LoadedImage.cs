using System.Diagnostics;
using Fiv.Imaging;
using Fiv.Render;
using Fiv.Scheduling;

namespace Fiv.App;

/// <summary>
/// One file in the image cache: what has been decoded of it, what is on the GPU, and which jobs
/// are under way. Owned by <see cref="ImagePipeline"/> and touched only on the UI thread, except
/// for <see cref="AcquireFile"/>, which the decode workers call.
/// </summary>
internal sealed unsafe class LoadedImage(string path, long listedSize, long listedModified, bool previewName)
{
    public string Path { get; } = path;

    /// <summary>Size and modified time from the folder index, 0 if unknown. A file that no longer matches is reloaded.</summary>
    public long ListedSize { get; set; } = listedSize;
    public long ListedModified { get; set; } = listedModified;

    /// <summary>
    /// The name says JPEG or camera raw (whose embedded JPEG stands in for it), so a preview stage
    /// is planned. The content decides in the end.
    /// </summary>
    public bool PreviewName { get; } = previewName;

    /// <summary>Size of the stored image in pixels, before the orientation is applied.</summary>
    public int Width, Height;
    public Orientation Orientation = Orientation.Normal;
    public long FileSize;
    public string? Error;

    public DecodedImage? Preview, Full;
    public MipChain? Mips;
    public GpuImage? GpuPreview, GpuFull;
    public (float Min, float Max) ValueRange = (0, 1);

    // --- pipeline state
    public int Rank = int.MaxValue; // 0: the image asked for; 1..: prefetched around it; MaxValue: only cached
    public bool WantFull;
    public WorkItem? PreviewJob, FullJob;
    public UploadTicket? PreviewUpload, FullUpload;
    public bool FullUploadFailed;

    /// <summary>The preview is decoded, or this file turned out to have none.</summary>
    public bool PreviewResolved;
    public bool HasNoPreview;

    /// <summary>The full decode succeeded or failed; either way it will not be tried again.</summary>
    public bool FullResolved;
    public bool Disposed;

    /// <summary>Removed from the cache while on screen (its file changed); whoever shows it disposes it afterwards.</summary>
    public bool Orphaned;
    public long LastUsed;

    // --- timings, for the overlay and the benchmarks
    public double ReadMs, PreviewDecodeMs, FullDecodeMs, MipMs, UploadMs;
    public int PreviewWidth, PreviewHeight;
    public long FileReadAt, PreviewDecodedAt, FullDecodedAt;

    /// <summary>Stopwatch timestamps of the first frame presented with any picture of this image, and with the full one; 0 until then.</summary>
    public long FirstFrame, FullFrame;

    public bool HasSize => Width > 0 && Height > 0;
    public double UprightWidth => Orientation.SwapsAxes() ? Height : Width;
    public double UprightHeight => Orientation.SwapsAxes() ? Width : Height;
    public GpuImage? BestGpu => GpuFull ?? GpuPreview;
    public PixelLayout? Layout => (Full ?? Preview)?.Layout ?? BestGpu?.Layout;
    public long CpuBytes => (Preview?.ByteCount ?? 0) + (Full?.ByteCount ?? 0) + (Mips?.ByteCount ?? 0);
    public long GpuBytes => (GpuPreview?.ByteCount ?? 0) + (GpuFull?.ByteCount ?? 0);

    // --- the file's bytes, read once and shared by the decodes of this image
    readonly object _fileGate = new();
    FileBytes? _file;

    /// <summary>
    /// The file in memory, read on first use. The caller releases what it gets. A second decode
    /// of the same image waits here for the first one's read instead of reading the file again,
    /// which matters on slow disks and network shares.
    /// </summary>
    public FileBytes AcquireFile(out double readMs, out long readAt)
    {
        lock (_fileGate)
        {
            if (_file == null)
            {
                long started = Stopwatch.GetTimestamp();
                _file = FileBytes.Read(Path);
                _readAt = Stopwatch.GetTimestamp();
                _readMs = Stopwatch.GetElapsedTime(started, _readAt).TotalMilliseconds;
            }
            _file.AddReference();
            readMs = _readMs;
            readAt = _readAt;
            return _file;
        }
    }

    double _readMs;
    long _readAt;

    /// <summary>Lets go of the file's bytes; decodes still running keep their own reference.</summary>
    public void ReleaseFile()
    {
        lock (_fileGate)
        {
            _file?.Release();
            _file = null;
        }
    }

    /// <summary>Frees everything. Uploads in flight hold their own references to the pixels they read.</summary>
    public void Dispose()
    {
        Disposed = true;
        GpuPreview?.Dispose();
        GpuFull?.Dispose();
        Preview?.Dispose();
        Full?.Dispose();
        Mips?.Dispose();
        GpuPreview = GpuFull = null;
        Preview = Full = null;
        Mips = null;
        ReleaseFile();
    }

    /// <summary>
    /// The stored samples at an image position, as the file has them: R, G, B, A for colour,
    /// one value for grayscale. False outside the image or when no decoded pixels are held.
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
}
