using System.Numerics;
using System.Runtime.InteropServices;
using Fiv.Comparing;
using Fiv.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>How images in one pane are combined. The numbers are the shader's.</summary>
public enum MixKind
{
    /// <summary>The images side by side, each in a strip of its own between dividers.</summary>
    SplitColumns = 0,

    /// <summary>The images above each other, each in a strip of its own between dividers.</summary>
    SplitRows = 1,

    /// <summary>Each image over those before it, with an opacity of its own; the first over the backdrop.</summary>
    Blend = 2,

    /// <summary>The images taking turns in the cells of a checkerboard.</summary>
    Checker = 3,

    /// <summary>The second image less the first, in the colours of a colour map.</summary>
    Difference = 4,

    /// <summary>The first "image" is a similarity per pixel (1: the same); it is shown as 1 less it, in the colours of a colour map. There is no second.</summary>
    Heat = 5,
}

public static class MixKindExtensions
{
    /// <summary>True for the kinds that take any number of images, a pass each; the others take two at once.</summary>
    public static bool IsStack(this MixKind kind) => kind <= MixKind.Checker;
}

/// <summary>
/// One image combined into a pane: its texture (null if it is not loaded yet: it then has no
/// pixels), the transform from its pixels to the pane's, and its zoom in screen pixels per
/// image pixel. <see cref="Amount"/> is, for a split, where its strip ends, in pane pixels from
/// the left or top edge (the last image's is not used); for a blend, its opacity 0..1 over the
/// images before it, and the first image's over the backdrop.
/// </summary>
public readonly record struct MixLayer(GpuImage? Image, Affine2 ToPane, double Zoom, float Amount = 0);

/// <summary>A way of combining images and its parameters.</summary>
public struct MixSettings
{
    public MixKind Kind;

    /// <summary>By kind: the cell size in pixels, or, for a difference and a similarity, the gain.</summary>
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
/// Draws images combined into one pane. Each goes, under its own transform, into a pane-sized
/// intermediate target that keeps the stored sample values (32-bit float per channel, so
/// 16-bit and float images lose nothing); a final pass reads it and writes what the compare
/// mode makes of it, through the display controls, into the back buffer. Placing an image
/// and combining images are thereby separate things, and a compare mode is a few lines of shader.
///
/// A difference and a similarity read two targets in one pass. A split, a blend and a
/// checkerboard take any number of images through one target, a pass each: each pass draws
/// its image's strip or cells, or lies it over the passes before it by its opacity, which the
/// blending of the GPU applies. Their memory does not grow with the number of images.
/// UI thread only.
/// </summary>
public sealed unsafe class CompareRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 Window, Checker, Backdrop, Pane, Amount;
        public uint Mode, Channel, Kind, Mask;
        public uint Layer, Layers, Unused0, Unused1;
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
    ID3D11BlendState* _over; // the blend's: what a pass draws, by the opacity given, over what is there
    Target _a, _b;
    int _width, _height;
    ID3D11ShaderResourceView* _colors;
    Colormap _colorsOf;

    /// <summary>Video memory held by the intermediate targets: the second is made only for the modes that read two at once.</summary>
    public long ByteCount => (_b.Draw != null ? 2L : 1L) * _width * _height * 16;

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

        // The blend factor is the opacity, for the colours and for the heights of the 3D canvas
        // alike. Blending 32-bit float targets is asked rather than assumed: without it the
        // heights are those of the image on top.
        uint support;
        bool heightsBlend = device->CheckFormatSupport(DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, &support).Succeeded &&
            (support & (uint)D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_BLENDABLE) != 0;
        D3D11_BLEND_DESC blendDesc = default;
        blendDesc.IndependentBlendEnable = !heightsBlend;
        ref D3D11_RENDER_TARGET_BLEND_DESC colors = ref blendDesc.RenderTarget._0;
        colors.BlendEnable = true;
        colors.SrcBlend = colors.SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_BLEND_FACTOR;
        colors.DestBlend = colors.DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_BLEND_FACTOR;
        colors.BlendOp = colors.BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        colors.RenderTargetWriteMask = 0x0F;
        blendDesc.RenderTarget._1 = colors;
        blendDesc.RenderTarget._1.BlendEnable = heightsBlend;
        ID3D11BlendState* over;
        Hr.Check(device->CreateBlendState(&blendDesc, &over), "CreateBlendState");
        _over = over;
    }

    /// <summary>
    /// Draws the combination of images into the pane at (<paramref name="paneX"/>, <paramref name="paneY"/>)
    /// of the back buffer: for a split, a blend or a checkerboard all of <paramref name="layers"/>,
    /// in their order; for a difference or a similarity the first two. An image that is null (not
    /// loaded yet) is treated as one without pixels: <paramref name="backdrop"/> shows in its place.
    /// </summary>
    public void Draw(ReadOnlySpan<MixLayer> layers, in DisplaySettings display, in MixSettings mix, int paneX, int paneY, int paneWidth, int paneHeight, Vector3 backdrop)
    {
        if (paneWidth <= 0 || paneHeight <= 0) return;
        Run(layers, display, mix, null, paneX, paneY, paneWidth, paneHeight, backdrop);
    }

    /// <summary>
    /// The same into a canvas laid flat for the 3D height map: each transform maps image pixels
    /// to texels of the canvas, which the strips and cells are given in too. Besides the
    /// colours, the combining pass writes how high each point stands; then the mip levels are made.
    /// </summary>
    public void DrawFlat(ReadOnlySpan<MixLayer> layers, in DisplaySettings display, in MixSettings mix, SurfaceCanvas canvas, Vector3 backdrop)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return;
        Run(layers, display, mix, canvas, 0, 0, canvas.Width, canvas.Height, backdrop);
        _gpu.BindBackBuffer(); // the canvas may be read now
        canvas.MakeMips();
    }

    /// <summary>The passes, into the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>) of the back buffer or of the canvas.</summary>
    void Run(ReadOnlySpan<MixLayer> layers, in DisplaySettings display, in MixSettings mix, SurfaceCanvas? canvas, int x, int y, int width, int height, Vector3 backdrop)
    {
        if (!mix.Kind.IsStack())
        {
            EnsureTargets(width, height, two: true);
            Fill(_a, layers.Length > 0 ? layers[0] : default, display, width, height);
            Fill(_b, layers.Length > 1 ? layers[1] : default, display, width, height);
            BindOutput(canvas);
            Combine(display, mix, 0, 2, Vector2.Zero, 1, x, y, width, height, backdrop);
            return;
        }
        EnsureTargets(width, height, two: false);
        float start = 0;
        for (int k = 0; k < layers.Length; k++)
        {
            if (mix.Kind == MixKind.Blend)
            {
                // The first image of a blend over the backdrop by its opacity, which its pass works in; each one
                // after it over those before, which the blend unit does. One that adds nothing is not drawn.
                float opacity = Math.Clamp(layers[k].Amount, 0, 1);
                if (k > 0 && opacity <= 0) continue;
                Fill(_a, layers[k], display, width, height);
                BindOutput(canvas);
                Combine(display, mix, k, layers.Length, new Vector2(k == 0 ? opacity : 1, 0), k == 0 ? 1 : opacity, x, y, width, height, backdrop);
                continue;
            }
            Fill(_a, layers[k], display, width, height);
            BindOutput(canvas);
            Combine(display, mix, k, layers.Length, new Vector2(start, layers[k].Amount), 1, x, y, width, height, backdrop);
            start = layers[k].Amount;
        }
    }

    void BindOutput(SurfaceCanvas? canvas)
    {
        if (canvas != null) canvas.Bind();
        else _gpu.BindBackBuffer();
    }

    /// <summary>
    /// A final pass, into the rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>)
    /// of whatever is bound: of image <paramref name="layer"/> of <paramref name="layers"/> for
    /// the kinds that take a pass each, <paramref name="amount"/> being its strip of a split, or
    /// of a blend the first image's opacity over the backdrop. With an <paramref name="opacity"/>
    /// below 1 the pass is blended over what is there.
    /// </summary>
    void Combine(in DisplaySettings display, in MixSettings mix, int layer, int layers, Vector2 amount, float opacity, int x, int y, int width, int height, Vector3 backdrop)
    {
        ID3D11DeviceContext* context = _gpu.Context;
        _gpu.SetViewport(x, y, width, height);

        Constants constants = default;
        constants.Window = display.WindowConstants();
        constants.Checker = DisplaySettings.CheckerConstants;
        constants.Backdrop = new Vector4(backdrop, 1);
        constants.Pane = new Vector4(x, y, width, height);
        constants.Amount = mix.Kind switch
        {
            MixKind.SplitColumns or MixKind.SplitRows or MixKind.Blend => new Vector4(amount, 0, 0),
            MixKind.Checker => new Vector4(Math.Max(1, mix.Amount), mix.OriginX, mix.OriginY, 0),
            MixKind.Difference => new Vector4(mix.Difference.Scale, mix.Difference.Gain, mix.Difference.Floor, mix.Difference.Threshold),
            MixKind.Heat => new Vector4(mix.Amount, mix.WithinSecond ? 1 : 0, 0, 0),
            _ => default,
        };
        constants.Mode = (uint)mix.Kind;
        constants.Channel = (uint)(mix.Kind == MixKind.Difference ? mix.Difference.Channel : (int)display.Channel);
        constants.Kind = (uint)mix.Difference.Kind;
        constants.Mask = mix.Difference.Mask ? 1u : 0u;
        constants.Layer = (uint)layer;
        constants.Layers = (uint)layers;
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
        float* factor = stackalloc float[4] { opacity, opacity, opacity, opacity };
        context->OMSetBlendState(opacity < 1 ? _over : null, factor, 0xFFFFFFFF);
        context->OMSetDepthStencilState(null, 0);

        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[3] { _a.Read, _b.Read, _colors };
        context->PSSetShaderResources(0, 3, views);
        context->Draw(3, 0);

        // The targets must not stay bound for reading: the next pass or frame draws into them again.
        views[0] = views[1] = views[2] = null;
        context->PSSetShaderResources(0, 3, views);
        context->OMSetBlendState(null, (float*)null, 0xFFFFFFFF);
        _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
    }

    /// <summary>Has the colour map's table in a texture of 256 by 1; made again only when another map is asked for.</summary>
    void UseColors(Colormap map)
    {
        if (_colors != null && _colorsOf == map) return;
        ID3D11ShaderResourceView* view = ColormapTexture.Create(_gpu.Device, map);
        if (_colors != null) _colors->Release();
        _colors = view;
        _colorsOf = map;
    }

    /// <summary>Clears a target to "no image here" and draws one image's stored values into it.</summary>
    void Fill(Target target, in MixLayer layer, in DisplaySettings display, int width, int height)
    {
        ID3D11DeviceContext* context = _gpu.Context;
        ID3D11RenderTargetView* view = target.Draw;
        context->OMSetRenderTargets(1, &view, (ID3D11DepthStencilView*)null);
        _gpu.SetViewport(0, 0, width, height);
        float* nothing = stackalloc float[4] { 0, 0, 0, -1 };
        context->ClearRenderTargetView(view, nothing);
        if (layer.Image != null) _view.Draw(layer.Image, layer.ToPane, layer.Zoom, display, 0, 0, width, height, width, height, stored: true);
    }

    /// <summary>Has the first target, and with <paramref name="two"/> the second, large enough for this size.</summary>
    void EnsureTargets(int width, int height, bool two)
    {
        int wantWidth = (width + SizeStep - 1) / SizeStep * SizeStep, wantHeight = (height + SizeStep - 1) / SizeStep * SizeStep;
        bool fits = width <= _width && height <= _height;
        if (!fits || (long)_width * _height > 2L * wantWidth * wantHeight) // too small, or wastefully large
        {
            ReleaseTargets();
            _a = CreateTarget(wantWidth, wantHeight);
            _width = wantWidth;
            _height = wantHeight;
        }
        if (two && _b.Draw == null) _b = CreateTarget(_width, _height);
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

    /// <summary>Gives back the video memory of the targets. They are made again by the next <see cref="Draw"/>.</summary>
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
        if (_over != null) _over->Release();
        if (_rasterizer != null) _rasterizer->Release();
        if (_constants != null) _constants->Release();
        if (_pixelShader != null) _pixelShader->Release();
        if (_vertexShader != null) _vertexShader->Release();
        _over = null;
        _rasterizer = null;
        _constants = null;
        _pixelShader = null;
        _vertexShader = null;
    }
}
