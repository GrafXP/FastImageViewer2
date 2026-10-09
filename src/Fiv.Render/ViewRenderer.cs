using System.Numerics;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Fiv.Render;

/// <summary>How sample values become screen brightness. Applied in the shader; the pixels stay as decoded.</summary>
public struct DisplaySettings
{
    /// <summary>Sample values shown as black and as white, in the units the GPU samples (0..1 for integer formats).</summary>
    public float WindowLow, WindowHigh;

    public float Gamma;

    /// <summary>Crisp pixel edges when magnified, instead of bilinear smoothing.</summary>
    public bool Sharp;

    public static DisplaySettings Default => new() { WindowLow = 0, WindowHigh = 1, Gamma = 1, Sharp = true };
}

/// <summary>Draws a <see cref="GpuImage"/> into the bound render target under an affine transform.</summary>
public sealed unsafe class ViewRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 ClipX, ClipY, TileRect, TileUv, TexSize, Window, Checker;
        public uint Sharp, Gray, Unused0, Unused1;
    }

    readonly GraphicsDevice _gpu;
    ID3D11VertexShader* _vertexShader;
    ID3D11PixelShader* _pixelShader;
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
        ID3D11RasterizerState* rasterizer;
        Hr.Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _rasterizer = rasterizer;
    }

    /// <summary>
    /// Draws the image. <paramref name="imageToScreen"/> maps image pixel coordinates to
    /// back-buffer pixel coordinates; <paramref name="zoom"/> is screen pixels per image pixel.
    /// </summary>
    public void Draw(GpuImage image, in Affine2 imageToScreen, double zoom, in DisplaySettings display)
    {
        ID3D11DeviceContext* context = _gpu.Context;
        double sx = 2.0 / _gpu.Width, sy = 2.0 / _gpu.Height;
        Affine2 m = imageToScreen;

        Constants constants = default;
        constants.ClipX = new Vector4((float)(m.A * sx), (float)(m.C * sx), (float)(m.Tx * sx - 1), 0);
        constants.ClipY = new Vector4((float)(-m.B * sy), (float)(-m.D * sy), (float)(1 - m.Ty * sy), 0);
        float span = display.WindowHigh - display.WindowLow;
        constants.Window = new Vector4(display.WindowLow, 1f / (MathF.Abs(span) > 1e-12f ? span : 1e-12f),
            1f / Math.Max(display.Gamma, 1e-3f), 0);
        constants.Checker = new Vector4(8, 0.36f, 0.48f, 0);
        constants.Gray = image.Layout is PixelLayout.Gray8 or PixelLayout.Gray16 or PixelLayout.GrayFloat ? 1u : 0u;

        ID3D11Buffer* buffer = _constants;
        ID3D11SamplerState* sampler = _sampler;
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->IASetInputLayout(null);
        context->VSSetShader(_vertexShader, (ID3D11ClassInstance**)null, 0);
        context->PSSetShader(_pixelShader, (ID3D11ClassInstance**)null, 0);
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
        if (_pixelShader != null) _pixelShader->Release();
        if (_vertexShader != null) _vertexShader->Release();
        _rasterizer = null;
        _constants = null;
        _sampler = null;
        _pixelShader = null;
        _vertexShader = null;
    }
}
