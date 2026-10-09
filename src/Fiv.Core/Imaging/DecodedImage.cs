using System.Runtime.InteropServices;

namespace Fiv.Imaging;

/// <summary>Sample layout of a decoded image. Values are stored as decoded: 16-bit and float data are not reduced to 8 bits.</summary>
public enum PixelLayout : byte
{
    Bgra8,
    Gray8,
    Gray16,
    GrayFloat,
    Rgba16,
}

public static class PixelLayoutExtensions
{
    public static int BytesPerPixel(this PixelLayout layout) => layout switch
    {
        PixelLayout.Bgra8 => 4,
        PixelLayout.Gray8 => 1,
        PixelLayout.Gray16 => 2,
        PixelLayout.GrayFloat => 4,
        PixelLayout.Rgba16 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };
}

/// <summary>
/// Pixels in native memory, in a layout the GPU accepts without conversion.
/// <see cref="SourceWidth"/> and <see cref="SourceHeight"/> are the size of the file's image;
/// they are larger than <see cref="Width"/> and <see cref="Height"/> for a reduced-size decode.
/// Ownership is counted: whoever calls <see cref="AddReference"/> also calls <see cref="Dispose"/>,
/// and the memory goes with the last one. That lets a worker keep reading the pixels of an
/// image the UI thread has already dropped.
/// </summary>
public sealed unsafe class DecodedImage : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelLayout Layout { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public byte* Pixels { get; private set; }

    public long ByteCount => (long)Stride * Height;

    public DecodedImage(int width, int height, PixelLayout layout, int sourceWidth, int sourceHeight)
    {
        Width = width;
        Height = height;
        Layout = layout;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Stride = width * layout.BytesPerPixel();
        Pixels = (byte*)NativeMemory.AlignedAlloc((nuint)Stride * (nuint)height, 64);
    }

    int _references = 1;

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        NativeMemory.AlignedFree(Pixels);
        Pixels = null;
        GC.SuppressFinalize(this);
    }

    ~DecodedImage() => NativeMemory.AlignedFree(Pixels);
}
