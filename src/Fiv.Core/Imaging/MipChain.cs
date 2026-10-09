using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fiv.Imaging;

/// <summary>
/// Half-size copies of an image down to 1x1, each the 2x2 average of the level above (sizes
/// round down, as Direct3D expects). Level 0 is the image itself and is not owned by the chain.
/// Computed on the decode thread so that the texture can be created complete, off the UI thread.
/// </summary>
public sealed unsafe class MipChain : IDisposable
{
    readonly byte*[] _data;
    readonly int[] _width, _height, _stride;
    byte* _block;

    public int LevelCount => _data.Length;
    public byte* Data(int level) => _data[level];
    public int Width(int level) => _width[level];
    public int Height(int level) => _height[level];
    public int Stride(int level) => _stride[level];

    public MipChain(DecodedImage image)
    {
        int levels = 1;
        for (int size = Math.Max(image.Width, image.Height); size > 1; size >>= 1) levels++;
        _data = new byte*[levels];
        _width = new int[levels];
        _height = new int[levels];
        _stride = new int[levels];

        int bytesPerPixel = image.Layout.BytesPerPixel();
        long total = 0;
        for (int i = 0; i < levels; i++)
        {
            _width[i] = Math.Max(1, image.Width >> i);
            _height[i] = Math.Max(1, image.Height >> i);
            _stride[i] = i == 0 ? image.Stride : _width[i] * bytesPerPixel;
            if (i > 0) total += (long)_stride[i] * _height[i];
        }
        _block = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(total, 1), 64);
        _data[0] = image.Pixels;
        byte* next = _block;
        for (int i = 1; i < levels; i++)
        {
            _data[i] = next;
            next += (long)_stride[i] * _height[i];
        }

        // The large levels in parallel by rows; the small ones are not worth a dispatch.
        for (int i = 1; i < levels; i++)
        {
            int level = i;
            if ((long)_width[level] * _height[level] >= 1 << 18)
                Parallel.For(0, _height[level], y => Downsample(image.Layout, level, y));
            else
                for (int y = 0; y < _height[level]; y++) Downsample(image.Layout, level, y);
        }
    }

    void Downsample(PixelLayout layout, int level, int y)
    {
        int sourceWidth = _width[level - 1], sourceHeight = _height[level - 1];
        // A 1-pixel-wide or -high source has no second column or row to average with.
        int y1 = Math.Min(2 * y + 1, sourceHeight - 1);
        byte* row0 = _data[level - 1] + (long)(2 * y) * _stride[level - 1];
        byte* row1 = _data[level - 1] + (long)y1 * _stride[level - 1];
        byte* destination = _data[level] + (long)y * _stride[level];
        int width = _width[level];
        bool pairs = sourceWidth >= 2;

        switch (layout)
        {
            case PixelLayout.Bgra8:
                DownsampleBytes(row0, row1, destination, width, 4, pairs);
                break;
            case PixelLayout.Gray8:
                DownsampleBytes(row0, row1, destination, width, 1, pairs);
                break;
            case PixelLayout.Gray16:
            case PixelLayout.Rgba16:
            {
                int channels = layout == PixelLayout.Rgba16 ? 4 : 1, step = pairs ? channels : 0;
                ushort* a = (ushort*)row0, b = (ushort*)row1, d = (ushort*)destination;
                for (int i = 0; i < width * channels; i++)
                {
                    int s = (i / channels) * 2 * channels + i % channels;
                    d[i] = (ushort)((a[s] + a[s + step] + b[s] + b[s + step] + 2) >> 2);
                }
                break;
            }
            case PixelLayout.GrayFloat:
            {
                float* a = (float*)row0, b = (float*)row1, d = (float*)destination;
                int step = pairs ? 1 : 0;
                for (int x = 0; x < width; x++) d[x] = (a[2 * x] + a[2 * x + step] + b[2 * x] + b[2 * x + step]) * 0.25f;
                break;
            }
        }
    }

    static void DownsampleBytes(byte* row0, byte* row1, byte* destination, int width, int channels, bool pairs)
    {
        int x = 0;
        if (channels == 4 && pairs && Avx2.IsSupported)
        {
            // 8 source pixels per row -> 4 destination pixels. Two rounded byte averages; the
            // result is at most one level above the exact rounded mean.
            Vector256<int> even = Vector256.Create(0, 2, 4, 6, 0, 0, 0, 0), odd = Vector256.Create(1, 3, 5, 7, 0, 0, 0, 0);
            for (; x + 4 <= width; x += 4)
            {
                Vector256<byte> rows = Avx2.Average(Avx.LoadVector256(row0 + x * 8), Avx.LoadVector256(row1 + x * 8));
                Vector128<byte> left = Avx2.PermuteVar8x32(rows.AsInt32(), even).GetLower().AsByte();
                Vector128<byte> right = Avx2.PermuteVar8x32(rows.AsInt32(), odd).GetLower().AsByte();
                Sse2.Store(destination + x * 4, Sse2.Average(left, right));
            }
        }
        int step = pairs ? channels : 0;
        for (int i = x * channels; i < width * channels; i++)
        {
            int s = (i / channels) * 2 * channels + i % channels;
            destination[i] = (byte)((row0[s] + row0[s + step] + row1[s] + row1[s + step] + 2) >> 2);
        }
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_block);
        _block = null;
        GC.SuppressFinalize(this);
    }

    ~MipChain() => NativeMemory.AlignedFree(_block);
}
