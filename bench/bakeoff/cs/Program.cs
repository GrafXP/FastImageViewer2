// Language bake-off, C# Native AOT side. Must stay step-for-step identical to ..\cpp\main.cpp.
//
//   bakeoff <image.jpg> [warmRuns] [holdMs]
//
// Thread A (main): window, D3D11 device, swap chain, shaders.
// Thread B:        read file, decode at the smallest JPEG scale that still covers the viewport.
// Then: upload, draw, present. Then `warmRuns` repetitions of decode + upload + present in the
// running process. Every stage is stamped with QueryPerformanceCounter and written to stdout.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.System.Console;
using Windows.Win32.UI.WindowsAndMessaging;

[assembly: DisableRuntimeMarshalling]

namespace Fiv.Bakeoff;

internal static unsafe class Program
{
    const int ViewWidth = 1600;
    const int ViewHeight = 900;
    const int MaxWarmRuns = 256;

    static long Now() => Stopwatch.GetTimestamp();

    static void WriteStdout(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        uint written;
        fixed (byte* p = bytes)
            PInvoke.WriteFile(PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE), p, (uint)bytes.Length, &written, null);
    }

    static void Fail(string what)
    {
        WriteStdout("error=" + what + "\n");
        PInvoke.ExitProcess(1);
    }

    static void Check(HRESULT hr, string what)
    {
        if (hr.Failed) Fail(what);
    }

    struct Decoded
    {
        public byte* Pixels;
        public int Width, Height, Stride;
        public int SourceWidth, SourceHeight;
        public long TStart, TRead, THeader, TDecoded;
    }

    // Size of the largest rectangle with the image's aspect ratio that fits the viewport.
    static void FitSize(int imageWidth, int imageHeight, out int fitWidth, out int fitHeight)
    {
        double scale = Math.Min((double)ViewWidth / imageWidth, (double)ViewHeight / imageHeight);
        fitWidth = Math.Max(1, (int)(imageWidth * scale + 0.5));
        fitHeight = Math.Max(1, (int)(imageHeight * scale + 0.5));
    }

    static int Scaled(int dimension, TurboJpeg.ScalingFactor f) => (dimension * f.Num + f.Denom - 1) / f.Denom;

    static void Decode(string path, ref Decoded d)
    {
        d.TStart = Now();
        HANDLE file;
        fixed (char* p = path)
        {
            file = PInvoke.CreateFile(p, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ, FILE_SHARE_MODE.FILE_SHARE_READ, null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN, default);
        }
        if ((nint)file.Value == -1) Fail("open");
        long size;
        PInvoke.GetFileSizeEx(file, &size);
        byte* jpeg = (byte*)NativeMemory.Alloc((nuint)size);
        uint got = 0;
        if (!PInvoke.ReadFile(file, jpeg, (uint)size, &got, null) || got != size) Fail("read");
        PInvoke.CloseHandle(file);
        d.TRead = Now();

        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        if (TurboJpeg.tj3DecompressHeader(tj, jpeg, got) != 0) Fail("header");
        d.SourceWidth = TurboJpeg.tj3Get(tj, TurboJpeg.ParamJpegWidth);
        d.SourceHeight = TurboJpeg.tj3Get(tj, TurboJpeg.ParamJpegHeight);
        d.THeader = Now();

        FitSize(d.SourceWidth, d.SourceHeight, out int fitWidth, out int fitHeight);
        var factor = new TurboJpeg.ScalingFactor { Num = 1, Denom = 1 };
        for (int denom = 8; denom > 1; denom /= 2)
        {
            var candidate = new TurboJpeg.ScalingFactor { Num = 1, Denom = denom };
            if (Scaled(d.SourceWidth, candidate) >= fitWidth && Scaled(d.SourceHeight, candidate) >= fitHeight)
            {
                factor = candidate;
                break;
            }
        }
        if (TurboJpeg.tj3SetScalingFactor(tj, factor) != 0) Fail("scale");
        d.Width = Scaled(d.SourceWidth, factor);
        d.Height = Scaled(d.SourceHeight, factor);
        d.Stride = d.Width * 4;
        d.Pixels = (byte*)NativeMemory.AlignedAlloc((nuint)d.Stride * (nuint)d.Height, 64);
        if (TurboJpeg.tj3Decompress8(tj, jpeg, got, d.Pixels, d.Stride, TurboJpeg.PixelFormatBgra) != 0) Fail("decode");
        TurboJpeg.tj3Destroy(tj);
        NativeMemory.Free(jpeg);
        d.TDecoded = Now();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_DESTROY)
        {
            PInvoke.PostQuitMessage(0);
            return default;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    static void PumpMessages()
    {
        MSG msg;
        while (PInvoke.PeekMessage(&msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    struct Gpu
    {
        public ID3D11Device* Device;
        public ID3D11DeviceContext* Context;
        public IDXGISwapChain1* SwapChain;
        public ID3D11RenderTargetView* BackBuffer;
        public ID3D11VertexShader* VertexShader;
        public ID3D11PixelShader* PixelShader;
        public ID3D11SamplerState* Sampler;
    }

    // Uploads the decoded image, draws it fitted and centred, presents.
    static void UploadAndPresent(in Gpu gpu, in Decoded d)
    {
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = (uint)d.Width;
        desc.Height = (uint)d.Height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data = new() { pSysMem = d.Pixels, SysMemPitch = (uint)d.Stride };
        ID3D11Texture2D* texture;
        ID3D11ShaderResourceView* view;
        Check(gpu.Device->CreateTexture2D(&desc, &data, &texture), "texture");
        Check(gpu.Device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &view), "srv");

        FitSize(d.Width, d.Height, out int fitWidth, out int fitHeight);
        D3D11_VIEWPORT viewport = new()
        {
            TopLeftX = (ViewWidth - fitWidth) * 0.5f,
            TopLeftY = (ViewHeight - fitHeight) * 0.5f,
            Width = fitWidth,
            Height = fitHeight,
            MinDepth = 0,
            MaxDepth = 1,
        };
        float* clear = stackalloc float[4] { 0.1f, 0.1f, 0.1f, 1 };
        ID3D11RenderTargetView* backBuffer = gpu.BackBuffer;
        ID3D11SamplerState* sampler = gpu.Sampler;
        gpu.Context->OMSetRenderTargets(1, &backBuffer, null);
        gpu.Context->ClearRenderTargetView(backBuffer, clear);
        gpu.Context->RSSetViewports(1, &viewport);
        gpu.Context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        gpu.Context->VSSetShader(gpu.VertexShader, null, 0);
        gpu.Context->PSSetShader(gpu.PixelShader, null, 0);
        gpu.Context->PSSetShaderResources(0, 1, &view);
        gpu.Context->PSSetSamplers(0, 1, &sampler);
        gpu.Context->Draw(3, 0);
        Check(gpu.SwapChain->Present(0, 0), "present");
        view->Release();
        texture->Release();
    }

    static string s_path = "";
    static Decoded s_first;

    static int Main(string[] args)
    {
        long tMain = Now();
        if (args.Length < 1) Fail("usage: bakeoff <image.jpg> [warmRuns] [holdMs]");
        int warmRuns = args.Length > 1 ? Math.Min(int.Parse(args[1]), MaxWarmRuns) : 0;
        int holdMs = args.Length > 2 ? int.Parse(args[2]) : 0;

        s_path = args[0];
        var decodeThread = new Thread(static () => Decode(s_path, ref s_first));
        decodeThread.Start();

        HINSTANCE instance = new(PInvoke.GetModuleHandle(null).Value);
        HWND hwnd;
        fixed (char* className = "FivBakeoff")
        fixed (char* title = "bakeoff (C#)")
        {
            WNDCLASSEXW windowClass = default;
            windowClass.cbSize = (uint)sizeof(WNDCLASSEXW);
            windowClass.lpfnWndProc = &WndProc;
            windowClass.hInstance = instance;
            windowClass.hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);
            windowClass.lpszClassName = className;
            PInvoke.RegisterClassEx(&windowClass);
            RECT rect = new() { right = ViewWidth, bottom = ViewHeight };
            PInvoke.AdjustWindowRectEx(&rect, WINDOW_STYLE.WS_OVERLAPPEDWINDOW, false, WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP);
            hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP, className, title,
                WINDOW_STYLE.WS_OVERLAPPEDWINDOW, 60, 40, rect.right - rect.left, rect.bottom - rect.top,
                default, default, instance, null);
        }
        if (hwnd.IsNull) Fail("window");
        long tWindow = Now();

        Gpu gpu = default;
        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        Check(PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, PInvoke.D3D11_SDK_VERSION,
            &gpu.Device, null, &gpu.Context), "device");
        long tDevice = Now();

        IDXGIDevice* dxgiDevice;
        IDXGIAdapter* adapter;
        IDXGIFactory2* factory;
        Guid iid = IDXGIDevice.IID_Guid;
        Check(gpu.Device->QueryInterface(&iid, (void**)&dxgiDevice), "dxgi device");
        Check(dxgiDevice->GetAdapter(&adapter), "adapter");
        iid = IDXGIFactory2.IID_Guid;
        Check(adapter->GetParent(&iid, (void**)&factory), "factory");
        DXGI_SWAP_CHAIN_DESC1 swapChainDesc = default;
        swapChainDesc.Width = ViewWidth;
        swapChainDesc.Height = ViewHeight;
        swapChainDesc.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        swapChainDesc.SampleDesc.Count = 1;
        swapChainDesc.BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapChainDesc.BufferCount = 2;
        swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapChainDesc.AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;
        Check(factory->CreateSwapChainForHwnd((IUnknown*)gpu.Device, hwnd, &swapChainDesc, (DXGI_SWAP_CHAIN_FULLSCREEN_DESC*)null, null, &gpu.SwapChain),
            "swap chain");
        ID3D11Texture2D* backBufferTexture;
        iid = ID3D11Texture2D.IID_Guid;
        Check(gpu.SwapChain->GetBuffer(0, &iid, (void**)&backBufferTexture), "back buffer");
        Check(gpu.Device->CreateRenderTargetView((ID3D11Resource*)backBufferTexture, (D3D11_RENDER_TARGET_VIEW_DESC*)null, &gpu.BackBuffer), "rtv");
        long tSwapChain = Now();

        fixed (byte* vs = Shaders.BlitVs)
            Check(gpu.Device->CreateVertexShader(vs, (nuint)Shaders.BlitVs.Length, null, &gpu.VertexShader), "vs");
        fixed (byte* ps = Shaders.BlitPs)
            Check(gpu.Device->CreatePixelShader(ps, (nuint)Shaders.BlitPs.Length, null, &gpu.PixelShader), "ps");
        D3D11_SAMPLER_DESC samplerDesc = default;
        samplerDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = PInvoke.D3D11_FLOAT32_MAX;
        Check(gpu.Device->CreateSamplerState(&samplerDesc, &gpu.Sampler), "sampler");
        long tPipeline = Now();

        decodeThread.Join();
        long tJoined = Now();

        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        long tShown = Now();
        UploadAndPresent(gpu, s_first);
        long tPresented = Now();
        PInvoke.DwmFlush();
        long tFlushed = Now();

        // Warm path: the same file again, decoded and presented in the running process.
        long[] warmDecode = new long[MaxWarmRuns], warmPresent = new long[MaxWarmRuns];
        for (int i = 0; i < warmRuns; i++)
        {
            PumpMessages();
            Decoded again = default;
            long t0 = Now();
            Decode(s_path, ref again);
            long t1 = Now();
            UploadAndPresent(gpu, again);
            long t2 = Now();
            NativeMemory.AlignedFree(again.Pixels);
            warmDecode[i] = t1 - t0;
            warmPresent[i] = t2 - t1;
        }

        var sb = new StringBuilder(16384);
        sb.Append($"lang=cs qpf={Stopwatch.Frequency} main={tMain} window={tWindow} device={tDevice} swapchain={tSwapChain} pipeline={tPipeline} ");
        sb.Append($"decodeStart={s_first.TStart} read={s_first.TRead} header={s_first.THeader} decoded={s_first.TDecoded} joined={tJoined} shown={tShown} presented={tPresented} ");
        sb.Append($"flushed={tFlushed} source={s_first.SourceWidth}x{s_first.SourceHeight} decodedSize={s_first.Width}x{s_first.Height}");
        sb.Append(" warmDecode=").AppendJoin(',', warmDecode.AsSpan(0, warmRuns).ToArray());
        sb.Append(" warmPresent=").AppendJoin(',', warmPresent.AsSpan(0, warmRuns).ToArray());
        sb.Append('\n');
        WriteStdout(sb.ToString());

        for (long end = Environment.TickCount64 + holdMs; holdMs > 0 && Environment.TickCount64 < end;)
        {
            PumpMessages();
            Thread.Sleep(5);
        }
        PInvoke.ExitProcess(0);
        return 0;
    }
}

internal static unsafe class TurboJpeg
{
    public const int InitDecompress = 1;      // TJINIT_DECOMPRESS
    public const int ParamJpegWidth = 5;      // TJPARAM_JPEGWIDTH
    public const int ParamJpegHeight = 6;     // TJPARAM_JPEGHEIGHT
    public const int PixelFormatBgra = 8;     // TJPF_BGRA

    public struct ScalingFactor
    {
        public int Num, Denom;
    }

    [DllImport("turbojpeg", ExactSpelling = true)] public static extern nint tj3Init(int initType);
    [DllImport("turbojpeg", ExactSpelling = true)] public static extern void tj3Destroy(nint handle);
    [DllImport("turbojpeg", ExactSpelling = true)] public static extern int tj3Get(nint handle, int param);
    [DllImport("turbojpeg", ExactSpelling = true)] public static extern int tj3DecompressHeader(nint handle, byte* jpegBuf, nuint jpegSize);
    [DllImport("turbojpeg", ExactSpelling = true)] public static extern int tj3SetScalingFactor(nint handle, ScalingFactor scalingFactor);
    [DllImport("turbojpeg", ExactSpelling = true)] public static extern int tj3Decompress8(nint handle, byte* jpegBuf, nuint jpegSize, byte* dstBuf, int pitch, int pixelFormat);
}
