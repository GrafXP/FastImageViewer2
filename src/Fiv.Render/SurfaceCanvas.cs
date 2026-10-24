using Fiv.Imaging;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>
/// A view that combines two images, laid flat for the 3D height map: what the view shows, over
/// the whole canvas of the comparison, in one texture, and how high each point of it stands in
/// another. The compare renderer draws into both at once (<see cref="CompareRenderer.DrawFlat"/>);
/// the surface renderer raises <see cref="Colors"/> by <see cref="Heights"/>. Both have mip
/// levels, made again after every drawing. UI thread only.
/// </summary>
public sealed unsafe class SurfaceCanvas : IDisposable
{
    readonly GraphicsDevice _gpu;
    ID3D11RenderTargetView* _colorTarget, _heightTarget;
    ID3D11ShaderResourceView* _colorView, _heightView;
    bool _heightMips;

    /// <summary>The size of the textures, in texels.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The colours shown, as an image the size of the canvas.</summary>
    public GpuImage? Colors { get; private set; }

    /// <summary>The heights 0..1, as an image the size of the canvas.</summary>
    public GpuImage? Heights { get; private set; }

    /// <summary>Video memory held by the two textures and their mip levels.</summary>
    public long ByteCount => (long)Width * Height * (4 + 4) * 4 / 3;

    public SurfaceCanvas(GraphicsDevice gpu)
    {
        _gpu = gpu;
        // Mip levels of float textures made by the GPU: on every card this viewer runs on, but asked rather than assumed.
        uint support;
        _heightMips = gpu.Device->CheckFormatSupport(DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, &support).Succeeded &&
            (support & (uint)D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_MIP_AUTOGEN) != 0;
    }

    /// <summary>Has textures of this size, for a canvas of that size: made again only when either changes.</summary>
    public void Ensure(int width, int height, int canvasWidth, int canvasHeight)
    {
        if (_colorView != null && width == Width && height == Height && Colors!.Width == canvasWidth && Colors.Height == canvasHeight) return;
        if (_colorView == null || width != Width || height != Height)
        {
            Release();
            CreateTarget(width, height, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, mips: true, out _colorTarget, out _colorView);
            CreateTarget(width, height, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, _heightMips, out _heightTarget, out _heightView);
            Width = width;
            Height = height;
        }
        Colors?.Dispose();
        Heights?.Dispose();
        Colors = GpuImage.Wrap(_colorView, canvasWidth, canvasHeight, width, height, PixelLayout.Bgra8);
        Heights = GpuImage.Wrap(_heightView, canvasWidth, canvasHeight, width, height, PixelLayout.GrayFloat);
    }

    void CreateTarget(int width, int height, DXGI_FORMAT format, bool mips, out ID3D11RenderTargetView* target, out ID3D11ShaderResourceView* view)
    {
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)width;
        desc.Height = (uint)height;
        desc.MipLevels = mips ? 0u : 1u; // 0: all of them
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = mips ? D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;

        ID3D11Texture2D* texture;
        Hr.Check(_gpu.Device->CreateTexture2D(&desc, (D3D11_SUBRESOURCE_DATA*)null, &texture), "CreateTexture2D");
        ID3D11RenderTargetView* draw = null;
        ID3D11ShaderResourceView* read = null;
        HRESULT hr = _gpu.Device->CreateRenderTargetView((ID3D11Resource*)texture, (D3D11_RENDER_TARGET_VIEW_DESC*)null, &draw);
        if (hr.Succeeded) hr = _gpu.Device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &read);
        texture->Release(); // the views keep it alive
        if (hr.Failed && draw != null) draw->Release();
        Hr.Check(hr, "CreateRenderTargetView");
        target = draw;
        view = read;
    }

    /// <summary>Binds both textures for drawing, colours first.</summary>
    internal void Bind()
    {
        ID3D11RenderTargetView** targets = stackalloc ID3D11RenderTargetView*[2] { _colorTarget, _heightTarget };
        _gpu.Context->OMSetRenderTargets(2, targets, (ID3D11DepthStencilView*)null);
    }

    /// <summary>The mip levels from what was drawn. The textures must no longer be bound for drawing.</summary>
    internal void MakeMips()
    {
        _gpu.Context->GenerateMips(_colorView);
        if (_heightMips) _gpu.Context->GenerateMips(_heightView);
    }

    /// <summary>Gives back the video memory. The next <see cref="Ensure"/> makes the textures again.</summary>
    public void Release()
    {
        Colors?.Dispose();
        Heights?.Dispose();
        Colors = Heights = null;
        if (_colorTarget != null) _colorTarget->Release();
        if (_colorView != null) _colorView->Release();
        if (_heightTarget != null) _heightTarget->Release();
        if (_heightView != null) _heightView->Release();
        _colorTarget = _heightTarget = null;
        _colorView = _heightView = null;
        Width = Height = 0;
    }

    public void Dispose() => Release();
}
