using Fiv.Render;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using Windows.Win32.Foundation;

namespace Fiv.App;

/// <summary>
/// Dear ImGui on top of the window's swap chain. Setting it up costs about 80 ms (two DLLs, a
/// font, and the backend compiling its shaders), so it happens in two steps: the constructor does
/// the expensive part and may run on any thread as soon as the device exists;
/// <see cref="AttachWindow"/> finishes on the UI thread. Between the two, nothing else may use ImGui.
/// </summary>
internal sealed unsafe class Ui : IDisposable
{
    readonly ImGuiContextPtr _context;

    /// <summary>True if a panel layout from an earlier run was found; otherwise the window builds the default one.</summary>
    public bool HasSavedLayout { get; }

    public Ui(GraphicsDevice gpu, double dpiScale, string layoutFile)
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // ImGui keeps where the panels are docked in this file. It holds on to the pointer, so the string is never freed.
        HasSavedLayout = File.Exists(layoutFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layoutFile)!);
            io.IniFilename = (byte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(layoutFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            io.IniFilename = null; // the layout just is not remembered
        }
        io.LogFilename = null;

        string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (File.Exists(font)) io.Fonts.AddFontFromFileTTF(font, (float)Math.Round(16 * dpiScale));

        ImGui.StyleColorsDark();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.WindowBorderSize = 0;
        style.ScaleAllSizes((float)dpiScale);

        // Device calls only, which Direct3D allows from any thread; the context is not touched here.
        ImGuiImplD3D11.SetCurrentContext(_context);
        ImGuiImplD3D11.Init(new ID3D11DevicePtr((ID3D11Device*)gpu.Device), new ID3D11DeviceContextPtr((ID3D11DeviceContext*)gpu.Context));
        ImGuiImplD3D11.CreateDeviceObjects();
    }

    /// <summary>Connects ImGui to the window's input. UI thread.</summary>
    public void AttachWindow(HWND window)
    {
        ImGui.SetCurrentContext(_context);
        ImGuiImplWin32.SetCurrentContext(_context);
        ImGuiImplWin32.Init(window.Value);
    }

    public bool WantsMouse => ImGui.GetIO().WantCaptureMouse;
    public bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>Feeds a window message to ImGui. True if ImGui consumed it.</summary>
    public bool HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam) =>
        ImGuiImplWin32.WndProcHandler((nint)window.Value, message, wParam.Value, lParam.Value) != 0;

    /// <summary>
    /// For tests: tells ImGui where the mouse is and, with <paramref name="button"/> 1 or 2, that
    /// the left button went down or up, as if the window had received the mouse messages.
    /// </summary>
    public void TestMouse(float x, float y, int button)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.AddMousePosEvent(x, y);
        if (button != 0) io.AddMouseButtonEvent(0, button == 1);
    }

    public void NewFrame()
    {
        ImGuiImplD3D11.NewFrame();
        ImGuiImplWin32.NewFrame();
        ImGui.NewFrame();
    }

    /// <summary>Draws what was built since <see cref="NewFrame"/> into the bound render target.</summary>
    public void Render()
    {
        ImGui.Render();
        ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
    }

    public void Dispose()
    {
        ImGuiImplD3D11.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyContext(_context);
    }
}
