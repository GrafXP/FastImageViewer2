using System.Numerics;
using System.Runtime.InteropServices;
using Fiv.Comparing;
using Fiv.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>How two images in one pane are combined. The numbers are the shader's.</summary>
public enum MixKind
{
    /// <summary>The first image left of a divider, the second right of it.</summary>
    SplitColumns = 0,

    /// <summary>The first image above a divider, the second below it.</summary>
    SplitRows = 1,

    /// <summary>The second image over the first with an opacity.</summary>
    Blend = 2,

    /// <summary>The two images alternating in the cells of a checkerboard.</summary>
    Checker = 3,

    /// <summary>The second image less the first, in the colours of a colour map.</summary>
    Difference = 4,

    /// <summary>The first "image" is a similarity per pixel (1: the same); it is shown as 1 less it, in the colours of a colour map. There is no second.</summary>
    Heat = 5,
}

/// <summary>A way of combining two images and its parameters.</summary>
public struct MixSettings
{
    public MixKind Kind;

    /// <summary>
    /// By kind: the divider's position in pixels from the pane's left or top edge, the opacity
    /// 0..1, the cell size in pixels, or, for a difference and a similarity, the gain.
    /// </summary>
    public float Amount;

    /// <summary>For the checkerboard: the pane position its cells are centred on.</summary>
    public float OriginX, OriginY;

    /// <summary>For a difference: how it is turned into colours. Values are in the units the GPU samples.</summary>
    public DifferenceView Difference;

    /// <summary>For a difference and a similarity: the colours.</summary>
    public Colormap Colors;

    /// <summary>For a similarity: show it only where the second image, which is then drawn too, has pixels.</summary>
    public bool WithinSecond;
}

/// <summary>
/// Draws two images into one pane. Each goes, under its own transform, into a pane-sized
/// intermediate target that keeps the stored sample values (32-bit float per channel, so
/// 16-bit and float images lose nothing); a final pass reads both and writes what the compare
/// mode makes of them, through the display controls, into the back buffer. Placing an image
/// and combining two are thereby separate things, and a compare mode is a few lines of shader.
/// UI thread only.
/// </summary>
public sealed unsafe class CompareRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 Window, Checker, Backdrop, Pane, Amount;
        public uint Mode, Channel, Kind, Mask;
    }

    struct Target
    {
        public ID3D11RenderTargetView* Draw;
        public ID3D11ShaderResourceView* Read;
    }

    // Targets are made a little larger than asked for, so that dragging the window's edge does not remake them at every step.
    const int SizeStep = 128;

    readonly GraphicsDevice _gpu;
    readonly ViewRenderer _view;
    ID3D11VertexShader* _vertexShader;
    ID3D11PixelShader* _pixelShader;
    ID3D11Buffer* _constants;
    ID3D11RasterizerState* _rasterizer;
    Target _a, _b;
    int _width, _height;
    ID3D11ShaderResourceView* _colors;
    Colormap _colorsOf;

    /// <summary>Video memory held by the two intermediate targets.</summary>
    public long ByteCount => 2L * _width * _height * 16;

    public CompareRenderer(GraphicsDevice gpu, ViewRenderer view)
    {
        _gpu = gpu;
        _view = view;
        ID3D11Device* device = gpu.Device;

        ID3D11VertexShader* vertexShader;
        fixed (byte* code = Shaders.CombineVs)
            Hr.Check(device->CreateVertexShader(code, (nuint)Shaders.CombineVs.Length, (ID3D11ClassLinkage*)null, &vertexShader), "CreateVertexShader");
        _vertexShader = vertexShader;

        ID3D11PixelShader* pixelShader;
        fixed (byte* code = Shaders.CombinePs)
            Hr.Check(device->CreatePixelShader(code, (nuint)Shaders.CombinePs.Length, (ID3D11ClassLinkage*)null, &pixelShader), "CreatePixelShader");
        _pixelShader = pixelShader;

        D3D11_BUFFER_DESC bufferDesc = default;
        bufferDesc.ByteWidth = (uint)sizeof(Constants);
        bufferDesc.Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC;
        bufferDesc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;
        bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE;
        ID3D11Buffer* constants;
        Hr.Check(device->CreateBuffer(&bufferDesc, (D3D11_SUBRESOURCE_DATA*)null, &constants), "CreateBuffer");
        _constants = constants;

        D3D11_RASTERIZER_DESC rasterizerDesc = default;
        rasterizerDesc.FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID;
        rasterizerDesc.CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE;
        rasterizerDesc.DepthClipEnable = true;
        ID3D11RasterizerState* rasterizer;
        Hr.Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _rasterizer = rasterizer;
    }

    /// <summary>
    /// Draws the combination of two images into the pane at (<paramref name="paneX"/>, <paramref name="paneY"/>)
    /// of the back buffer. Each transform maps image pixel coordinates to pane pixel coordinates;
    /// each zoom is screen pixels per image pixel. An image that is null (not loaded yet) is
    /// treated as one without pixels: <paramref name="backdrop"/> shows in its place.
    /// </summary>
    public void Draw(GpuImage? a, in Affine2 aToPane, double zoomA, GpuImage? b, in Affine2 bToPane, double zoomB,
        in DisplaySettings display, in MixSettings mix, int paneX, int paneY, int paneWidth, int paneHeight, Vector3 backdrop)
    {
        if (paneWidth <= 0 || paneHeight <= 0) return;
        EnsureTargets(paneWidth, paneHeight);
        ID3D11DeviceContext* context = _gpu.Context;
        Fill(_a, a, aToPane, zoomA, display, paneWidth, paneHeight);
        Fill(_b, b, bToPane, zoomB, display, paneWidth, paneHeight);

        _gpu.BindBackBuffer();
        _gpu.SetViewport(paneX, paneY, paneWidth, paneHeight);

        Constants constants = default;
        constants.Window = display.WindowConstants();
        constants.Checker = DisplaySettings.CheckerConstants;
        constants.Backdrop = new Vector4(backdrop, 1);
        constants.Pane = new Vector4(paneX, paneY, paneWidth, paneHeight);
        constants.Amount = mix.Kind switch
        {
            MixKind.Checker => new Vector4(Math.Max(1, mix.Amount), mix.OriginX, mix.OriginY, 0),
            MixKind.Difference => new Vector4(mix.Difference.Scale, mix.Difference.Gain, mix.Difference.Floor, mix.Difference.Threshold),
            MixKind.Heat => new Vector4(mix.Amount, mix.WithinSecond ? 1 : 0, 0, 0),
            _ => new Vector4(mix.Amount, mix.OriginX, mix.OriginY, 0),
        };
        constants.Mode = (uint)mix.Kind;
        constants.Channel = (uint)(mix.Kind == MixKind.Difference ? mix.Difference.Channel : (int)display.Channel);
        constants.Kind = (uint)mix.Difference.Kind;
        constants.Mask = mix.Difference.Mask ? 1u : 0u;
        if (mix.Kind is MixKind.Difference or MixKind.Heat) UseColors(mix.Colors);

        ID3D11Buffer* buffer = _constants;
        D3D11_MAPPED_SUBRESOURCE mapped;
        Hr.Check(context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(Constants*)mapped.pData = constants;
        context->Unmap((ID3D11Resource*)buffer, 0);

        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->IASetInputLayout(null);
        context->VSSetShader(_vertexShader, (ID3D11ClassInstance**)null, 0);
        context->PSSetShader(_pixelShader, (ID3D11ClassInstance**)null, 0);
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->RSSetState(_rasterizer);
        context->OMSetBlendState(null, (float*)null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(null, 0);

        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[3] { _a.Read, _b.Read, _colors };
        context->PSSetShaderResources(0, 3, views);
        context->Draw(3, 0);

        // The targets must not stay bound for reading: the next frame draws into them again.
        views[0] = views[1] = views[2] = null;
        context->PSSetShaderResources(0, 3, views);
        _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
    }

    /// <summary>Has the colour map's table in a texture of 256 by 1; made again only when another map is asked for.</summary>
    void UseColors(Colormap map)
    {
        if (_colors != null && _colorsOf == map) return;
        ID3D11Device* device = _gpu.Device;
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = Colormaps.Size;
        desc.Height = 1;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;

        ID3D11Texture2D* texture;
        fixed (byte* table = Colormaps.Table(map))
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = table, SysMemPitch = Colormaps.Size * 4 };
            Hr.Check(device->CreateTexture2D(&desc, &data, &texture), "CreateTexture2D");
        }
        ID3D11ShaderResourceView* view;
        HRESULT hr = device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &view);
        texture->Release();
        Hr.Check(hr, "CreateShaderResourceView");
        if (_colors != null) _colors->Release();
        _colors = view;
        _colorsOf = map;
    }

    /// <summary>Clears a target to "no image here" and draws one image's stored values into it.</summary>
    void Fill(Target target, GpuImage? image, in Affine2 toPane, double zoom, in DisplaySettings display, int width, int height)
    {
        ID3D11DeviceContext* context = _gpu.Context;
        ID3D11RenderTargetView* view = target.Draw;
        context->OMSetRenderTargets(1, &view, (ID3D11DepthStencilView*)null);
        _gpu.SetViewport(0, 0, width, height);
        float* nothing = stackalloc float[4] { 0, 0, 0, -1 };
        context->ClearRenderTargetView(view, nothing);
        if (image != null) _view.Draw(image, toPane, zoom, display, 0, 0, width, height, width, height, stored: true);
    }

    void EnsureTargets(int width, int height)
    {
        int wantWidth = (width + SizeStep - 1) / SizeStep * SizeStep, wantHeight = (height + SizeStep - 1) / SizeStep * SizeStep;
        bool fits = width <= _width && height <= _height;
        if (fits && (long)_width * _height <= 2L * wantWidth * wantHeight) return; // large enough, and not wastefully large
        ReleaseTargets();
        _a = CreateTarget(wantWidth, wantHeight);
        _b = CreateTarget(wantWidth, wantHeight);
        _width = wantWidth;
        _height = wantHeight;
    }

    Target CreateTarget(int width, int height)
    {
        ID3D11Device* device = _gpu.Device;
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)width;
        desc.Height = (uint)height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;

        ID3D11Texture2D* texture;
        Hr.Check(device->CreateTexture2D(&desc, (D3D11_SUBRESOURCE_DATA*)null, &texture), "CreateTexture2D");
        ID3D11RenderTargetView* draw = null;
        ID3D11ShaderResourceView* read = null;
        HRESULT hr = device->CreateRenderTargetView((ID3D11Resource*)texture, (D3D11_RENDER_TARGET_VIEW_DESC*)null, &draw);
        if (hr.Succeeded) hr = device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &read);
        texture->Release(); // the views keep it alive
        if (hr.Failed && draw != null) draw->Release();
        Hr.Check(hr, "CreateRenderTargetView");
        return new Target { Draw = draw, Read = read };
    }

    /// <summary>Gives back the video memory of the two targets. They are made again by the next <see cref="Draw"/>.</summary>
    public void ReleaseTargets()
    {
        if (_a.Draw != null) _a.Draw->Release();
        if (_a.Read != null) _a.Read->Release();
        if (_b.Draw != null) _b.Draw->Release();
        if (_b.Read != null) _b.Read->Release();
        _a = _b = default;
        _width = _height = 0;
    }

    public void Dispose()
    {
        ReleaseTargets();
        if (_colors != null) _colors->Release();
        _colors = null;
        if (_rasterizer != null) _rasterizer->Release();
        if (_constants != null) _constants->Release();
        if (_pixelShader != null) _pixelShader->Release();
        if (_vertexShader != null) _vertexShader->Release();
        _rasterizer = null;
        _constants = null;
        _pixelShader = null;
        _vertexShader = null;
    }
}
