using System.Runtime.InteropServices;
using Fiv.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>
/// An image on the GPU, as one texture or, beyond the texture size limit, as overlapping tiles.
/// Coordinates are in pixels of the image file, also for a reduced-size preview, so that a
/// preview and the full image are positioned identically.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    public readonly struct Tile
    {
        public ID3D11ShaderResourceView* View { get; init; }

        /// <summary>The image region the texture holds, including the overlap with its neighbours.</summary>
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>The image region this tile draws.</summary>
        public int DrawLeft { get; init; }
        public int DrawTop { get; init; }
        public int DrawRight { get; init; }
        public int DrawBottom { get; init; }

        public int TextureWidth { get; init; }
        public int TextureHeight { get; init; }
    }

    // Tiles advance by TileStep and overlap by TileOverlap on each side, 8192 texels at most.
    // Both are multiples of 64, so the first seven mip levels of every tile are plain
    // sub-rectangles of the image's mip chain.
    const int MaxSingleTexture = 16384;
    const int TileStep = 8064;
    const int TileOverlap = 64;
    const int MaxTiledMipLevels = 7;

    public int Width { get; }
    public int Height { get; }
    public PixelLayout Layout { get; }
    public bool IsPreview { get; }
    public long ByteCount { get; private set; }
    public IReadOnlyList<Tile> Tiles => _tiles;

    readonly List<Tile> _tiles = [];

    GpuImage(DecodedImage image)
    {
        Width = image.SourceWidth;
        Height = image.SourceHeight;
        Layout = image.Layout;
        IsPreview = image.Width != image.SourceWidth || image.Height != image.SourceHeight;
    }

    static DXGI_FORMAT Format(PixelLayout layout) => layout switch
    {
        PixelLayout.Bgra8 => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        PixelLayout.Gray8 => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
        PixelLayout.Gray16 => DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
        PixelLayout.GrayFloat => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
        PixelLayout.Rgba16 => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>
    /// Creates the textures with their contents. Callable from any thread. The pixel memory is
    /// only read during the call. Without <paramref name="mips"/> the textures have one level.
    /// </summary>
    public static GpuImage Create(ID3D11Device* device, DecodedImage image, MipChain? mips)
    {
        var result = new GpuImage(image);
        try
        {
            if (image.Width <= MaxSingleTexture && image.Height <= MaxSingleTexture)
            {
                result.AddTile(device, image, mips, 0, 0, image.Width, image.Height, 0, 0, image.Width, image.Height,
                    mips?.LevelCount ?? 1);
                return result;
            }
            foreach (var (top, bottom) in Split(image.Height))
            {
                foreach (var (left, right) in Split(image.Width))
                {
                    int x0 = Math.Max(0, left - TileOverlap), x1 = Math.Min(image.Width, right + TileOverlap);
                    int y0 = Math.Max(0, top - TileOverlap), y1 = Math.Min(image.Height, bottom + TileOverlap);
                    int levels = 1;
                    if (mips != null)
                    {
                        while (levels < MaxTiledMipLevels && levels < mips.LevelCount && Math.Max(x1 - x0, y1 - y0) >> levels > 0) levels++;
                    }
                    result.AddTile(device, image, mips, x0, y0, x1 - x0, y1 - y0, left, top, right, bottom, levels);
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    static IEnumerable<(int Start, int End)> Split(int size)
    {
        if (size <= MaxSingleTexture)
        {
            yield return (0, size);
            yield break;
        }
        for (int start = 0; start < size; start += TileStep) yield return (start, Math.Min(size, start + TileStep));
    }

    void AddTile(ID3D11Device* device, DecodedImage image, MipChain? mips, int x, int y, int width, int height,
        int drawLeft, int drawTop, int drawRight, int drawBottom, int levels)
    {
        int bytesPerPixel = image.Layout.BytesPerPixel();
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)width;
        desc.Height = (uint)height;
        desc.MipLevels = (uint)levels;
        desc.ArraySize = 1;
        desc.Format = Format(image.Layout);
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA* data = stackalloc D3D11_SUBRESOURCE_DATA[levels];
        long bytes = 0;
        for (int level = 0; level < levels; level++)
        {
            byte* pixels = mips != null ? mips.Data(level) : image.Pixels;
            int stride = mips != null ? mips.Stride(level) : image.Stride;
            data[level] = new D3D11_SUBRESOURCE_DATA
            {
                pSysMem = pixels + (long)(y >> level) * stride + (long)(x >> level) * bytesPerPixel,
                SysMemPitch = (uint)stride,
            };
            bytes += (long)Math.Max(1, width >> level) * Math.Max(1, height >> level) * bytesPerPixel;
        }

        ID3D11Texture2D* texture;
        Hr.Check(device->CreateTexture2D(&desc, data, &texture), "CreateTexture2D");
        ID3D11ShaderResourceView* view;
        HRESULT hr = device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &view);
        texture->Release();
        Hr.Check(hr, "CreateShaderResourceView");

        // A preview's texels each stand for several image pixels.
        double scaleX = (double)image.SourceWidth / image.Width, scaleY = (double)image.SourceHeight / image.Height;
        _tiles.Add(new Tile
        {
            View = view,
            X = (int)(x * scaleX),
            Y = (int)(y * scaleY),
            Width = (int)Math.Round(width * scaleX),
            Height = (int)Math.Round(height * scaleY),
            DrawLeft = (int)(drawLeft * scaleX),
            DrawTop = (int)(drawTop * scaleY),
            DrawRight = (int)Math.Round(drawRight * scaleX),
            DrawBottom = (int)Math.Round(drawBottom * scaleY),
            TextureWidth = width,
            TextureHeight = height,
        });
        ByteCount += bytes;
    }

    public void Dispose()
    {
        foreach (Tile tile in _tiles) tile.View->Release();
        _tiles.Clear();
    }
}
