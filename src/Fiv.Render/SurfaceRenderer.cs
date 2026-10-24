using System.Numerics;
using System.Runtime.InteropServices;
using Fiv.Comparing;
using Fiv.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>How the 3D height map is drawn: from where, how high, how lit and in which colours.</summary>
public struct SurfaceSettings
{
    /// <summary>The camera: world to clip space. The ground is the upright image, a world unit an image pixel, y up.</summary>
    public Matrix4x4 WorldToClip;

    /// <summary>Stored image pixels to the ground: the image's orientation, and in a comparison its place on the canvas.</summary>
    public Affine2 ImageToGround;

    /// <summary>The world height of a value shown as white; 0 lays the image flat.</summary>
    public float HeightScale;

    /// <summary>Grid cells along the image's long side; never more than the height grid has values.</summary>
    public int MeshCells;

    /// <summary>The mip level of the heights read at the finest, which smooths heights that were not smoothed before: 0 for none.</summary>
    public float HeightLevel;

    /// <summary>How much the light shades the surface: 0 not at all, 1 fully.</summary>
    public float Shading;

    public bool Invert, Wireframe;

    /// <summary>Coloured by height through this map; null: in the image's own colours.</summary>
    public Colormap? Colors;
}

/// <summary>
/// Draws an image as a surface in 3D: a grid of vertices over each of its tiles, raised by a
/// grid of heights, with the tile's own pixels on it. The depth buffer is this renderer's, the
/// size of the back buffer, made when first needed. UI thread only.
/// </summary>
public sealed unsafe class SurfaceRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Matrix4x4 WorldToClip;
        public Vector4 ToWorldX, ToWorldZ, SlopeX, SlopeZ, TileRect, TileUv, TexSize, Window, Grid, Surface, HeightSize, Light, Backdrop;
        public uint Sharp, Gray, Channel, Flags;
    }

    // From the image's upper left, 45 degrees above the ground, as hill shading has it.
    static readonly Vector3 TowardsLight = Vector3.Normalize(new Vector3(-1, MathF.Sqrt(2), -1));

    readonly GraphicsDevice _gpu;
    ID3D11VertexShader* _vertexShader;
    ID3D11PixelShader* _pixelShader;
    ID3D11SamplerState* _imageSampler, _heightSampler;
    ID3D11Buffer* _constants;
    ID3D11RasterizerState* _solid, _wireframe;
    ID3D11DepthStencilState* _depthTest;
    ID3D11DepthStencilView* _depth;
    int _depthWidth, _depthHeight;
    ID3D11ShaderResourceView* _colors;
    Colormap _colorsOf;

    /// <summary>Video memory held by the depth buffer.</summary>
    public long ByteCount => 4L * _depthWidth * _depthHeight;

    public SurfaceRenderer(GraphicsDevice gpu)
    {
        _gpu = gpu;
        ID3D11Device* device = gpu.Device;

        ID3D11VertexShader* vertexShader;
        fixed (byte* code = Shaders.SurfaceVs)
            Hr.Check(device->CreateVertexShader(code, (nuint)Shaders.SurfaceVs.Length, (ID3D11ClassLinkage*)null, &vertexShader), "CreateVertexShader");
        _vertexShader = vertexShader;

        ID3D11PixelShader* pixelShader;
        fixed (byte* code = Shaders.SurfacePs)
            Hr.Check(device->CreatePixelShader(code, (nuint)Shaders.SurfacePs.Length, (ID3D11ClassLinkage*)null, &pixelShader), "CreatePixelShader");
        _pixelShader = pixelShader;

        // The image is seen at a slant: anisotropic filtering keeps it sharp there.
        D3D11_SAMPLER_DESC samplerDesc = default;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_ANISOTROPIC;
        samplerDesc.MaxAnisotropy = 8;
        samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = PInvoke.D3D11_FLOAT32_MAX;
        ID3D11SamplerState* sampler;
        Hr.Check(device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
        _imageSampler = sampler;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.MaxAnisotropy = 1;
        Hr.Check(device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
        _heightSampler = sampler;

        D3D11_BUFFER_DESC bufferDesc = default;
        bufferDesc.ByteWidth = (uint)sizeof(Constants);
        bufferDesc.Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC;
        bufferDesc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;
        bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE;
        ID3D11Buffer* constants;
        Hr.Check(device->CreateBuffer(&bufferDesc, (D3D11_SUBRESOURCE_DATA*)null, &constants), "CreateBuffer");
        _constants = constants;

        // Both sides are seen when the camera is low, and a mirrored orientation reverses the winding: nothing is culled.
        D3D11_RASTERIZER_DESC rasterizerDesc = default;
        rasterizerDesc.FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID;
        rasterizerDesc.CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE;
        rasterizerDesc.DepthClipEnable = true;
        rasterizerDesc.ScissorEnable = true;
        ID3D11RasterizerState* rasterizer;
        Hr.Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _solid = rasterizer;
        rasterizerDesc.FillMode = D3D11_FILL_MODE.D3D11_FILL_WIREFRAME;
        Hr.Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _wireframe = rasterizer;

        // Reversed depth: nearer is larger.
        D3D11_DEPTH_STENCIL_DESC depthDesc = default;
        depthDesc.DepthEnable = true;
        depthDesc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL;
        depthDesc.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_GREATER;
        ID3D11DepthStencilState* depthTest;
        Hr.Check(device->CreateDepthStencilState(&depthDesc, &depthTest), "CreateDepthStencilState");
        _depthTest = depthTest;
    }

    /// <summary>
    /// Draws the image as a surface into the rectangle (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="width"/>, <paramref name="height"/>) of the back buffer, which clip space
    /// then spans. <paramref name="heights"/> is the image's height grid on the GPU, one texture;
    /// without it the image lies flat. <paramref name="backdrop"/> shows through transparent pixels.
    /// </summary>
    public void Draw(GpuImage image, GpuImage? heights, in SurfaceSettings surface, in DisplaySettings display,
        int x, int y, int width, int height, Vector3 backdrop)
    {
        if (width <= 0 || height <= 0) return;
        EnsureDepth(_gpu.Width, _gpu.Height);
        ID3D11DeviceContext* context = _gpu.Context;
        context->ClearDepthStencilView(_depth, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 0f, 0);
        _gpu.BindBackBuffer(_depth);
        _gpu.SetViewport(x, y, width, height);
        var clip = new RECT { left = x, top = y, right = x + width, bottom = y + height };
        context->RSSetScissorRects(1, &clip);

        GpuImage.Tile? heightTile = heights is { Tiles.Count: > 0 } ? heights.Tiles[0] : null;
        // Flat, one cell does; but coloured by height, the vertices carry the heights even while they do not stand up yet.
        bool flat = heightTile == null || (surface.HeightScale == 0 && surface.Colors == null);
        if (surface.Colors is { } map) UseColors(map);

        // One vertex per height value at the finest; flat, one cell does.
        double longSide = Math.Max(image.Width, image.Height);
        double valueSize = heightTile is { } t ? (double)image.Width / t.TextureWidth : 1;
        double spacing = flat ? longSide : Math.Max(longSide / Math.Max(1, surface.MeshCells), valueSize);
        float level = flat ? 0 : (float)Math.Max(0, Math.Log2(spacing / valueSize));

        Affine2 g = surface.ImageToGround;
        double det = g.A * g.D - g.B * g.C;
        Constants constants = default;
        constants.WorldToClip = surface.WorldToClip;
        constants.ToWorldX = new Vector4((float)g.A, (float)g.C, (float)g.Tx, 0);
        constants.ToWorldZ = new Vector4((float)g.B, (float)g.D, (float)g.Ty, 0);
        // A slope turns with the ground by the inverse transpose, which for a turn or a mirroring is the transform itself.
        constants.SlopeX = new Vector4((float)(g.D / det), (float)(-g.B / det), 0, 0);
        constants.SlopeZ = new Vector4((float)(-g.C / det), (float)(g.A / det), 0, 0);
        constants.Window = display.WindowConstants();
        constants.Surface = new Vector4(flat ? 0 : surface.HeightScale, 1f / image.Width, 1f / image.Height, surface.Shading);
        constants.HeightSize = heightTile is { } h ? new Vector4(h.TextureWidth, h.TextureHeight, surface.HeightLevel, 0) : Vector4.One;
        constants.Light = new Vector4(TowardsLight, 1 / TowardsLight.Y);
        constants.Backdrop = new Vector4(backdrop, 1);
        constants.Sharp = display.Sharp ? 1u : 0u;
        constants.Gray = image.Layout is PixelLayout.Gray8 or PixelLayout.Gray16 or PixelLayout.GrayFloat ? 1u : 0u;
        constants.Channel = (uint)display.Channel;
        constants.Flags = (surface.Invert ? 1u : 0u) | (surface.Colors != null ? 2u : 0u);

        ID3D11Buffer* buffer = _constants;
        ID3D11SamplerState** samplers = stackalloc ID3D11SamplerState*[2] { _imageSampler, _heightSampler };
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->IASetInputLayout(null);
        context->VSSetShader(_vertexShader, (ID3D11ClassInstance**)null, 0);
        context->PSSetShader(_pixelShader, (ID3D11ClassInstance**)null, 0);
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->VSSetSamplers(0, 2, samplers);
        context->PSSetSamplers(0, 2, samplers);
        context->RSSetState(surface.Wireframe ? _wireframe : _solid);
        context->OMSetBlendState(null, (float*)null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(_depthTest, 0);

        ID3D11ShaderResourceView* heightView = heightTile is { } ht ? ht.View : null;
        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[3] { null, heightView, surface.Colors != null ? _colors : null };
        context->VSSetShaderResources(1, 1, &views[1]);

        foreach (GpuImage.Tile tile in image.Tiles)
        {
            // The grid is the image's; a tile draws the cells that reach into it, cut at its edges,
            // so that two tiles meet at vertices of the same height and leave no crack between them.
            int left = (int)Math.Floor(tile.DrawLeft / spacing), right = (int)Math.Ceiling(tile.DrawRight / spacing);
            int top = (int)Math.Floor(tile.DrawTop / spacing), bottom = (int)Math.Ceiling(tile.DrawBottom / spacing);
            if (right <= left || bottom <= top) continue;
            constants.TileRect = new Vector4(tile.DrawLeft, tile.DrawTop, tile.DrawRight, tile.DrawBottom);
            constants.TileUv = new Vector4(tile.X, tile.Y, 1f / tile.Width, 1f / tile.Height);
            constants.TexSize = new Vector4(tile.TextureWidth, tile.TextureHeight, 0, 0);
            constants.Grid = new Vector4((float)(left * spacing), (float)(top * spacing), (float)spacing, level);

            D3D11_MAPPED_SUBRESOURCE mapped;
            Hr.Check(context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
            *(Constants*)mapped.pData = constants;
            context->Unmap((ID3D11Resource*)buffer, 0);

            views[0] = tile.View;
            context->PSSetShaderResources(0, 3, views);
            context->DrawInstanced((uint)(2 * (right - left + 1)), (uint)(bottom - top), 0, 0);
        }

        views[0] = views[1] = views[2] = null;
        context->VSSetShaderResources(1, 1, &views[1]);
        context->PSSetShaderResources(0, 3, views);
        context->OMSetDepthStencilState(null, 0);
        _gpu.BindBackBuffer();
        _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
    }

    /// <summary>Has the colour map's table in a texture; made again only when another map is asked for.</summary>
    void UseColors(Colormap map)
    {
        if (_colors != null && _colorsOf == map) return;
        ID3D11ShaderResourceView* view = ColormapTexture.Create(_gpu.Device, map);
        if (_colors != null) _colors->Release();
        _colors = view;
        _colorsOf = map;
    }

    void EnsureDepth(int width, int height)
    {
        if (_depth != null && width == _depthWidth && height == _depthHeight) return;
        ReleaseTargets();
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)width;
        desc.Height = (uint)height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL;
        ID3D11Texture2D* texture;
        Hr.Check(_gpu.Device->CreateTexture2D(&desc, (D3D11_SUBRESOURCE_DATA*)null, &texture), "CreateTexture2D");
        ID3D11DepthStencilView* view;
        HRESULT hr = _gpu.Device->CreateDepthStencilView((ID3D11Resource*)texture, (D3D11_DEPTH_STENCIL_VIEW_DESC*)null, &view);
        texture->Release(); // the view keeps it alive
        Hr.Check(hr, "CreateDepthStencilView");
        _depth = view;
        _depthWidth = width;
        _depthHeight = height;
    }

    /// <summary>Gives back the depth buffer's video memory. It is made again by the next <see cref="Draw"/>.</summary>
    public void ReleaseTargets()
    {
        if (_depth != null) _depth->Release();
        _depth = null;
        _depthWidth = _depthHeight = 0;
    }

    public void Dispose()
    {
        ReleaseTargets();
        if (_colors != null) _colors->Release();
        if (_depthTest != null) _depthTest->Release();
        if (_wireframe != null) _wireframe->Release();
        if (_solid != null) _solid->Release();
        if (_constants != null) _constants->Release();
        if (_heightSampler != null) _heightSampler->Release();
        if (_imageSampler != null) _imageSampler->Release();
        if (_pixelShader != null) _pixelShader->Release();
        if (_vertexShader != null) _vertexShader->Release();
        _colors = null;
        _depthTest = null;
        _wireframe = _solid = null;
        _constants = null;
        _heightSampler = _imageSampler = null;
        _pixelShader = null;
        _vertexShader = null;
    }
}
