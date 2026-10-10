using System.Numerics;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Fiv.Render;

/// <summary>Which part of a pixel is shown. The numbers are the shader's.</summary>
public enum ChannelView
{
    Color = 0,
    Red = 1,
    Green = 2,
    Blue = 3,
    Alpha = 4,
    Luma = 5,
}

/// <summary>
/// How sample values become screen brightness. Applied in the shader; the pixels stay as decoded.
/// One setting serves every image on screen, so that compared images are always shown alike.
/// </summary>
public struct DisplaySettings
{
    /// <summary>Sample values shown as black and as white, in the units the GPU samples (0..1 for integer formats).</summary>
    public float WindowLow, WindowHigh;

    public float Gamma;

    /// <summary>Brightness in stops: after the window, values are multiplied by two to this power.</summary>
    public float Exposure;

    public ChannelView Channel;

    /// <summary>Crisp pixel edges when magnified, instead of bilinear smoothing.</summary>
    public bool Sharp;

    public static DisplaySettings Default => new() { WindowLow = 0, WindowHigh = 1, Gamma = 1, Sharp = true };

    /// <summary>Window, gamma and exposure as the shaders take them: low, 1 / (high - low), 1 / gamma, gain.</summary>
    internal readonly Vector4 WindowConstants()
    {
        float span = WindowHigh - WindowLow;
        return new Vector4(WindowLow, 1f / (MathF.Abs(span) > 1e-12f ? span : 1e-12f), 1f / Math.Max(Gamma, 1e-3f), MathF.Pow(2, Exposure));
    }

    /// <summary>The checkerboard behind transparent pixels: cell size in screen pixels and its two grey levels.</summary>
    internal static Vector4 CheckerConstants => new(8, 0.36f, 0.48f, 0);
}

/// <summary>Draws a <see cref="GpuImage"/> into the bound render target under an affine transform.</summary>
public sealed unsafe class ViewRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 ClipX, ClipY, TileRect, TileUv, TexSize, Window, Checker;
        public uint Sharp, Gray, Channel, Unused;
    }

    readonly GraphicsDevice _gpu;
    ID3D11VertexShader* _vertexShader;
    ID3D11PixelShader* _pixelShader;
    ID3D11PixelShader* _rawShader;
    ID3D11SamplerState* _sampler;
    ID3D11Buffer* _constants;
    ID3D11RasterizerState* _rasterizer;

    public ViewRenderer(GraphicsDevice gpu)
    {
        _gpu = gpu;
        ID3D11Device* device = gpu.Device;

        ID3D11VertexShader* vertexShader;
        fixed (byte* code = Shaders.ViewVs)
            Hr.Check(device->CreateVertexShader(code, (nuint)Shaders.ViewVs.Length, (ID3D11ClassLinkage*)null, &vertexShader), "CreateVertexShader");
        _vertexShader = vertexShader;

        ID3D11PixelShader* pixelShader;
        fixed (byte* code = Shaders.ViewPs)
            Hr.Check(device->CreatePixelShader(code, (nuint)Shaders.ViewPs.Length, (ID3D11ClassLinkage*)null, &pixelShader), "CreatePixelShader");
        _pixelShader = pixelShader;

        fixed (byte* code = Shaders.ViewRawPs)
            Hr.Check(device->CreatePixelShader(code, (nuint)Shaders.ViewRawPs.Length, (ID3D11ClassLinkage*)null, &pixelShader), "CreatePixelShader");
        _rawShader = pixelShader;

        D3D11_SAMPLER_DESC samplerDesc = default;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = PInvoke.D3D11_FLOAT32_MAX;
        ID3D11SamplerState* sampler;
        Hr.Check(device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
        _sampler = sampler;

        D3D11_BUFFER_DESC bufferDesc = default;
        bufferDesc.ByteWidth = (uint)sizeof(Constants);
        bufferDesc.Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC;
        bufferDesc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;
        bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE;
        ID3D11Buffer* constants;
        Hr.Check(device->CreateBuffer(&bufferDesc, (D3D11_SUBRESOURCE_DATA*)null, &constants), "CreateBuffer");
        _constants = constants;

        // Mirrored orientations reverse the winding, so nothing may be culled.
        D3D11_RASTERIZER_DESC rasterizerDesc = default;
        rasterizerDesc.FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID;
        rasterizerDesc.CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE;
        rasterizerDesc.DepthClipEnable = true;
        rasterizerDesc.ScissorEnable = true;
        ID3D11RasterizerState* rasterizer;
        Hr.Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _rasterizer = rasterizer;
    }

    /// <summary>
    /// Draws the image into the back buffer. <paramref name="imageToScreen"/> maps image pixel
    /// coordinates to back-buffer pixel coordinates; <paramref name="zoom"/> is screen pixels per image pixel.
    /// Nothing is drawn outside the rectangle (<paramref name="clipX"/>, <paramref name="clipY"/>,
    /// <paramref name="clipWidth"/>, <paramref name="clipHeight"/>), the part of the window the image has.
    /// </summary>
    public void Draw(GpuImage image, in Affine2 imageToScreen, double zoom, in DisplaySettings display,
        int clipX, int clipY, int clipWidth, int clipHeight) =>
        Draw(image, imageToScreen, zoom, display, clipX, clipY, clipWidth, clipHeight, _gpu.Width, _gpu.Height, stored: false);

    /// <summary>
    /// Draws the image into the bound render target, whose viewport is <paramref name="targetWidth"/>
    /// by <paramref name="targetHeight"/> pixels. With <paramref name="stored"/> the sample values
    /// are written as they are, for a later pass to combine, instead of what the display controls make of them.
    /// </summary>
    internal void Draw(GpuImage image, in Affine2 imageToTarget, double zoom, in DisplaySettings display,
        int clipX, int clipY, int clipWidth, int clipHeight, int targetWidth, int targetHeight, bool stored)
    {
        ID3D11DeviceContext* context = _gpu.Context;
        var clip = new Windows.Win32.Foundation.RECT { left = clipX, top = clipY, right = clipX + clipWidth, bottom = clipY + clipHeight };
        context->RSSetScissorRects(1, &clip);
        double sx = 2.0 / targetWidth, sy = 2.0 / targetHeight;
        Affine2 m = imageToTarget;

        Constants constants = default;
        constants.ClipX = new Vector4((float)(m.A * sx), (float)(m.C * sx), (float)(m.Tx * sx - 1), 0);
        constants.ClipY = new Vector4((float)(-m.B * sy), (float)(-m.D * sy), (float)(1 - m.Ty * sy), 0);
        constants.Window = display.WindowConstants();
        constants.Checker = DisplaySettings.CheckerConstants;
        constants.Gray = image.Layout is PixelLayout.Gray8 or PixelLayout.Gray16 or PixelLayout.GrayFloat ? 1u : 0u;
        constants.Channel = (uint)display.Channel;

        ID3D11Buffer* buffer = _constants;
        ID3D11SamplerState* sampler = _sampler;
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->IASetInputLayout(null);
        context->VSSetShader(_vertexShader, (ID3D11ClassInstance**)null, 0);
        context->PSSetShader(stored ? _rawShader : _pixelShader, (ID3D11ClassInstance**)null, 0);
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->PSSetSamplers(0, 1, &sampler);
        context->RSSetState(_rasterizer);
        context->OMSetBlendState(null, (float*)null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(null, 0);

        foreach (GpuImage.Tile tile in image.Tiles)
        {
            // Texels per screen pixel decides the sampling: crisp edges only when magnifying.
            double texelsPerImagePixel = (double)tile.TextureWidth / tile.Width;
            constants.Sharp = display.Sharp && zoom / texelsPerImagePixel > 1.0 ? 1u : 0u;
            constants.TileRect = new Vector4(tile.DrawLeft, tile.DrawTop, tile.DrawRight, tile.DrawBottom);
            constants.TileUv = new Vector4(tile.X, tile.Y, 1f / tile.Width, 1f / tile.Height);
            constants.TexSize = new Vector4(tile.TextureWidth, tile.TextureHeight, 0, 0);

            D3D11_MAPPED_SUBRESOURCE mapped;
            Hr.Check(context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
            *(Constants*)mapped.pData = constants;
            context->Unmap((ID3D11Resource*)buffer, 0);

            ID3D11ShaderResourceView* view = tile.View;
            context->PSSetShaderResources(0, 1, &view);
            context->Draw(4, 0);
        }
    }

    public void Dispose()
    {
        if (_rasterizer != null) _rasterizer->Release();
        if (_constants != null) _constants->Release();
        if (_sampler != null) _sampler->Release();
        if (_rawShader != null) _rawShader->Release();
        if (_pixelShader != null) _pixelShader->Release();
        if (_vertexShader != null) _vertexShader->Release();
        _rasterizer = null;
        _constants = null;
        _sampler = null;
        _rawShader = null;
        _pixelShader = null;
        _vertexShader = null;
    }
}
