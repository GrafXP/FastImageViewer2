// Language bake-off, C++ side. Must stay step-for-step identical to ..\cs\Program.cs.
//
//   bakeoff <image.jpg> [warmRuns] [holdMs]
//
// Thread A (main): window, D3D11 device, swap chain, shaders.
// Thread B:        read file, decode at the smallest JPEG scale that still covers the viewport.
// Then: upload, draw, present. Then `warmRuns` repetitions of decode + upload + present in the
// running process. Every stage is stamped with QueryPerformanceCounter and written to stdout.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwmapi.h>
#include <malloc.h>
#include <stdio.h>
#include <stdlib.h>
#include <turbojpeg.h>

#include "blit_ps.h"
#include "blit_vs.h"

static const int ViewWidth = 1600;
static const int ViewHeight = 900;
static const int MaxWarmRuns = 256;

static long long Now()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

static void Fail(const char* what)
{
    char line[256];
    int n = snprintf(line, sizeof(line), "error=%s\n", what);
    DWORD written;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line, (DWORD)n, &written, nullptr);
    ExitProcess(1);
}

static void Check(HRESULT hr, const char* what)
{
    if (FAILED(hr)) Fail(what);
}

struct Decoded
{
    const wchar_t* path;
    unsigned char* pixels;
    int width, height, stride;
    int sourceWidth, sourceHeight;
    long long tStart, tRead, tHeader, tDecoded;
};

// Size of the largest rectangle with the image's aspect ratio that fits the viewport.
static void FitSize(int imageWidth, int imageHeight, int* fitWidth, int* fitHeight)
{
    double scale = std::min((double)ViewWidth / imageWidth, (double)ViewHeight / imageHeight);
    *fitWidth = std::max(1, (int)(imageWidth * scale + 0.5));
    *fitHeight = std::max(1, (int)(imageHeight * scale + 0.5));
}

static void Decode(Decoded* d)
{
    d->tStart = Now();
    HANDLE file = CreateFileW(d->path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                              FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) Fail("open");
    LARGE_INTEGER size;
    GetFileSizeEx(file, &size);
    unsigned char* jpeg = (unsigned char*)malloc((size_t)size.QuadPart);
    DWORD got = 0;
    if (!ReadFile(file, jpeg, (DWORD)size.QuadPart, &got, nullptr) || got != size.QuadPart) Fail("read");
    CloseHandle(file);
    d->tRead = Now();

    tjhandle tj = tj3Init(TJINIT_DECOMPRESS);
    if (tj3DecompressHeader(tj, jpeg, got) != 0) Fail("header");
    d->sourceWidth = tj3Get(tj, TJPARAM_JPEGWIDTH);
    d->sourceHeight = tj3Get(tj, TJPARAM_JPEGHEIGHT);
    d->tHeader = Now();

    int fitWidth, fitHeight;
    FitSize(d->sourceWidth, d->sourceHeight, &fitWidth, &fitHeight);
    tjscalingfactor factor = {1, 1};
    for (int denom = 8; denom > 1; denom /= 2)
    {
        tjscalingfactor candidate = {1, denom};
        if (TJSCALED(d->sourceWidth, candidate) >= fitWidth && TJSCALED(d->sourceHeight, candidate) >= fitHeight)
        {
            factor = candidate;
            break;
        }
    }
    if (tj3SetScalingFactor(tj, factor) != 0) Fail("scale");
    d->width = TJSCALED(d->sourceWidth, factor);
    d->height = TJSCALED(d->sourceHeight, factor);
    d->stride = d->width * 4;
    d->pixels = (unsigned char*)_aligned_malloc((size_t)d->stride * d->height, 64);
    if (tj3Decompress8(tj, jpeg, got, d->pixels, d->stride, TJPF_BGRA) != 0) Fail("decode");
    tj3Destroy(tj);
    free(jpeg);
    d->tDecoded = Now();
}

static DWORD WINAPI DecodeThread(void* parameter)
{
    Decode((Decoded*)parameter);
    return 0;
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_DESTROY)
    {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

static void PumpMessages()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

struct Gpu
{
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    IDXGISwapChain1* swapChain;
    ID3D11RenderTargetView* backBuffer;
    ID3D11VertexShader* vertexShader;
    ID3D11PixelShader* pixelShader;
    ID3D11SamplerState* sampler;
};

// Uploads the decoded image, draws it fitted and centred, presents.
static void UploadAndPresent(const Gpu& gpu, const Decoded& d)
{
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = d.width;
    desc.Height = d.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_IMMUTABLE;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data = {d.pixels, (UINT)d.stride, 0};
    ID3D11Texture2D* texture;
    ID3D11ShaderResourceView* view;
    Check(gpu.device->CreateTexture2D(&desc, &data, &texture), "texture");
    Check(gpu.device->CreateShaderResourceView(texture, nullptr, &view), "srv");

    int fitWidth, fitHeight;
    FitSize(d.width, d.height, &fitWidth, &fitHeight);
    D3D11_VIEWPORT viewport = {(ViewWidth - fitWidth) * 0.5f, (ViewHeight - fitHeight) * 0.5f,
                               (float)fitWidth, (float)fitHeight, 0, 1};
    const float clear[4] = {0.1f, 0.1f, 0.1f, 1};
    gpu.context->OMSetRenderTargets(1, &gpu.backBuffer, nullptr);
    gpu.context->ClearRenderTargetView(gpu.backBuffer, clear);
    gpu.context->RSSetViewports(1, &viewport);
    gpu.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    gpu.context->VSSetShader(gpu.vertexShader, nullptr, 0);
    gpu.context->PSSetShader(gpu.pixelShader, nullptr, 0);
    gpu.context->PSSetShaderResources(0, 1, &view);
    gpu.context->PSSetSamplers(0, 1, &gpu.sampler);
    gpu.context->Draw(3, 0);
    Check(gpu.swapChain->Present(0, 0), "present");
    view->Release();
    texture->Release();
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    long long tMain = Now();
    if (__argc < 2) Fail("usage: bakeoff <image.jpg> [warmRuns] [holdMs]");
    int warmRuns = __argc > 2 ? std::min(_wtoi(__wargv[2]), MaxWarmRuns) : 0;
    int holdMs = __argc > 3 ? _wtoi(__wargv[3]) : 0;

    Decoded first = {};
    first.path = __wargv[1];
    HANDLE decodeThread = CreateThread(nullptr, 0, DecodeThread, &first, 0, nullptr);

    WNDCLASSEXW windowClass = {sizeof(windowClass)};
    windowClass.lpfnWndProc = WndProc;
    windowClass.hInstance = instance;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.lpszClassName = L"FivBakeoff";
    RegisterClassExW(&windowClass);
    RECT rect = {0, 0, ViewWidth, ViewHeight};
    AdjustWindowRectEx(&rect, WS_OVERLAPPEDWINDOW, FALSE, WS_EX_NOREDIRECTIONBITMAP);
    HWND hwnd = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP, windowClass.lpszClassName, L"bakeoff (C++)",
                                WS_OVERLAPPEDWINDOW, 60, 40, rect.right - rect.left, rect.bottom - rect.top,
                                nullptr, nullptr, instance, nullptr);
    if (!hwnd) Fail("window");
    long long tWindow = Now();

    Gpu gpu = {};
    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
    Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1,
                            D3D11_SDK_VERSION, &gpu.device, nullptr, &gpu.context),
          "device");
    long long tDevice = Now();

    IDXGIDevice* dxgiDevice;
    IDXGIAdapter* adapter;
    IDXGIFactory2* factory;
    Check(gpu.device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice), "dxgi device");
    Check(dxgiDevice->GetAdapter(&adapter), "adapter");
    Check(adapter->GetParent(__uuidof(IDXGIFactory2), (void**)&factory), "factory");
    DXGI_SWAP_CHAIN_DESC1 swapChainDesc = {};
    swapChainDesc.Width = ViewWidth;
    swapChainDesc.Height = ViewHeight;
    swapChainDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    swapChainDesc.SampleDesc.Count = 1;
    swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapChainDesc.BufferCount = 2;
    swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    swapChainDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    Check(factory->CreateSwapChainForHwnd(gpu.device, hwnd, &swapChainDesc, nullptr, nullptr, &gpu.swapChain),
          "swap chain");
    ID3D11Texture2D* backBufferTexture;
    Check(gpu.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBufferTexture), "back buffer");
    Check(gpu.device->CreateRenderTargetView(backBufferTexture, nullptr, &gpu.backBuffer), "rtv");
    long long tSwapChain = Now();

    Check(gpu.device->CreateVertexShader(g_blit_vs, sizeof(g_blit_vs), nullptr, &gpu.vertexShader), "vs");
    Check(gpu.device->CreatePixelShader(g_blit_ps, sizeof(g_blit_ps), nullptr, &gpu.pixelShader), "ps");
    D3D11_SAMPLER_DESC samplerDesc = {};
    samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
    Check(gpu.device->CreateSamplerState(&samplerDesc, &gpu.sampler), "sampler");
    long long tPipeline = Now();

    WaitForSingleObject(decodeThread, INFINITE);
    long long tJoined = Now();

    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    long long tShown = Now();
    UploadAndPresent(gpu, first);
    long long tPresented = Now();
    DwmFlush();
    long long tFlushed = Now();

    // Warm path: the same file again, decoded and presented in the running process.
    static long long warmDecode[MaxWarmRuns], warmPresent[MaxWarmRuns];
    for (int i = 0; i < warmRuns; i++)
    {
        PumpMessages();
        Decoded again = {};
        again.path = first.path;
        long long t0 = Now();
        Decode(&again);
        long long t1 = Now();
        UploadAndPresent(gpu, again);
        long long t2 = Now();
        _aligned_free(again.pixels);
        warmDecode[i] = t1 - t0;
        warmPresent[i] = t2 - t1;
    }

    LARGE_INTEGER frequency;
    QueryPerformanceFrequency(&frequency);
    static char out[16384];
    int n = snprintf(out, sizeof(out),
                     "lang=cpp qpf=%lld main=%lld window=%lld device=%lld swapchain=%lld pipeline=%lld "
                     "decodeStart=%lld read=%lld header=%lld decoded=%lld joined=%lld shown=%lld presented=%lld "
                     "flushed=%lld source=%dx%d decodedSize=%dx%d",
                     frequency.QuadPart, tMain, tWindow, tDevice, tSwapChain, tPipeline, first.tStart, first.tRead,
                     first.tHeader, first.tDecoded, tJoined, tShown, tPresented, tFlushed, first.sourceWidth,
                     first.sourceHeight, first.width, first.height);
    n += snprintf(out + n, sizeof(out) - n, " warmDecode=");
    for (int i = 0; i < warmRuns; i++) n += snprintf(out + n, sizeof(out) - n, i ? ",%lld" : "%lld", warmDecode[i]);
    n += snprintf(out + n, sizeof(out) - n, " warmPresent=");
    for (int i = 0; i < warmRuns; i++) n += snprintf(out + n, sizeof(out) - n, i ? ",%lld" : "%lld", warmPresent[i]);
    n += snprintf(out + n, sizeof(out) - n, "\n");
    DWORD written;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), out, (DWORD)n, &written, nullptr);

    for (DWORD end = GetTickCount() + holdMs; holdMs > 0 && GetTickCount() < end;)
    {
        PumpMessages();
        Sleep(5);
    }
    ExitProcess(0);
}
