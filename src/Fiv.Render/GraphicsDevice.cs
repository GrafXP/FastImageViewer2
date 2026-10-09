using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Fiv.Render;

/// <summary>
/// The Direct3D 11 device and the window's swap chain. The device is created without a window
/// (on any thread, since that is the slow part of start-up); the swap chain is attached on the
/// UI thread. Textures may be created through <see cref="Device"/> from any thread;
/// <see cref="Context"/> belongs to the UI thread.
/// </summary>
public sealed unsafe class GraphicsDevice : IDisposable
{
    public ID3D11Device* Device { get; private set; }
    public ID3D11DeviceContext* Context { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasSwapChain => _swapChain != null;

    IDXGISwapChain1* _swapChain;
    ID3D11RenderTargetView* _backBuffer;

    GraphicsDevice()
    {
    }

    public static GraphicsDevice Create()
    {
        var gpu = new GraphicsDevice();
        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        HRESULT hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, PInvoke.D3D11_SDK_VERSION,
            &device, null, &context);
        if (hr.Failed)
        {
            // No usable GPU or driver: the software rasteriser keeps the viewer working.
            hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, PInvoke.D3D11_SDK_VERSION,
                &device, null, &context);
        }
        Hr.Check(hr, "D3D11CreateDevice");
        gpu.Device = device;
        gpu.Context = context;
        return gpu;
    }

    /// <summary>Creates the swap chain for the window. UI thread.</summary>
    public void AttachWindow(HWND window, int width, int height)
    {
        IDXGIDevice1* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        try
        {
            Guid iid = IDXGIDevice1.IID_Guid;
            Hr.Check(Device->QueryInterface(&iid, (void**)&dxgiDevice), "QueryInterface");
            Hr.Check(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
            iid = IDXGIFactory2.IID_Guid;
            Hr.Check(adapter->GetParent(&iid, (void**)&factory), "GetParent");

            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            DXGI_SWAP_CHAIN_DESC1 desc = default;
            desc.Width = (uint)Width;
            desc.Height = (uint)Height;
            desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;
            IDXGISwapChain1* swapChain;
            Hr.Check(factory->CreateSwapChainForHwnd((IUnknown*)Device, window, &desc,
                (DXGI_SWAP_CHAIN_FULLSCREEN_DESC*)null, null, &swapChain), "CreateSwapChainForHwnd");
            _swapChain = swapChain;
            factory->MakeWindowAssociation(window, DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER);
            dxgiDevice->SetMaximumFrameLatency(1);
            CreateBackBufferView();
        }
        finally
        {
            if (factory != null) factory->Release();
            if (adapter != null) adapter->Release();
            if (dxgiDevice != null) dxgiDevice->Release();
        }
    }

    void CreateBackBufferView()
    {
        ID3D11Texture2D* texture;
        Guid iid = ID3D11Texture2D.IID_Guid;
        Hr.Check(_swapChain->GetBuffer(0, &iid, (void**)&texture), "GetBuffer");
        ID3D11RenderTargetView* view;
        HRESULT hr = Device->CreateRenderTargetView((ID3D11Resource*)texture, (D3D11_RENDER_TARGET_VIEW_DESC*)null, &view);
        texture->Release();
        Hr.Check(hr, "CreateRenderTargetView");
        _backBuffer = view;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_swapChain == null || (width == Width && height == Height)) return;
        Context->OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        _backBuffer->Release();
        _backBuffer = null;
        Hr.Check(_swapChain->ResizeBuffers(0, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers");
        Width = width;
        Height = height;
        CreateBackBufferView();
    }

    /// <summary>Binds the back buffer with a viewport covering it and clears it.</summary>
    public void BeginFrame(float red, float green, float blue)
    {
        ID3D11RenderTargetView* target = _backBuffer;
        Context->OMSetRenderTargets(1, &target, (ID3D11DepthStencilView*)null);
        D3D11_VIEWPORT viewport = new() { Width = Width, Height = Height, MaxDepth = 1 };
        Context->RSSetViewports(1, &viewport);
        float* color = stackalloc float[4] { red, green, blue, 1 };
        Context->ClearRenderTargetView(target, color);
    }

    /// <summary>Rebinds the back buffer; a flip-model present unbinds it.</summary>
    public void BindBackBuffer()
    {
        ID3D11RenderTargetView* target = _backBuffer;
        Context->OMSetRenderTargets(1, &target, (ID3D11DepthStencilView*)null);
    }

    public void Present(bool waitForVerticalBlank)
    {
        HRESULT hr = _swapChain->Present(waitForVerticalBlank ? 1u : 0u, 0);
        if (hr == HRESULT.DXGI_ERROR_DEVICE_REMOVED || hr == HRESULT.DXGI_ERROR_DEVICE_RESET) Hr.Check(hr, "Present");
    }

    public void Dispose()
    {
        if (_backBuffer != null) _backBuffer->Release();
        if (_swapChain != null) _swapChain->Release();
        if (Context != null) Context->Release();
        if (Device != null) Device->Release();
        _backBuffer = null;
        _swapChain = null;
        Context = null;
        Device = null;
    }
}
