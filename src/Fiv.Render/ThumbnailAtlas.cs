using System.Numerics;
using Fiv.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>Where a thumbnail sits in the atlas: which texture, which part of it, and its size in pixels.</summary>
public readonly struct AtlasSlot
{
    /// <summary>The page's shader resource view (an ID3D11ShaderResourceView*), as the UI library wants it.</summary>
    public nint Texture { get; init; }
    public Vector2 Uv0 { get; init; }
    public Vector2 Uv1 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>A quick stand-in (the camera's embedded thumbnail); the proper one is on its way.</summary>
    public bool Provisional { get; init; }
}

/// <summary>
/// Thumbnails on the GPU: a few large textures divided into equal cells, so that a whole grid of
/// them is drawn with a handful of draw calls. Thumbnails are stored at the size they are shown
/// at, one texel per screen pixel, which needs no mip levels and little memory. When all cells
/// are taken, the one unused for longest is given to the newcomer. UI thread only.
/// </summary>
public sealed unsafe class ThumbnailAtlas : IDisposable
{
    const int PageSize = 2048;

    struct Cell
    {
        public ulong Key;
        public int LastUsed;
        public ushort Width, Height;
        public bool Used, Provisional;
    }

    readonly GraphicsDevice _gpu;
    readonly int _maxPages;
    readonly List<nint> _textures = [];
    readonly List<nint> _views = [];
    readonly Dictionary<ulong, int> _cellOf = [];
    Cell[] _cells = [];
    int _cellSize, _cellsPerRow, _cellsPerPage, _nextFree, _frame;

    public int CellSize => _cellSize;
    public int Count => _cellOf.Count;
    public long ByteCount => (long)_textures.Count * PageSize * PageSize * 4;

    public ThumbnailAtlas(GraphicsDevice gpu, int cellSize, int maxPages = 12)
    {
        _gpu = gpu;
        _maxPages = maxPages;
        Reset(cellSize);
    }

    /// <summary>Empties the atlas and sets the cell size. The textures are kept and reused.</summary>
    public void Reset(int cellSize)
    {
        _cellSize = Math.Clamp(cellSize, 16, PageSize);
        _cellsPerRow = PageSize / _cellSize;
        _cellsPerPage = _cellsPerRow * _cellsPerRow;
        _cells = new Cell[_cellsPerPage * _maxPages];
        _cellOf.Clear();
        _nextFree = 0;
    }

    /// <summary>Starts a frame; cells looked up from now on count as in use.</summary>
    public void BeginFrame() => _frame++;

    public bool Contains(ulong key, out bool provisional)
    {
        provisional = false;
        if (!_cellOf.TryGetValue(key, out int index)) return false;
        provisional = _cells[index].Provisional;
        return true;
    }

    public bool TryGet(ulong key, out AtlasSlot slot)
    {
        if (!_cellOf.TryGetValue(key, out int index))
        {
            slot = default;
            return false;
        }
        ref Cell cell = ref _cells[index];
        cell.LastUsed = _frame;
        int page = index / _cellsPerPage, within = index % _cellsPerPage;
        float x = within % _cellsPerRow * _cellSize, y = within / _cellsPerRow * _cellSize;
        slot = new AtlasSlot
        {
            Texture = _views[page],
            Uv0 = new Vector2(x / PageSize, y / PageSize),
            Uv1 = new Vector2((x + cell.Width) / PageSize, (y + cell.Height) / PageSize),
            Width = cell.Width,
            Height = cell.Height,
            Provisional = cell.Provisional,
        };
        return true;
    }

    /// <summary>
    /// Stores a BGRA8 thumbnail no larger than the cell size. A proper thumbnail replaces a
    /// provisional one under the same key; a provisional one never replaces a proper one.
    /// </summary>
    public void Put(ulong key, DecodedImage image, bool provisional)
    {
        if (image.Layout != PixelLayout.Bgra8 || image.Width > _cellSize || image.Height > _cellSize) return;
        if (_cellOf.TryGetValue(key, out int index))
        {
            if (provisional && !_cells[index].Provisional) return;
        }
        else
        {
            index = Allocate();
            if (index < 0) return;
            _cellOf[key] = index;
        }
        _cells[index] = new Cell
        {
            Key = key,
            LastUsed = _frame,
            Width = (ushort)image.Width,
            Height = (ushort)image.Height,
            Used = true,
            Provisional = provisional,
        };

        int page = index / _cellsPerPage, within = index % _cellsPerPage;
        uint x = (uint)(within % _cellsPerRow * _cellSize), y = (uint)(within / _cellsPerRow * _cellSize);
        var box = new D3D11_BOX { left = x, top = y, front = 0, right = x + (uint)image.Width, bottom = y + (uint)image.Height, back = 1 };
        _gpu.Context->UpdateSubresource((ID3D11Resource*)_textures[page], 0, &box, image.Pixels, (uint)image.Stride, 0);
    }

    /// <summary>A free cell, a cell on a new page, or the cell unused for longest. -1 if every cell was used this frame.</summary>
    int Allocate()
    {
        int capacity = _textures.Count * _cellsPerPage;
        if (_nextFree < capacity) return _nextFree++;
        if (_textures.Count < _maxPages && AddPage()) return _nextFree++;

        int oldest = -1;
        for (int i = 0; i < capacity; i++)
        {
            if (_cells[i].LastUsed >= _frame - 1) continue; // on screen now
            if (oldest < 0 || _cells[i].LastUsed < _cells[oldest].LastUsed) oldest = i;
        }
        if (oldest >= 0) _cellOf.Remove(_cells[oldest].Key);
        return oldest;
    }

    bool AddPage()
    {
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = PageSize;
        desc.Height = PageSize;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* texture;
        if (_gpu.Device->CreateTexture2D(&desc, (D3D11_SUBRESOURCE_DATA*)null, &texture).Failed) return false; // out of video memory: recycle cells instead
        ID3D11ShaderResourceView* view;
        HRESULT hr = _gpu.Device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &view);
        if (hr.Failed)
        {
            texture->Release();
            return false;
        }
        _textures.Add((nint)texture);
        _views.Add((nint)view);
        return true;
    }

    public void Dispose()
    {
        foreach (nint view in _views) ((ID3D11ShaderResourceView*)view)->Release();
        foreach (nint texture in _textures) ((ID3D11Texture2D*)texture)->Release();
        _views.Clear();
        _textures.Clear();
        _cellOf.Clear();
    }
}
