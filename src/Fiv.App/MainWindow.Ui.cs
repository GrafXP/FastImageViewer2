using System.Numerics;
using System.Text.Unicode;
using Fiv.Imaging;
using Hexa.NET.ImGui;

namespace Fiv.App;

// What is drawn over the image: status bar, timing overlay, display controls, messages.
// Text is formatted as UTF-8 into one reused buffer; drawing a frame allocates nothing.
internal sealed unsafe partial class MainWindow
{
    Ui? _ui;
    Ui? _pendingUi;
    bool _showPerf;
    bool _showDisplayPanel;
    int _extraFrames; // frames still to render after the current one; see Invalidate()
    readonly byte[] _utf8 = new byte[2048];

    const ImGuiWindowFlags PinnedWindow = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;

    /// <summary>Draws the first <paramref name="length"/> bytes of the text buffer.</summary>
    void Put(int length)
    {
        fixed (byte* text = _utf8) ImGui.TextUnformatted(text, text + length);
    }

    void Separator()
    {
        ImGui.SameLine();
        ImGui.TextDisabled("|"u8);
        ImGui.SameLine();
    }

    void DrawUi()
    {
        DrawStatusBar();
        if (_showPerf) DrawPerfOverlay();
        if (_showDisplayPanel) DrawDisplayPanel();
        DrawMessage();
    }

    void DrawStatusBar()
    {
        float scale = (float)_dpiScale;
        int height = StatusBarHeight;
        ImGui.SetNextWindowPos(new Vector2(0, _clientHeight - height));
        ImGui.SetNextWindowSize(new Vector2(_clientWidth, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * scale, 2 * scale));
        if (ImGui.Begin("status"u8, PinnedWindow | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            LoadedImage? image = _image;
            if (image == null)
            {
                ImGui.TextDisabled(_files.Count == 0 ? "No image"u8 : "Loading"u8);
            }
            else
            {
                if (FileCount > 1 && Utf8.TryWrite(_utf8, $"{_fileIndex + 1}/{FileCount}", out int n))
                {
                    Put(n);
                    Separator();
                }
                if (Utf8.TryWrite(_utf8, $"{Path.GetFileName(image.Path.AsSpan())}", out n)) Put(n);
                if (image.HasSize)
                {
                    Separator();
                    double megapixels = (double)image.Width * image.Height / 1e6;
                    if (Utf8.TryWrite(_utf8, $"{image.UprightWidth:0} x {image.UprightHeight:0}   {megapixels:0.0} MP   {image.FileSize / 1048576.0:0.00} MB", out n)) Put(n);
                    Separator();
                    if (Utf8.TryWrite(_utf8, $"{_view.Zoom * 100:0.#} %", out n)) Put(n);
                    if (image.Layout is { } layout && layout != PixelLayout.Bgra8)
                    {
                        Separator();
                        if (Utf8.TryWrite(_utf8, $"{LayoutName(layout)}", out n)) Put(n);
                    }
                    if (TryGetCursorPixel(image, out int x, out int y, out PixelValue value))
                    {
                        Separator();
                        if (Utf8.TryWrite(_utf8, $"x {x}  y {y}", out n)) Put(n);
                        Separator();
                        WriteValue(value);
                    }
                    if (image.GpuFull == null && image.Error == null)
                    {
                        Separator();
                        ImGui.TextDisabled(image.Preview != null ? "preview"u8 : "loading"u8);
                    }
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    static string LayoutName(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => "8-bit gray",
        PixelLayout.Gray16 => "16-bit gray",
        PixelLayout.GrayFloat => "32-bit float gray",
        PixelLayout.Rgba16 => "16-bit RGBA",
        _ => "8-bit BGRA",
    };

    void WriteValue(in PixelValue value)
    {
        int n;
        bool ok = value.Layout switch
        {
            PixelLayout.Bgra8 when value.A == 255 => Utf8.TryWrite(_utf8, $"R {value.R:0}  G {value.G:0}  B {value.B:0}", out n),
            PixelLayout.Bgra8 or PixelLayout.Rgba16 => Utf8.TryWrite(_utf8, $"R {value.R:0}  G {value.G:0}  B {value.B:0}  A {value.A:0}", out n),
            PixelLayout.GrayFloat => Utf8.TryWrite(_utf8, $"{value.R:G6}", out n),
            _ => Utf8.TryWrite(_utf8, $"{value.R:0}", out n),
        };
        if (ok) Put(n);
        if (value.FromPreview)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(preview)"u8);
        }
    }

    /// <summary>The pixel of the stored image under the mouse, if the mouse is over the image.</summary>
    bool TryGetCursorPixel(LoadedImage image, out int x, out int y, out PixelValue value)
    {
        x = y = 0;
        value = default;
        if (_mouseX < 0 || _mouseY < 0 || _mouseX >= ViewWidth || _mouseY >= ViewHeight) return false;
        (double ix, double iy) = ImageToScreen(image, _view).Inverse().Apply(_mouseX + 0.5, _mouseY + 0.5);
        x = (int)Math.Floor(ix);
        y = (int)Math.Floor(iy);
        return image.TryReadPixel(x, y, out value);
    }

    void DrawPerfOverlay()
    {
        float scale = (float)_dpiScale;
        ImGui.SetNextWindowPos(new Vector2(10 * scale, 10 * scale));
        ImGui.SetNextWindowBgAlpha(0.8f);
        if (ImGui.Begin("perf"u8, PinnedWindow | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs))
        {
            int n;
            ImGui.TextDisabled("start-up, ms after main()"u8);
            foreach (Perf.Mark mark in Perf.Marks)
            {
                if (mark.Name == null || mark.Name == "main") continue;
                if (Utf8.TryWrite(_utf8, $"{Perf.MillisecondsSinceStart(mark.Ticks),8:0.0}  {mark.Name}", out n)) Put(n);
            }
            if (_image is { } image)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("this image, ms"u8);
                if (image.PreviewInfo is { } preview && Utf8.TryWrite(_utf8, $"{preview.ReadMs,8:0.0}  read file\n{preview.DecodeMs,8:0.0}  decode preview {preview.Image?.Width ?? 0} x {preview.Image?.Height ?? 0}", out n)) Put(n);
                if (image.FullInfo is { } full)
                {
                    if (image.PreviewInfo == null && Utf8.TryWrite(_utf8, $"{full.ReadMs,8:0.0}  read file", out n)) Put(n);
                    if (Utf8.TryWrite(_utf8, $"{full.DecodeMs,8:0.0}  decode full\n{full.MipMs,8:0.0}  mip levels + statistics", out n)) Put(n);
                }
                if (image.GpuFull is { } gpuImage && Utf8.TryWrite(_utf8, $"{image.UploadMs,8:0.0}  upload {gpuImage.ByteCount / 1048576.0:0} MB, {gpuImage.Tiles.Count} texture(s)", out n)) Put(n);
            }
            if (_folder is { } folder)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("folder index, ms"u8);
                if (Utf8.TryWrite(_utf8, $"{folder.EnumerateMs,8:0.0}  read {folder.Count} entries\n{folder.SortMs,8:0.0}  sort", out n)) Put(n);
            }
            ImGui.Spacing();
            if (Utf8.TryWrite(_utf8, $"{_lastFrameMs,8:0.00}  last frame (CPU)", out n)) Put(n);
        }
        ImGui.End();
    }

    void DrawDisplayPanel()
    {
        LoadedImage? image = _image;
        float scale = (float)_dpiScale;
        ImGui.SetNextWindowPos(new Vector2(_clientWidth - 10 * scale, 10 * scale), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.9f);
        if (ImGui.Begin("Display"u8, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav))
        {
            // The window is edited in the file's units: 0..65535 for 16-bit, the stored values for float, 0..255 for 8-bit.
            float unit = image?.Layout switch
            {
                PixelLayout.Gray16 or PixelLayout.Rgba16 => 65535f,
                PixelLayout.GrayFloat => 1f,
                _ => 255f,
            };
            float low = _display.WindowLow * unit, high = _display.WindowHigh * unit;
            float rangeLow = 0, rangeHigh = unit;
            if (image?.Layout == PixelLayout.GrayFloat)
            {
                float span = Math.Max(image.ValueRange.Max - image.ValueRange.Min, 1e-6f);
                rangeLow = image.ValueRange.Min - span * 0.25f;
                rangeHigh = image.ValueRange.Max + span * 0.25f;
            }
            ImGui.PushItemWidth(260 * scale);
            bool changed = ImGui.SliderFloat("black"u8, ref low, rangeLow, rangeHigh);
            changed |= ImGui.SliderFloat("white"u8, ref high, rangeLow, rangeHigh);
            changed |= ImGui.SliderFloat("gamma"u8, ref _display.Gamma, 0.2f, 5f);
            ImGui.PopItemWidth();
            if (changed)
            {
                _display.WindowLow = low / unit;
                _display.WindowHigh = high / unit;
            }
            if (ImGui.Button("Auto (min - max)"u8) && image != null)
            {
                _display.WindowLow = image.ValueRange.Min;
                _display.WindowHigh = image.ValueRange.Max;
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset"u8))
            {
                _display.WindowLow = 0;
                _display.WindowHigh = 1;
                _display.Gamma = 1;
            }
            ImGui.Checkbox("Exact pixels when zoomed in (I)"u8, ref _display.Sharp);
            ImGui.Checkbox("Keep zoom and position across images (L)"u8, ref _keepView);
        }
        ImGui.End();
    }

    /// <summary>Errors, and the hint in an empty window, centred over the view.</summary>
    void DrawMessage()
    {
        string? text = _image?.Error ?? (_image == null && _files.Count == 0 ? "Drop images here" : null);
        if (text == null || !Utf8.TryWrite(_utf8, $"{text}", out int n)) return;
        ImGui.SetNextWindowPos(new Vector2(ViewWidth / 2f, ViewHeight / 2f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.6f);
        if (ImGui.Begin("message"u8, PinnedWindow | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs))
        {
            if (_image?.Error != null && Utf8.TryWrite(_utf8.AsSpan(1024), $"{Path.GetFileName(_image.Path.AsSpan())}", out int m))
            {
                fixed (byte* name = &_utf8[1024]) ImGui.TextUnformatted(name, name + m);
            }
            Put(n);
        }
        ImGui.End();
    }
}
