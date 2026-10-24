using System.Numerics;
using System.Text.Unicode;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
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

    /// <summary>
    /// Text that came from outside (file names, paths, error messages), dimmed or coloured.
    /// Never through ImGui.TextDisabled or TextColored: those take a printf format, and a name
    /// with a per-cent sign in it would be read as one.
    /// </summary>
    static void TextIn(Vector4 color, byte* text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    static void TextDim(byte* text) => TextIn(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], text);

    void Separator()
    {
        ImGui.SameLine();
        ImGui.TextDisabled("|"u8);
        ImGui.SameLine();
    }

    bool _layoutChecked;

    void DrawUi()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        DrawStatusBar(viewport); // takes its strip off the bottom; the dock space gets the rest

        // Panels dock around a central area that stays empty: that is where the image is.
        uint dock = ImGui.GetID("dock"u8);
        _menuDrawn = false;
        if (!_layoutChecked || _resetLayout)
        {
            if (!_ui!.HasSavedLayout || _resetLayout) BuildDefaultLayout(dock, viewport.WorkSize);
            _layoutChecked = true;
            _resetLayout = false;
        }
        ImGui.DockSpaceOverViewport(dock, viewport, ImGuiDockNodeFlags.PassthruCentralNode);

        if (_showBrowser)
        {
            ImGui.SetNextWindowSize(new Vector2(420, 600) * (float)_dpiScale, ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Folder"u8, ref _showBrowser)) DrawBrowser();
            ImGui.End();
        }
        if (_showFilmstrip)
        {
            ImGui.SetNextWindowSize(new Vector2(900, 140) * (float)_dpiScale, ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Filmstrip"u8, ref _showFilmstrip, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) DrawFilmstrip();
            ImGui.End();
        }

        ImGuiDockNodePtr central = ImGuiP.DockBuilderGetCentralNode(dock);
        if (!central.IsNull) SetViewRect((int)central.Pos.X, (int)central.Pos.Y, (int)central.Size.X, (int)central.Size.Y);

        if (Comparing)
        {
            DrawCompareOverlays();
            DrawSurfacePanel();
            if (_showComparePanel) DrawComparePanel();
            if (_showMetrics) DrawMetricsPanel();
            if (_alignMode) DrawAlignPanel();
        }
        else
        {
            if (!_surface) DrawSinglePixelValues();
            DrawSingleLabel();
            DrawSurfacePanel();
        }
        if (_measure)
        {
            DrawMeasurePanel(); // first: it knows which point of a line the mouse is at in the plot, which is marked on the image
            DrawMeasurements();
        }
        if (_showPerf) DrawPerfOverlay();
        if (_showDisplayPanel) DrawDisplayPanel();
        if (_showKeys) DrawKeys();
        if (_showSettings) DrawSettings();
        if (_showFileTypes) DrawFileTypes();
        DrawMessage();
        DrawNotice();
        DrawViewMenu();
        DrawDialogs();
        if (!_menuDrawn && !_menuRequested) _menuPath = null; // the item menu has closed
    }

    /// <summary>The layout of a first run: folder panel on the left, filmstrip along the bottom, the image in what remains.</summary>
    void BuildDefaultLayout(uint dock, Vector2 size)
    {
        const ImGuiDockNodeFlags dockSpace = (ImGuiDockNodeFlags)(1 << 10); // ImGuiDockNodeFlags_DockSpace, an internal flag
        ImGuiP.DockBuilderRemoveNode(dock);
        ImGuiP.DockBuilderAddNode(dock, dockSpace);
        ImGuiP.DockBuilderSetNodeSize(dock, size);
        uint left, rest, bottom, centre;
        ImGuiP.DockBuilderSplitNode(dock, ImGuiDir.Left, 0.26f, &left, &rest);
        ImGuiP.DockBuilderSplitNode(rest, ImGuiDir.Down, 0.15f, &bottom, &centre);
        ImGuiP.DockBuilderDockWindow("Folder"u8, left);
        ImGuiP.DockBuilderDockWindow("Filmstrip"u8, bottom);
        ImGuiP.DockBuilderFinish(dock);
    }

    void DrawStatusBar(ImGuiViewportPtr viewport)
    {
        float scale = (float)_dpiScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * scale, 2 * scale));
        bool open;
        fixed (byte* name = "##status"u8)
            open = ImGuiP.BeginViewportSideBar(name, ref *viewport.Handle, ImGuiDir.Down, StatusBarHeight, PinnedWindow | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (open)
        {
            LoadedImage? image = _shown;
            if (image == null)
            {
                ImGui.TextDisabled(FileCount == 0 ? "No image"u8 : "Loading"u8);
            }
            else
            {
                int n;
                bool comparing = Comparing && _singleItem >= 0;
                if (comparing)
                {
                    if (Utf8.TryWrite(_utf8, $"Comparing {_compare.Count}, {ModeName(_compareMode)}", out n)) Put(n);
                    Separator();
                    if (Utf8.TryWrite(_utf8, $"{_singleItem + 1}", out n)) Put(n);
                    Separator();
                }
                else if (FileCount > 1 && _shownIndex >= 0 && Utf8.TryWrite(_utf8, $"{_shownIndex + 1}/{FileCount}", out n))
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
                    double zoom = comparing && _canvasWidth > 0 ? ZoomOf(_singleItem, image) : _view.Zoom;
                    if (SurfaceOnShow) ImGui.TextUnformatted("3D height map"u8);
                    else if (Utf8.TryWrite(_utf8, $"{zoom * 100:0.#} %", out n)) Put(n);
                    if (image.Layout is { } layout && layout != PixelLayout.Bgra8)
                    {
                        Separator();
                        if (Utf8.TryWrite(_utf8, $"{LayoutName(layout)}", out n)) Put(n);
                    }
                    if (_display.Channel != ChannelView.Color)
                    {
                        Separator();
                        if (Utf8.TryWrite(_utf8, $"{ChannelName(_display.Channel)} only", out n)) Put(n);
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
                        ImGui.TextDisabled("preview"u8);
                    }
                }
                if (_folder != null && (_sort != FolderSort.Name || _sortDescending))
                {
                    Separator();
                    if (Utf8.TryWrite(_utf8, $"by {SortName(_sort)}{(_sortDescending ? ", descending" : "")}", out n)) Put(n);
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    static string SortName(FolderSort sort) => sort switch
    {
        FolderSort.Modified => "date",
        FolderSort.Size => "size",
        FolderSort.Type => "type",
        _ => "name",
    };

    static string LayoutName(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => "8-bit gray",
        PixelLayout.Gray16 => "16-bit gray",
        PixelLayout.GrayFloat => "32-bit float gray",
        PixelLayout.Rgba16 => "16-bit RGBA",
        _ => "8-bit BGRA",
    };

    static string ChannelName(ChannelView channel) => channel switch
    {
        ChannelView.Red => "red",
        ChannelView.Green => "green",
        ChannelView.Blue => "blue",
        ChannelView.Alpha => "alpha",
        ChannelView.Luma => "luma",
        _ => "colour",
    };

    /// <summary>Writes a pixel's stored values as text; returns the number of bytes written.</summary>
    static int FormatValue(in PixelValue value, Span<byte> to)
    {
        int n;
        bool ok = value.Layout switch
        {
            PixelLayout.Bgra8 when value.A == 255 => Utf8.TryWrite(to, $"R {value.R:0}  G {value.G:0}  B {value.B:0}", out n),
            PixelLayout.Bgra8 or PixelLayout.Rgba16 => Utf8.TryWrite(to, $"R {value.R:0}  G {value.G:0}  B {value.B:0}  A {value.A:0}", out n),
            PixelLayout.GrayFloat => Utf8.TryWrite(to, $"{value.R:G6}", out n),
            _ => Utf8.TryWrite(to, $"{value.R:0}", out n),
        };
        return ok ? n : 0;
    }

    void WriteValue(in PixelValue value)
    {
        int n = FormatValue(value, _utf8);
        if (n > 0) Put(n);
        if (value.FromPreview)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(preview)"u8);
        }
    }

    /// <summary>The pixel of the stored image under the mouse, if the mouse is over the image.</summary>
    bool TryGetCursorPixel(LoadedImage image, out int x, out int y, out PixelValue value)
    {
        if (Comparing) return TryGetComparePixel(_singleItem, out x, out y, out value); // through the pane the mouse is in
        x = y = 0;
        value = default;
        if (SurfaceOnShow) return false; // the surface is not where the flat image would be
        if (_mouseX < ViewX || _mouseY < ViewY || _mouseX >= ViewX + ViewWidth || _mouseY >= ViewY + ViewHeight) return false;
        (double ix, double iy) = ImageToScreen(image, _view).Inverse().Apply(_mouseX + 0.5, _mouseY + 0.5);
        x = (int)Math.Floor(ix);
        y = (int)Math.Floor(iy);
        return image.TryReadPixel(x, y, out value);
    }

    void DrawPerfOverlay()
    {
        float scale = (float)_dpiScale;
        // Below the name and readout that a comparison shows in this corner, or the name of an image on its own.
        ImGui.SetNextWindowPos(new Vector2(ViewX + 10 * scale, ViewY + (Comparing ? 56 : _paneLabels && _shown != null ? 34 : 10) * scale));
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
            if (_shown is { } image)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("this image, ms"u8);
                if (Utf8.TryWrite(_utf8, $"{image.ReadMs,8:0.0}  read file", out n)) Put(n);
                if (image.PreviewWidth > 0 && Utf8.TryWrite(_utf8, $"{image.PreviewDecodeMs,8:0.0}  decode preview {image.PreviewWidth} x {image.PreviewHeight}", out n)) Put(n);
                if (image.FullDecodedAt != 0 && Utf8.TryWrite(_utf8, $"{image.FullDecodeMs,8:0.0}  decode full\n{image.MipMs,8:0.0}  mip levels + statistics", out n)) Put(n);
                if (image.GpuFull is { } gpuImage && Utf8.TryWrite(_utf8, $"{image.UploadMs,8:0.0}  upload {gpuImage.ByteCount / 1048576.0:0} MB in {gpuImage.Tiles.Count} texture(s)", out n)) Put(n);
            }
            ImGui.Spacing();
            ImGui.TextDisabled("navigation, ms from key to first frame"u8);
            if (Utf8.TryWrite(_utf8, $"{_lastStepMs,8:0.0}  last step\n{_stepMs / Math.Max(_steps, 1),8:0.0}  mean of {_steps}\n{_stepMaxMs,8:0.0}  longest", out n)) Put(n);
            ImGui.Spacing();
            ImGui.TextDisabled("cache"u8);
            if (Utf8.TryWrite(_utf8, $"{_pipeline.Count,8}  images\n{_pipeline.CpuBytes / 1048576.0,8:0}  MB decoded\n{_pipeline.GpuBytes / 1048576.0,8:0}  MB textures", out n)) Put(n);
            if (_folder is { } folder)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("folder index, ms"u8);
                if (Utf8.TryWrite(_utf8, $"{folder.EnumerateMs,8:0.0}  read {folder.Count} entries\n{folder.SortMs,8:0.0}  sort", out n)) Put(n);
                if (_thumbScreenMs >= 0 && Utf8.TryWrite(_utf8, $"{_thumbScreenMs,8:0.0}  first screen of thumbnails", out n)) Put(n);
            }
            ImGui.Spacing();
            if (Utf8.TryWrite(_utf8, $"{_lastFrameMs,8:0.00}  last frame (CPU)", out n)) Put(n);
        }
        ImGui.End();
    }

    void DrawDisplayPanel()
    {
        LoadedImage? image = _shown;
        float scale = (float)_dpiScale;
        // In the bottom right corner: the top corners are where a comparison names its images.
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth - 10 * scale, ViewY + ViewHeight - 10 * scale), ImGuiCond.Always, new Vector2(1, 1));
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
            if (changed)
            {
                _display.WindowLow = low / unit;
                _display.WindowHigh = high / unit;
                _windowTouched = true;
            }
            ImGui.SliderFloat("gamma"u8, ref _display.Gamma, 0.2f, 5f);
            ImGui.SliderFloat("exposure, stops"u8, ref _display.Exposure, -8f, 8f, "%+.2f"u8);
            ImGui.PopItemWidth();
            if (ImGui.Button("Auto (min - max)"u8) && image != null)
            {
                // Of the image; of all of them when comparing, so that they stay shown alike.
                (float min, float max) = image.ValueRange;
                if (Comparing && CompareValueRange(out float setMin, out float setMax)) (min, max) = (setMin, setMax);
                _display.WindowLow = min;
                _display.WindowHigh = max;
                _windowTouched = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset"u8))
            {
                _display.WindowLow = 0;
                _display.WindowHigh = 1;
                _display.Gamma = 1;
                _display.Exposure = 0;
                _display.Channel = ChannelView.Color;
                _windowTouched = true;
            }

            // Which part of the pixels is shown (H goes through them).
            for (ChannelView channel = ChannelView.Color; channel <= ChannelView.Luma; channel++)
            {
                if (channel != ChannelView.Color) ImGui.SameLine();
                Utf8Of(ChannelName(channel));
                fixed (byte* label = _utf8)
                    if (ImGui.RadioButton(label, _display.Channel == channel)) _display.Channel = channel;
            }
            ImGui.Checkbox("Exact pixels when zoomed in (I)"u8, ref _display.Sharp);
            ImGui.Checkbox("Keep zoom and position across images (L)"u8, ref _keepView);
        }
        ImGui.End();
    }

    /// <summary>Errors, and the hint in an empty window, centred over the view.</summary>
    void DrawMessage()
    {
        if (Comparing) return; // each pane says for itself what is wrong with its image
        string? text = _shown?.Error ?? (_shown == null && FileCount == 0 ? "Drop images here" : null);
        if (text == null || !Utf8.TryWrite(_utf8, $"{text}", out int n)) return;
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight / 2f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.6f);
        if (ImGui.Begin("message"u8, PinnedWindow | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs))
        {
            if (_shown?.Error != null && Utf8.TryWrite(_utf8.AsSpan(1024), $"{Path.GetFileName(_shown.Path.AsSpan())}", out int m))
            {
                fixed (byte* name = &_utf8[1024]) ImGui.TextUnformatted(name, name + m);
            }
            Put(n);
        }
        ImGui.End();
    }
}
