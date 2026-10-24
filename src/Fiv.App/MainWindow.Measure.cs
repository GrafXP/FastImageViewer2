using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Measuring;
using Fiv.Render;
using Hexa.NET.ImGui;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Fiv.App;

// Measuring: lines, rectangles and circles drawn on the image, and what they say about it. A
// line has a length, a direction and the values of the image along it; a rectangle or a circle
// has a size and the values of the pixels inside: their numbers and their histogram.
//
// The shapes are on the pixel grid on show: the upright image, or the canvas of a comparison.
// They stay where they are when the image changes, so the same line or area can be looked at
// in every image of a folder, and in a comparison in all of its images at once: each image of
// the set has its curves in the plot and its rows in the tables, and can be switched off there.
//
// What is drawn over the image is for looking at. The values are taken from the full-resolution
// pixels as the files have them (Fiv.Measuring): along a line at once, of an area on a worker.
internal sealed unsafe partial class MainWindow
{
    const uint ColorMeasure = 0xFFF0D040, ColorMeasureSelected = 0xFFFFFFFF, ColorCurve = 0xFFE8E8E8;

    /// <summary>
    /// A colour for each image of a comparison, by its place in the set: what its curve is
    /// drawn in where the channel does not say (a grey value, luma, alpha), and its mark in the list.
    /// </summary>
    static readonly uint[] ImageColors =
    [
        0xFFE8E8E8, 0xFF40E0FF, 0xFFF0D840, 0xFFF070F0, 0xFF3098FF, 0xFF70F080, 0xFFFFA878, 0xFFB8A0FF,
        0xFF30E0C0, 0xFFFF90B0, 0xFFA0C030, 0xFF7080FF, 0xFF80B8D8, 0xFFFFE0A0, 0xFF50B0A8, 0xFF9060E0,
    ];

    bool _measure;                        // the measurements and their panel are on show
    readonly List<MeasureShape> _shapes = [];
    int _selectedShape = -1;
    MeasureKind? _measureArmed;           // the next drag on the image draws one of these

    bool _dragMeasure;                    // the drag under way draws or changes a measurement
    bool _drawingShape, _measureConstrain;
    MeasureHandle _measureHandle;
    MeasureShape _measureDragStart;
    double _measureGrabX, _measureGrabY;
    int _measureDragItem = -1;            // the image of the set in whose pane the drag began

    /// <summary>What one pixel is long in <see cref="_unit"/>; 0: lengths are given in pixels only.</summary>
    double _pixelSize;
    string _unit = "mm";
    readonly byte[] _unitBuffer = new byte[24];
    bool _histogramLog;
    float _panelRight;                    // the right edge of the panel's contents, in window pixels: where a row of them has to end
    float _panelWrap;                     // the same edge counted from the panel's own left edge: where its text wraps

    // ---------------------------------------------------------------- the grid

    /// <summary>The upright image shown on its own, to window pixels.</summary>
    Affine2 UprightToScreen() => Affine2.ScaleTranslate(1, 1, ViewX, ViewY).After(_view.ToScreen(ViewWidth, ViewHeight));

    /// <summary>The size of the pixel grid the measurements are on: the canvas of a comparison, or the upright image. False if nothing is on show.</summary>
    bool TryGetGridSize(out double width, out double height)
    {
        (width, height) = (0, 0);
        if (Comparing) (width, height) = (_canvasWidth, _canvasHeight);
        else if (_shown is { HasSize: true } image) (width, height) = (image.UprightWidth, image.UprightHeight);
        return width > 0 && height > 0;
    }

    /// <summary>The grid under a point of the window: how it lies there, and, in a comparison, the image of the set whose pane that is. False if there is none.</summary>
    bool TryGetGridAt(int x, int y, out Affine2 toScreen, out int item)
    {
        toScreen = default;
        item = -1;
        if (Comparing)
        {
            LayoutCompare();
            int p = PaneAt(x, y);
            if (p < 0 || _panes[p].First < 0 || _canvasWidth <= 0) return false;
            item = _panes[p].First;
            toScreen = CanvasToScreen(item, _panes[p].Rect);
            return true;
        }
        if (_shown is not { HasSize: true } || x < ViewX || y < ViewY || x >= ViewX + ViewWidth || y >= ViewY + ViewHeight) return false;
        toScreen = UprightToScreen();
        return true;
    }

    /// <summary>The grid as it lies in the pane the drag under way began in, wherever the mouse is by now.</summary>
    bool TryGetDragGrid(out Affine2 toScreen)
    {
        toScreen = default;
        if (!Comparing)
        {
            if (_shown is not { HasSize: true }) return false;
            toScreen = UprightToScreen();
            return true;
        }
        for (int p = 0; p < _paneCount; p++)
        {
            if (_panes[p].First != _measureDragItem || _canvasWidth <= 0) continue;
            toScreen = CanvasToScreen(_panes[p].First, _panes[p].Rect);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The point of the grid under the mouse. Zoomed out, a screen pixel is one image pixel or
    /// several: with <paramref name="toCentres"/> the point is the centre of the pixel under the
    /// mouse, so that a line runs from pixel to pixel. Zoomed in, it is where the mouse is, to
    /// the fraction of a pixel the screen shows.
    /// </summary>
    static (double X, double Y) GridPoint(in Affine2 toScreen, int x, int y, bool toCentres)
    {
        (double gx, double gy) = toScreen.Inverse().Apply(x + 0.5, y + 0.5);
        if (toCentres && toScreen.A <= 1) (gx, gy) = (Math.Floor(gx) + 0.5, Math.Floor(gy) + 0.5);
        return (gx, gy);
    }

    /// <summary>The measurement at a point of the window, and what of it: the selected one first, whose handles are on show, then the one drawn last. -1 if none.</summary>
    int HitShape(in Affine2 toScreen, int x, int y, out MeasureHandle handle)
    {
        (double gx, double gy) = GridPoint(toScreen, x, y, toCentres: false);
        double tolerance = 6 * _dpiScale / toScreen.A;
        if (_selectedShape >= 0 && _selectedShape < _shapes.Count)
        {
            handle = _shapes[_selectedShape].HitTest(gx, gy, tolerance);
            if (handle != MeasureHandle.None) return _selectedShape;
        }
        for (int i = _shapes.Count - 1; i >= 0; i--)
        {
            handle = _shapes[i].HitTest(gx, gy, tolerance);
            if (handle != MeasureHandle.None) return i;
        }
        handle = MeasureHandle.None;
        return -1;
    }

    // ---------------------------------------------------------------- input

    void SetMeasure(bool on)
    {
        if (on == _measure) return;
        if (on) EndSurface(); // the shapes are drawn on the flat image
        _measure = on;
        _measureArmed = null;
        _dragMeasure = _drawingShape = false;
        if (on && _shapes.Count == 0) Notice("Measuring: L for a line, R for a rectangle, C for a circle, then drag on the image.");
    }

    /// <summary>Makes the next drag on the image draw a shape of this kind; asked for again, takes that back.</summary>
    void ArmMeasure(MeasureKind kind)
    {
        _measure = true;
        _measureArmed = _measureArmed == kind ? null : kind;
        // A drag can only draw one thing: not the numbers' region or the alignment's as well.
        if (_measureArmed != null) _regionArmed = _alignRegionArmed = false;
    }

    void RemoveShape(int index)
    {
        if (index < 0 || index >= _shapes.Count) return;
        _shapes.RemoveAt(index);
        _selectedShape = Math.Min(index, _shapes.Count - 1);
    }

    void ClearShapes()
    {
        _shapes.Clear();
        _selectedShape = -1;
    }

    /// <summary>The keys of measuring. True if the key was one of them.</summary>
    bool MeasureKey(VIRTUAL_KEY key, bool shift, bool control)
    {
        if (control || shift) return false;
        if (key == VIRTUAL_KEY.VK_U)
        {
            SetMeasure(!_measure);
            return true;
        }
        if (!_measure) return false;
        switch (key)
        {
            // While the measurements are on show these three choose a shape, and Del is theirs, not the file's.
            case VIRTUAL_KEY.VK_L:
                ArmMeasure(MeasureKind.Line);
                return true;
            case VIRTUAL_KEY.VK_R:
                ArmMeasure(MeasureKind.Rectangle);
                return true;
            case VIRTUAL_KEY.VK_C:
                ArmMeasure(MeasureKind.Circle);
                return true;
            case VIRTUAL_KEY.VK_DELETE:
                if (_selectedShape >= 0) RemoveShape(_selectedShape);
                else Notice("No measurement is selected. (To delete the file, close the measurements first: U.)");
                return true;
            case VIRTUAL_KEY.VK_ESCAPE when !_showKeys && !_showSettings && !_showFileTypes:
                if (_measureArmed != null) _measureArmed = null;
                else SetMeasure(false);
                return true;
        }
        return false;
    }

    /// <summary>
    /// The left button went down over the image. True if the drag that follows is a
    /// measurement's: it draws the shape that was asked for, or changes or moves the one that
    /// was taken hold of. With <paramref name="shift"/> it is constrained (see <see cref="MeasureShape.Drawn"/>).
    /// </summary>
    bool MeasureMouseDown(int x, int y, bool shift)
    {
        if (!_measure) return false;
        if (!TryGetGridAt(x, y, out Affine2 toScreen, out int item) || !TryGetGridSize(out double width, out double height)) return false;
        if (_measureArmed is { } kind)
        {
            (_measureGrabX, _measureGrabY) = GridPoint(toScreen, x, y, toCentres: kind != MeasureKind.Rectangle);
            _shapes.Add(MeasureShape.Drawn(kind, _measureGrabX, _measureGrabY, _measureGrabX, _measureGrabY, false, width, height));
            _selectedShape = _shapes.Count - 1;
            _drawingShape = true;
        }
        else
        {
            int hit = HitShape(toScreen, x, y, out MeasureHandle handle);
            if (hit < 0) return false;
            _selectedShape = hit;
            _measureHandle = handle;
            _measureDragStart = _shapes[hit];
            (_measureGrabX, _measureGrabY) = GridPoint(toScreen, x, y, toCentres: _shapes[hit].Kind != MeasureKind.Rectangle);
            _drawingShape = false;
        }
        _dragMeasure = true;
        _measureConstrain = shift;
        _measureDragItem = item;
        return true;
    }

    void MeasureDrag(int x, int y)
    {
        if (!_dragMeasure || _selectedShape < 0 || _selectedShape >= _shapes.Count) return;
        if (!TryGetDragGrid(out Affine2 toScreen) || !TryGetGridSize(out double width, out double height)) return;
        MeasureKind kind = _shapes[_selectedShape].Kind;
        (double gx, double gy) = GridPoint(toScreen, x, y, toCentres: kind != MeasureKind.Rectangle);
        _shapes[_selectedShape] = _drawingShape
            ? MeasureShape.Drawn(kind, _measureGrabX, _measureGrabY, gx, gy, _measureConstrain, width, height)
            : _measureDragStart.Dragged(_measureHandle, _measureGrabX, _measureGrabY, gx, gy, _measureConstrain, width, height);
    }

    void EndMeasureDrag()
    {
        if (!_dragMeasure) return;
        _dragMeasure = false;
        if (!_drawingShape) return;
        _drawingShape = false;
        // Smaller than three screen pixels: a click, not a shape. The tool stays ready for the drag that was meant.
        double zoom = TryGetDragGrid(out Affine2 toScreen) ? toScreen.A : 1;
        if (_selectedShape >= 0 && _selectedShape < _shapes.Count && _shapes[_selectedShape].Extent * zoom < 3 * _dpiScale)
        {
            RemoveShape(_selectedShape);
            _selectedShape = _shapes.Count - 1;
            return;
        }
        _measureArmed = null;
    }

    // ---------------------------------------------------------------- what the numbers are of

    /// <summary>
    /// One image whose values a measurement shows: its full-resolution pixels, the transform
    /// from them to the grid, what its values are multiplied by so that images of different
    /// depth can be read against each other, and which image of the set it is (-1: the image
    /// on show, which is not being compared).
    /// </summary>
    readonly record struct MeasureSource(DecodedImage Image, Affine2 ToGrid, float Scale, int Item);

    /// <summary>The images the values are taken of now, in the order of the set: every image of a comparison that is loaded in full and not switched off.</summary>
    readonly List<MeasureSource> _sources = [];

    /// <summary>For each image of the set its place in <see cref="_sources"/>, which decides its line style; -1 if it is not among them.</summary>
    readonly int[] _rankOfItem = new int[MaxCompare];

    readonly PixelLayout[] _sourceLayouts = new PixelLayout[MaxCompare];
    readonly float[] _sourceScales = new float[MaxCompare];

    /// <summary>Works out <see cref="_sources"/> and the units their values are in together. False if there are none.</summary>
    bool GatherMeasureSources(out ValueUnit unit)
    {
        _sources.Clear();
        Array.Fill(_rankOfItem, -1);
        unit = ValueUnit.Bits8;
        if (!Comparing)
        {
            if (_shown is not { Full: { } single } image) return false; // the values are of the full image only
            unit = CompareMetrics.Units(single.Layout, single.Layout).Unit;
            _sources.Add(new MeasureSource(single, image.Orientation.ToUpright(image.Width, image.Height), 1, -1));
            return true;
        }
        if (_canvasWidth <= 0) return false;
        for (int i = 0; i < _compare.Count; i++)
        {
            CompareItem entry = _compare[i];
            if (!entry.Measured || entry.Image is not { HasSize: true, Full: { } full }) continue;
            _rankOfItem[i] = _sources.Count;
            _sourceLayouts[_sources.Count] = full.Layout;
            _sources.Add(new MeasureSource(full, ImageToCanvas(i), 1, i));
        }
        if (_sources.Count == 0) return false;
        unit = MeasureUnits.Common(_sourceLayouts.AsSpan(0, _sources.Count), _sourceScales);
        for (int s = 0; s < _sources.Count; s++) _sources[s] = _sources[s] with { Scale = _sourceScales[s] };
        return true;
    }

    static bool SameSources(ReadOnlySpan<MeasureSource> a, ReadOnlySpan<MeasureSource> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // The values along the selected line: a profile for each of the sources they were taken of.
    readonly List<MeasureSource> _profileSources = [];
    readonly List<LineProfile> _profiles = [];
    MeasureShape _profileShape;
    int _profileHover = -1; // the sample of the profile the mouse is at in the plot, to be marked on the image

    /// <summary>Takes the values along the selected line again if the line or what is under it has changed. On this thread: a few thousand samples an image at most.</summary>
    void UpdateProfile(in MeasureShape line)
    {
        if (_profiles.Count > 0 && line == _profileShape && SameSources(CollectionsMarshal.AsSpan(_sources), CollectionsMarshal.AsSpan(_profileSources))) return;
        _profileShape = line;
        _profileSources.Clear();
        _profileSources.AddRange(_sources);
        _profiles.Clear();
        foreach (MeasureSource source in _sources)
            _profiles.Add(LineProfile.Sample(new CompareSource(source.Image, source.ToGrid), line, source.Scale));
    }

    // The numbers of the selected area: one set for each of the sources they were taken of.
    readonly List<MeasureSource> _areaSources = [];
    List<AreaStatistics> _areas = [];
    MeasureShape _areaShape, _areaFailedShape;
    MeasureSource[]? _areaFailedSources;
    bool _areaRunning;
    int _areaRun;
    string? _areaError;
    CancellationTokenSource? _areaCancel;

    /// <summary>True if the numbers on hand are of this area and of the images as they are now.</summary>
    bool AreaCurrent(in MeasureShape area) =>
        _areas.Count > 0 && area == _areaShape && SameSources(CollectionsMarshal.AsSpan(_sources), CollectionsMarshal.AsSpan(_areaSources));

    /// <summary>
    /// Starts the numbers of the selected area if they are neither there nor on their way. One
    /// run at a time: while a shape is being dragged, each run is of the shape as it was when the
    /// run before it ended, and the panel follows as fast as the area can be gone through.
    /// </summary>
    void UpdateArea(in MeasureShape area)
    {
        if (AreaCurrent(area) || _areaRunning) return;
        if (_areaError != null && area == _areaFailedShape && _areaFailedSources != null &&
            SameSources(CollectionsMarshal.AsSpan(_sources), _areaFailedSources)) return; // it failed for exactly this: do not try for ever
        var cancel = _areaCancel = new CancellationTokenSource();
        int run = ++_areaRun;
        _areaRunning = true;
        _areaError = null;
        MeasureShape shape = area;
        MeasureSource[] wanted = [.. _sources];
        // The worker keeps the pixels alive, whatever the cache does with the images meanwhile.
        foreach (MeasureSource source in wanted) source.Image.AddReference();
        Task.Run(() =>
        {
            List<AreaStatistics>? results = new(wanted.Length);
            string? error = null;
            try
            {
                foreach (MeasureSource source in wanted)
                {
                    AreaStatistics? one = AreaStatistics.Compute(new CompareSource(source.Image, source.ToGrid), shape, source.Scale, cancel.Token);
                    if (one == null)
                    {
                        results = null; // cancelled
                        break;
                    }
                    results.Add(one);
                }
            }
            catch (Exception e)
            {
                CrashLog.Write(e);
                error = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
                results = null;
            }
            finally
            {
                foreach (MeasureSource source in wanted) source.Image.Dispose();
            }
            RunOnUi(() => OnArea(run, wanted, shape, results, error));
        });
    }

    /// <summary>A worker has the numbers of an area. UI thread.</summary>
    void OnArea(int run, MeasureSource[] sources, MeasureShape shape, List<AreaStatistics>? results, string? error)
    {
        if (run != _areaRun) return;
        _areaRunning = false;
        _areaError = error;
        if (error != null) (_areaFailedSources, _areaFailedShape) = (sources, shape);
        if (results == null) return; // cancelled or failed
        _areas = results;
        _areaShape = shape;
        _areaSources.Clear();
        _areaSources.AddRange(sources);
        _barsStale = true; // the histogram is drawn from these: laid out again
        if (_options.PerfLog == null) return;
        try
        {
            double ms = 0;
            foreach (AreaStatistics area in results) ms += area.Milliseconds;
            File.AppendAllText(_options.PerfLog,
                $"measure_area kind={shape.Kind} pixels={(results.Count > 0 ? results[0].Pixels : 0)} ms={ms:F1} images={results.Count} at={Stopwatch.GetTimestamp()}\n");
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- drawing on the image

    /// <summary>The measurements over the image: in the view, or in every pane of a comparison. Sets the mouse cursor's shape over them.</summary>
    void DrawMeasurements()
    {
        if (!_measure) return;
        float scale = (float)_dpiScale;
        int hover = -1;
        MeasureHandle hoverHandle = MeasureHandle.None;
        bool overImage = false;
        if (!_dragMeasure && TryGetGridAt(_mouseX, _mouseY, out Affine2 under, out _))
        {
            overImage = true;
            if (_measureArmed == null) hover = HitShape(under, _mouseX, _mouseY, out hoverHandle);
        }
        if (_shapes.Count > 0)
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            if (Comparing)
            {
                for (int p = 0; p < _paneCount; p++)
                {
                    Pane pane = _panes[p];
                    if (pane.First < 0 || _canvasWidth <= 0) continue;
                    PaneRect r = pane.Rect;
                    draw.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Bottom), true);
                    DrawShapes(draw, CanvasToScreen(pane.First, r), r, hover, scale);
                    draw.PopClipRect();
                }
            }
            else if (_shown is { HasSize: true })
            {
                var r = new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
                draw.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Bottom), true);
                DrawShapes(draw, UprightToScreen(), r, hover, scale);
                draw.PopClipRect();
            }
        }

        if (_dragMeasure) ImGui.SetMouseCursor(_drawingShape ? ImGuiMouseCursor.ResizeAll : CursorOf(_measureHandle));
        else if (hover >= 0) ImGui.SetMouseCursor(CursorOf(hoverHandle));
        else if (_measureArmed != null && overImage) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

        static ImGuiMouseCursor CursorOf(MeasureHandle handle) => handle switch
        {
            MeasureHandle.TopLeft or MeasureHandle.BottomRight => ImGuiMouseCursor.ResizeNwse,
            MeasureHandle.TopRight or MeasureHandle.BottomLeft => ImGuiMouseCursor.ResizeNesw,
            MeasureHandle.Top or MeasureHandle.Bottom => ImGuiMouseCursor.ResizeNs,
            MeasureHandle.Left or MeasureHandle.Right => ImGuiMouseCursor.ResizeEw,
            MeasureHandle.Body => ImGuiMouseCursor.Hand,
            _ => ImGuiMouseCursor.ResizeAll,
        };
    }

    void DrawShapes(ImDrawListPtr draw, in Affine2 toScreen, in PaneRect r, int hover, float scale)
    {
        Affine2 m = toScreen;
        float zoom = (float)m.A, lineHeight = ImGui.GetTextLineHeight();
        Vector2 Screen(double x, double y)
        {
            (double sx, double sy) = m.Apply(x, y);
            return new Vector2((float)sx, (float)sy);
        }

        for (int i = 0; i < _shapes.Count; i++)
        {
            MeasureShape shape = _shapes[i];
            bool selected = i == _selectedShape, lit = selected || i == hover;
            uint color = selected ? ColorMeasureSelected : ColorMeasure;
            float thickness = (lit ? 2f : 1.5f) * scale;
            Vector2 a = Screen(shape.X0, shape.Y0), b = Screen(shape.X1, shape.Y1), labelAt;
            switch (shape.Kind)
            {
                case MeasureKind.Line:
                    draw.AddLine(a, b, ColorOutline, thickness + 2 * scale);
                    draw.AddLine(a, b, color, thickness);
                    Handle(a, lit ? 4 : 2.5f);
                    Handle(b, lit ? 4 : 2.5f);
                    labelAt = (a + b) / 2 + new Vector2(8, 8) * scale;
                    break;
                case MeasureKind.Rectangle:
                {
                    a = new Vector2(MathF.Round(a.X), MathF.Round(a.Y));
                    b = new Vector2(MathF.Round(b.X), MathF.Round(b.Y));
                    draw.AddRect(a - Vector2.One, b + Vector2.One, ColorOutline, 0, 0, thickness + 2 * scale);
                    draw.AddRect(a, b, color, 0, 0, thickness);
                    if (lit)
                    {
                        Vector2 middle = (a + b) / 2;
                        Handle(a, 4);
                        Handle(new Vector2(b.X, a.Y), 4);
                        Handle(b, 4);
                        Handle(new Vector2(a.X, b.Y), 4);
                        // The middles of the sides only where the rectangle is large enough to tell them from the corners.
                        if (b.X - a.X > 40 * scale)
                        {
                            Handle(new Vector2(middle.X, a.Y), 3);
                            Handle(new Vector2(middle.X, b.Y), 3);
                        }
                        if (b.Y - a.Y > 40 * scale)
                        {
                            Handle(new Vector2(a.X, middle.Y), 3);
                            Handle(new Vector2(b.X, middle.Y), 3);
                        }
                    }
                    // Its label above its top left corner, or inside where there is no room above.
                    float above = a.Y - lineHeight - 8 * scale;
                    labelAt = new Vector2(MathF.Max(a.X, r.X + 2 * scale), above >= r.Y ? above : MathF.Max(a.Y, r.Y) + 4 * scale);
                    break;
                }
                default:
                {
                    float radius = (float)shape.Radius * zoom, arm = 5 * scale;
                    draw.AddCircle(a, radius, ColorOutline, 0, thickness + 2 * scale);
                    draw.AddCircle(a, radius, color, 0, thickness);
                    draw.AddLine(a - new Vector2(arm, 0), a + new Vector2(arm, 0), ColorOutline, thickness + 2 * scale);
                    draw.AddLine(a - new Vector2(0, arm), a + new Vector2(0, arm), ColorOutline, thickness + 2 * scale);
                    draw.AddLine(a - new Vector2(arm, 0), a + new Vector2(arm, 0), color, thickness);
                    draw.AddLine(a - new Vector2(0, arm), a + new Vector2(0, arm), color, thickness);
                    if (lit)
                    {
                        Handle(a + new Vector2(radius, 0), 3);
                        Handle(a - new Vector2(radius, 0), 3);
                        Handle(a + new Vector2(0, radius), 3);
                        Handle(a - new Vector2(0, radius), 3);
                    }
                    labelAt = a + new Vector2(8, 8) * scale;
                    break;
                }
            }
            WriteShapeLabel(i, shape);
            Plate(draw, MathF.Round(labelAt.X), MathF.Round(labelAt.Y), color);

            void Handle(Vector2 at, float half)
            {
                var size = new Vector2(half, half) * scale;
                at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
                draw.AddRectFilled(at - size - Vector2.One, at + size + Vector2.One, ColorOutline);
                draw.AddRectFilled(at - size, at + size, color);
            }
        }

        // The sample of the profile the mouse is at in the plot: where on the line that is.
        if (_profileHover >= 0 && _profiles.Count > 0 && _selectedShape >= 0 && _selectedShape < _shapes.Count &&
            _profiles[0].Line == _shapes[_selectedShape] && _profileHover < _profiles[0].Count)
        {
            (double x, double y) = _profiles[0].PointAt(_profileHover);
            Vector2 at = Screen(x, y);
            draw.AddCircle(at, 6 * scale, ColorOutline, 0, 4 * scale);
            draw.AddCircle(at, 6 * scale, ColorRegion, 0, 2 * scale);
        }
    }

    /// <summary>Writes "2   123.4 px" (or "1.54 mm", "200 x 100", "Ø 80.2") for a measurement into the shared buffer, null-terminated.</summary>
    void WriteShapeLabel(int index, in MeasureShape shape)
    {
        bool scaled = _pixelSize > 0;
        double per = scaled ? _pixelSize : 1;
        int n = Utf8.TryWrite(_utf8, $"{index + 1}   ", out int written) ? written : 0;
        switch (shape.Kind)
        {
            case MeasureKind.Line:
                n = Add(n, shape.Length * per);
                break;
            case MeasureKind.Rectangle:
                n = Add(n, shape.Width * per);
                n = Add(n, " x "u8);
                n = Add(n, shape.Height * per);
                break;
            default:
                n = Add(n, "Ø "u8);
                n = Add(n, shape.Diameter * per);
                break;
        }
        n = Add(n, " "u8);
        n = scaled ? AddText(n, _unit) : shape.Kind == MeasureKind.Line ? Add(n, "px"u8) : n - 1;
        _utf8[n] = 0;
    }

    // ---------------------------------------------------------------- text and layout

    /// <summary>Appends to the text in the shared buffer, which is <paramref name="n"/> bytes long so far; returns its new length.</summary>
    int Add(int n, ReadOnlySpan<byte> text)
    {
        if (n + text.Length >= _utf8.Length) return n;
        text.CopyTo(_utf8.AsSpan(n));
        return n + text.Length;
    }

    /// <summary>Appends a number with about four significant digits.</summary>
    int Add(int n, double number) => n + FormatNumber(number, _utf8.AsSpan(n, Math.Min(32, _utf8.Length - n - 1)));

    /// <summary>Appends a position or a length in pixels: to a hundredth of a pixel, however large.</summary>
    int AddPixels(int n, double pixels) => Utf8.TryWrite(_utf8.AsSpan(n, _utf8.Length - n - 1), $"{pixels:0.##}", out int written) ? n + written : n;

    int AddText(int n, ReadOnlySpan<char> text) => n + Utf8Of(text, n);

    /// <summary>Appends " = 1.543 mm" if there is a scale; <paramref name="power"/> 2 for an area.</summary>
    int AddScaled(int n, double pixels, int power = 1)
    {
        if (_pixelSize <= 0) return n;
        n = Add(n, " = "u8);
        n = Add(n, pixels * (power == 2 ? _pixelSize * _pixelSize : _pixelSize));
        n = Add(n, " "u8);
        n = AddText(n, _unit);
        return power == 2 ? Add(n, "²"u8) : n;
    }

    /// <summary>Draws the first <paramref name="length"/> bytes of the text buffer, on as many lines as the panel's width makes of them.</summary>
    void PutWrapped(int length)
    {
        ImGui.PushTextWrapPos(_panelWrap);
        Put(length);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A remark in the dimmed colour, on as many lines as the panel's width makes of it.</summary>
    void Remark(ReadOnlySpan<byte> text)
    {
        ImGui.PushTextWrapPos(_panelWrap);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Puts what comes next on the line of what came last, if something <paramref name="width"/>
    /// pixels wide still fits into the panel there; otherwise it starts a new line, as a word
    /// does in a paragraph. The panel can be made narrow, and docked.
    /// </summary>
    void Flow(float width)
    {
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= _panelRight) ImGui.SameLine();
    }

    static float TextWidth(ReadOnlySpan<byte> text)
    {
        fixed (byte* p = text) return ImGui.CalcTextSize(p, p + text.Length).X;
    }

    static float ButtonWidth(ReadOnlySpan<byte> label) => TextWidth(label) + 2 * ImGui.GetStyle().FramePadding.X;

    // ---------------------------------------------------------------- the panel

    static ReadOnlySpan<byte> KindLabel(MeasureKind kind) => kind switch
    {
        MeasureKind.Rectangle => "Rectangle"u8,
        MeasureKind.Circle => "Circle"u8,
        _ => "Line"u8,
    };

    /// <summary>
    /// The measure panel: a window like the folder panel, to be moved, sized and docked. What
    /// is in it follows its width (rows break, the plot is as wide as the panel), and it scrolls
    /// when there is more than it has room for.
    /// </summary>
    void DrawMeasurePanel()
    {
        float scale = (float)_dpiScale;
        // In the top right corner to begin with; from then on where the user puts it, and as large.
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth - 10 * scale, ViewY + 10 * scale), ImGuiCond.FirstUseEver, new Vector2(1, 0));
        ImGui.SetNextWindowSize(new Vector2(600, 660) * scale, ImGuiCond.FirstUseEver);
        bool open = true;
        if (!ImGui.Begin("Measure"u8, ref open, ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.End();
            if (!open) SetMeasure(false);
            return;
        }
        if (!open) _deferred = () => SetMeasure(false);
        _panelRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        // Text wraps at the edge of what is visible, said outright: left to itself, a panel that can scroll
        // sideways wraps at the width of its widest row, and a long line would then keep it that wide.
        _panelWrap = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        int hoverBefore = _profileHover;
        _profileHover = -1;

        Vector4 on = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        float space = ImGui.GetStyle().ItemSpacing.X;
        ToolButton("Line (L)"u8, MeasureKind.Line);
        Flow(ButtonWidth("Rectangle (R)"u8));
        ToolButton("Rectangle (R)"u8, MeasureKind.Rectangle);
        Flow(ButtonWidth("Circle (C)"u8));
        ToolButton("Circle (C)"u8, MeasureKind.Circle);
        Flow(ButtonWidth("Delete (Del)"u8) + space + ButtonWidth("Clear all"u8));
        ImGui.BeginDisabled(_selectedShape < 0);
        if (ImGui.Button("Delete (Del)"u8)) RemoveShape(_selectedShape);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_shapes.Count == 0);
        if (ImGui.Button("Clear all"u8)) ClearShapes();
        ImGui.EndDisabled();
        if (_measureArmed != null)
        {
            ImGui.PushTextWrapPos(_panelWrap);
            ImGui.TextUnformatted("Drag on the image. With Shift: a level, upright or diagonal line; a square."u8);
            ImGui.PopTextWrapPos();
        }
        else if (_shapes.Count == 0)
        {
            Remark("Choose a shape, then drag on the image to draw it."u8);
        }
        else
        {
            Remark("Drag a handle to change one, its outline or centre to move it."u8);
        }

        DrawShapeList();
        if (_selectedShape >= 0 && _selectedShape < _shapes.Count)
        {
            ImGui.Separator();
            EditSelectedShape(scale);
            MeasureShape shape = _shapes[_selectedShape];
            WriteShapeNumbers(shape);
            bool any = GatherMeasureSources(out ValueUnit unit);
            DrawImageChoice(unit, any, scale);
            if (!any)
            {
                bool allOff = Comparing && !_compare.Exists(item => item.Measured);
                Remark(allOff ? "Every image is switched off: tick one above."u8
                    : Comparing ? "Waiting for the images to be loaded in full..."u8 : "Waiting for the image to be loaded in full..."u8);
            }
            else if (shape.Kind == MeasureKind.Line)
            {
                UpdateProfile(shape);
                DrawProfile(scale);
            }
            else
            {
                UpdateArea(shape);
                DrawArea(shape, scale);
            }
        }

        ImGui.Separator();
        DrawScale(scale);
        ImGui.BeginDisabled(_shapes.Count == 0);
        if (ImGui.Button("Save the list... (CSV)"u8)) _deferred = SaveShapeList;
        ImGui.EndDisabled();
        ImGui.End();
        if (_profileHover != hoverBefore) Invalidate(); // the mark on the image follows the mouse in the plot

        void ToolButton(ReadOnlySpan<byte> label, MeasureKind kind)
        {
            bool armed = _measureArmed == kind;
            if (armed) ImGui.PushStyleColor(ImGuiCol.Button, on);
            if (ImGui.Button(label)) ArmMeasure(kind);
            if (armed) ImGui.PopStyleColor();
        }
    }

    /// <summary>The measurements as a table: a click on a row selects.</summary>
    void DrawShapeList()
    {
        if (_shapes.Count == 0) return;
        bool scaled = _pixelSize > 0;
        if (!ImGui.BeginTable("shapes"u8, scaled ? 5 : 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) return;
        ImGui.TableSetupColumn(""u8);
        ImGui.TableSetupColumn("Shape"u8);
        ImGui.TableSetupColumn("Size, pixels"u8);
        if (scaled)
        {
            Utf8Of(_unit);
            fixed (byte* text = _utf8) ImGui.TableSetupColumn(text);
        }
        ImGui.TableSetupColumn("At"u8);
        ImGui.TableHeadersRow();
        for (int i = 0; i < _shapes.Count; i++)
        {
            MeasureShape shape = _shapes[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (Utf8.TryWrite(_utf8, $"{i + 1}\0", out _))
            {
                fixed (byte* text = _utf8)
                    if (ImGui.Selectable(text, i == _selectedShape, ImGuiSelectableFlags.SpanAllColumns)) _selectedShape = i;
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(KindLabel(shape.Kind));
            for (int column = 2; column < (scaled ? 4 : 3); column++)
            {
                double per = column == 2 ? 1 : _pixelSize;
                ImGui.TableSetColumnIndex(column);
                int n = 0;
                switch (shape.Kind)
                {
                    case MeasureKind.Line:
                        n = column == 2 ? AddPixels(n, shape.Length) : Add(n, shape.Length * per);
                        break;
                    case MeasureKind.Rectangle:
                        n = Add(n, shape.Width * per);
                        n = Add(n, " x "u8);
                        n = Add(n, shape.Height * per);
                        break;
                    default:
                        n = Add(n, "Ø "u8);
                        n = column == 2 ? AddPixels(n, shape.Diameter) : Add(n, shape.Diameter * per);
                        break;
                }
                Put(n);
            }
            ImGui.TableSetColumnIndex(scaled ? 4 : 3);
            int m = AddPixels(0, shape.X0);
            m = Add(m, ", "u8);
            m = AddPixels(m, shape.Y0);
            if (shape.Kind == MeasureKind.Line)
            {
                m = Add(m, " to "u8);
                m = AddPixels(m, shape.X1);
                m = Add(m, ", "u8);
                m = AddPixels(m, shape.Y1);
            }
            Put(m);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// <summary>The selected measurement's position and size as numbers that can be typed and stepped.</summary>
    void EditSelectedShape(float scale)
    {
        MeasureShape shape = _shapes[_selectedShape];
        if (!TryGetGridSize(out double width, out double height)) (width, height) = (double.MaxValue, double.MaxValue);
        double x0 = shape.X0, y0 = shape.Y0;
        bool edited;
        float field = 118 * scale, space = ImGui.GetStyle().ItemSpacing.X;
        ImGui.PushItemWidth(field);
        ImGui.AlignTextToFramePadding();
        switch (shape.Kind)
        {
            case MeasureKind.Line:
            {
                double x1 = shape.X1, y1 = shape.Y1;
                ImGui.TextUnformatted("From"u8);
                ImGui.SameLine();
                edited = ImGui.InputDouble("##x0"u8, ref x0, 1, 10, "%.2f"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##y0"u8, ref y0, 1, 10, "%.2f"u8);
                Flow(TextWidth("to"u8) + 2 * (field + space));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("to"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##x1"u8, ref x1, 1, 10, "%.2f"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##y1"u8, ref y1, 1, 10, "%.2f"u8);
                if (edited && double.IsFinite(x0 + y0 + x1 + y1)) _shapes[_selectedShape] = MeasureShape.Line(x0, y0, x1, y1).Within(width, height);
                break;
            }
            case MeasureKind.Rectangle:
            {
                double w = shape.Width, h = shape.Height;
                ImGui.TextUnformatted("At"u8);
                ImGui.SameLine();
                edited = ImGui.InputDouble("##x0"u8, ref x0, 1, 10, "%.0f"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##y0"u8, ref y0, 1, 10, "%.0f"u8);
                Flow(TextWidth("size"u8) + 2 * (field + space));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("size"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##w"u8, ref w, 1, 10, "%.0f"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##h"u8, ref h, 1, 10, "%.0f"u8);
                if (edited && double.IsFinite(x0 + y0 + w + h))
                    _shapes[_selectedShape] = MeasureShape.Rectangle(x0, y0, x0 + Math.Max(1, w), y0 + Math.Max(1, h)).Within(width, height);
                break;
            }
            default:
            {
                double radius = shape.Radius;
                ImGui.TextUnformatted("Centre"u8);
                ImGui.SameLine();
                edited = ImGui.InputDouble("##x0"u8, ref x0, 1, 10, "%.2f"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##y0"u8, ref y0, 1, 10, "%.2f"u8);
                Flow(TextWidth("radius"u8) + field + space);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("radius"u8);
                ImGui.SameLine();
                edited |= ImGui.InputDouble("##r"u8, ref radius, 1, 10, "%.2f"u8);
                if (edited && double.IsFinite(x0 + y0 + radius)) _shapes[_selectedShape] = MeasureShape.Circle(x0, y0, Math.Max(0.5, radius)).Within(width, height);
                break;
            }
        }
        ImGui.PopItemWidth();
    }

    /// <summary>What the shape itself comes to, whatever is under it: in pixels, and in the unit of the scale if there is one.</summary>
    void WriteShapeNumbers(in MeasureShape shape)
    {
        int n;
        switch (shape.Kind)
        {
            case MeasureKind.Line:
                n = Add(0, "Length "u8);
                n = AddPixels(n, shape.Length);
                n = Add(n, " px"u8);
                n = AddScaled(n, shape.Length);
                n = Add(n, "    across "u8);
                n = AddPixels(n, shape.Dx);
                n = Add(n, "    down "u8);
                n = AddPixels(n, shape.Dy);
                n = Add(n, "    angle "u8);
                if (Utf8.TryWrite(_utf8.AsSpan(n), $"{shape.Angle:0.##}°", out int more)) n += more;
                break;
            case MeasureKind.Rectangle:
                n = AddPixels(0, shape.Width);
                n = Add(n, " x "u8);
                n = AddPixels(n, shape.Height);
                n = Add(n, " px    area "u8);
                n = AddPixels(n, shape.Area);
                n = Add(n, " px²"u8);
                n = AddScaled(n, shape.Area, 2);
                n = Add(n, "    perimeter "u8);
                n = AddPixels(n, shape.Perimeter);
                n = AddScaled(n, shape.Perimeter);
                break;
            default:
                n = Add(0, "Diameter "u8);
                n = AddPixels(n, shape.Diameter);
                n = Add(n, " px"u8);
                n = AddScaled(n, shape.Diameter);
                n = Add(n, "    area "u8);
                n = Add(n, shape.Area);
                n = Add(n, " px²"u8);
                n = AddScaled(n, shape.Area, 2);
                n = Add(n, "    circumference "u8);
                n = Add(n, shape.Perimeter);
                n = AddScaled(n, shape.Perimeter);
                break;
        }
        PutWrapped(n);
    }

    /// <summary>The scale: what a pixel is long in a unit of the user's, typed, or worked out from the selected line and its known length.</summary>
    void DrawScale(float scale)
    {
        float field = 110 * scale, space = ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Scale: 1 pixel is"u8);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(field);
        double size = _pixelSize;
        if (ImGui.InputDouble("##pixel"u8, ref size, 0, 0, "%.6g"u8)) _pixelSize = double.IsFinite(size) && size > 0 ? size : 0;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(64 * scale);
        bool renamed;
        fixed (byte* buffer = _unitBuffer) renamed = ImGui.InputText("##unit"u8, buffer, (nuint)_unitBuffer.Length);
        if (renamed) _unit = Encoding.UTF8.GetString(_unitBuffer, 0, Math.Max(0, Array.IndexOf(_unitBuffer, (byte)0))).Trim();
        if (_selectedShape >= 0 && _selectedShape < _shapes.Count && _shapes[_selectedShape] is { Kind: MeasureKind.Line, Length: > 0 } line)
        {
            // A line of known length sets the scale: what is typed here is what the line is long.
            Flow(TextWidth("the selected line is"u8) + space + field);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("the selected line is"u8);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(field);
            double known = line.Length * _pixelSize;
            if (ImGui.InputDouble("##known"u8, ref known, 0, 0, "%.6g"u8)) _pixelSize = double.IsFinite(known) && known > 0 ? known / line.Length : 0;
        }
        if (_pixelSize <= 0)
        {
            Flow(TextWidth("(0: pixels only)"u8));
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("(0: pixels only)"u8);
        }
    }

    // ---------------------------------------------------------------- which images, and how each is drawn

    /// <summary>The colour of an image of the set (-1: the image on show, on its own).</summary>
    static uint ImageColor(int item) => item < 0 ? ColorCurve : ImageColors[item % ImageColors.Length];

    /// <summary>The colour a channel is drawn in: red, green and blue in their own; a grey value, luma and alpha in the colour of the image.</summary>
    static uint ChannelColor(string name, int item) => name switch
    {
        ChannelMetrics.Red => ColorRed,
        ChannelMetrics.Green => ColorGreen,
        ChannelMetrics.Blue => ColorBlue,
        _ => ImageColor(item),
    };

    /// <summary>True if a channel is among those the display shows: all three colours, or the one chosen with H.</summary>
    bool ChannelShown(string name, bool colour) => !colour || _display.Channel switch
    {
        ChannelView.Red => name == ChannelMetrics.Red,
        ChannelView.Green => name == ChannelMetrics.Green,
        ChannelView.Blue => name == ChannelMetrics.Blue,
        ChannelView.Alpha => name == ChannelMetrics.Alpha,
        ChannelView.Luma => name == ChannelMetrics.Luma,
        _ => name is ChannelMetrics.Red or ChannelMetrics.Green or ChannelMetrics.Blue,
    };

    /// <summary>
    /// The line style of the image at a place among those on show: whether a stroke that many
    /// pixels along its curve is drawn. The first is solid, the second dashed, then dotted,
    /// dash-dot, long dashes and sparse dots; after that the styles come round again. Red,
    /// green and blue curves have the same colour in every image: the style is what tells whose they are.
    /// </summary>
    static bool StrokeDrawn(int rank, float along, float scale)
    {
        float p = along / MathF.Max(scale, 0.5f);
        switch (rank % 6)
        {
            case 1: return p % 10 < 5;
            case 2: return p % 5 < 2;
            case 3:
            {
                float within = p % 17;
                return within < 9 || (within >= 12 && within < 14);
            }
            case 4: return p % 16 < 12;
            case 5: return p % 8 < 2;
            default: return true;
        }
    }

    /// <summary>
    /// The images whose values are shown, each with a tick to switch it off and on, a sample of
    /// the line it is drawn with, its number and its name; then the units the values are in.
    /// An image shown on its own has only the units.
    /// </summary>
    void DrawImageChoice(ValueUnit unit, bool any, float scale)
    {
        if (Comparing)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float start = ImGui.GetCursorPosX(), limit = start + MathF.Max(ImGui.GetContentRegionAvail().X, 200 * scale);
            float next = start, sample = 28 * scale, gap = ImGui.GetStyle().ItemInnerSpacing.X, box = ImGui.GetFrameHeight();
            for (int i = 0; i < _compare.Count; i++)
            {
                CompareItem entry = _compare[i];
                if (!Utf8.TryWrite(_utf8, $"{i + 1}  {Path.GetFileName(entry.Path.AsSpan())}\0", out _)) continue;
                float textWidth;
                fixed (byte* text = _utf8) textWidth = ImGui.CalcTextSize(text).X;
                float width = box + gap + sample + gap + textWidth;
                // As many on a line as the panel is wide for.
                if (i > 0 && next + width <= limit) ImGui.SameLine(next, 0);
                else next = start;
                ImGui.PushID(i);
                ImGui.Checkbox("##measured"u8, ref entry.Measured);
                ImGui.SameLine(0, gap);
                Vector2 at = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(sample, box));
                int rank = _rankOfItem[i];
                if (rank >= 0)
                {
                    float middle = MathF.Round(at.Y + box / 2);
                    uint color = ImageColor(i);
                    for (float x = 0; x < sample; x++)
                        if (StrokeDrawn(rank, x, scale)) draw.AddLine(new Vector2(at.X + x, middle), new Vector2(at.X + x + 1, middle), color, 2 * scale);
                }
                ImGui.SameLine(0, gap);
                // The name of an image that is switched off, or not loaded yet, is dimmed.
                fixed (byte* text = _utf8)
                {
                    if (rank >= 0) ImGui.TextUnformatted(text);
                    else TextDim(text);
                }
                ImGui.PopID();
                next += width + 14 * scale;
            }
        }
        if (any && Utf8.TryWrite(_utf8, $"{MetricsReport.UnitName(unit)} values\0", out _))
        {
            fixed (byte* text = _utf8) TextDim(text);
        }
    }

    /// <summary>"Red", or "2  Red" for a channel of the second image of a set (<paramref name="item"/> -1: the image is on its own), in the colour of its curve.</summary>
    void RowName(string channel, int item, uint color)
    {
        int n = item >= 0 && Utf8.TryWrite(_utf8, $"{item + 1}  ", out int written) ? written : 0;
        n = Add(n, ChannelLabel(channel));
        _utf8[n] = 0;
        fixed (byte* text = _utf8) TextIn(ImGui.ColorConvertU32ToFloat4(color), text);
    }

    // ---------------------------------------------------------------- a line: the values along it

    /// <summary>One line of the plot: a channel of one of the images, which is the <paramref name="Rank"/>th of those on show.</summary>
    readonly record struct Curve(float[] Values, ValueSummary Summary, string Name, uint Color, int Rank, int Item);

    readonly List<Curve> _curves = [];

    void DrawProfile(float scale)
    {
        if (_profiles.Count == 0) return;
        LineProfile first = _profiles[0];
        _curves.Clear();
        for (int p = 0; p < _profiles.Count && p < _profileSources.Count; p++)
        {
            LineProfile profile = _profiles[p];
            int item = _profileSources[p].Item;
            foreach (ProfileChannel channel in profile.Channels)
                if (ChannelShown(channel.Name, profile.IsColor)) _curves.Add(new Curve(channel.Values, channel.Summary, channel.Name, ChannelColor(channel.Name, item), p, item));
        }
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (Curve curve in _curves)
        {
            if (curve.Summary.Count == 0) continue;
            low = Math.Min(low, curve.Summary.Min);
            high = Math.Max(high, curve.Summary.Max);
        }
        if (!(low <= high))
        {
            Remark("The line is not on the image."u8);
            return;
        }

        // The range shown: that of the values, with a little room above and below.
        double room = high > low ? (high - low) * 0.06 : 0.5;
        (double bottom, double top) = (low - room, high + room);
        var size = new Vector2(MathF.Max(ImGui.GetContentRegionAvail().X, 160 * scale), 170 * scale);
        Vector2 origin = ImGui.GetCursorScreenPos();
        float rowStart = ImGui.GetCursorPosX();
        ImGui.Dummy(size);
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xFF181818);
        for (int quarter = 1; quarter < 4; quarter++)
        {
            float y = MathF.Round(origin.Y + size.Y * quarter / 4);
            draw.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), 0x18FFFFFF);
        }
        int columns = Math.Max(2, (int)size.X), count = first.Count;
        float thickness = MathF.Max(1, 1.5f * scale);
        float Down(double value) => origin.Y + 1 + (float)((top - value) / (top - bottom)) * (size.Y - 2);

        draw.PushClipRect(origin, origin + size, true);
        foreach (Curve curve in _curves)
        {
            float[] values = curve.Values;
            int n = values.Length;
            float previousX = 0, previousY = 0;
            bool joined = false;
            for (int c = 0; c < columns; c++)
            {
                float x = origin.X + c + 0.5f;
                // Every image has its line style, so that its curves can be told from another's where they run together.
                bool drawn = StrokeDrawn(curve.Rank, c, scale);
                if (n <= columns)
                {
                    // Fewer samples than columns: the value between two samples, on the straight line from one to the other.
                    double t = (double)c / (columns - 1) * (n - 1);
                    int i = Math.Min((int)t, n - 2);
                    float f = (float)(t - i), from = values[i], to = values[i + 1];
                    float value = f <= 0 ? from : f >= 1 ? to : from + (to - from) * f;
                    if (!float.IsFinite(value))
                    {
                        joined = false; // the image has no pixel there: the curve has a gap
                        continue;
                    }
                    float y = Down(value);
                    if (joined && drawn) draw.AddLine(new Vector2(previousX, previousY), new Vector2(x, y), curve.Color, thickness);
                    (previousX, previousY, joined) = (x, y, true);
                    continue;
                }

                // More samples than columns: the column is a stroke from the smallest to the largest
                // of the samples that fall into it, reaching to where the column before it left off.
                int start = (int)((long)c * n / columns), end = Math.Max(start + 1, (int)((long)(c + 1) * n / columns));
                float least = float.PositiveInfinity, most = float.NegativeInfinity, leaving = float.NaN;
                for (int i = start; i < end; i++)
                {
                    float value = values[i];
                    if (!float.IsFinite(value)) continue;
                    leaving = value;
                    least = MathF.Min(least, value);
                    most = MathF.Max(most, value);
                }
                if (!float.IsFinite(leaving))
                {
                    joined = false;
                    continue;
                }
                float upper = Down(most), lower = Down(least);
                if (joined)
                {
                    upper = MathF.Min(upper, previousY);
                    lower = MathF.Max(lower, previousY);
                }
                if (lower - upper < 1) lower = upper + 1; // a level stretch is still a pixel high
                if (drawn) draw.AddLine(new Vector2(x, upper), new Vector2(x, lower), curve.Color, thickness);
                (previousX, previousY, joined) = (x, Down(leaving), true);
            }
        }
        draw.PopClipRect();

        // The range, written into the corners of the plot.
        Span<byte> label = stackalloc byte[32];
        int length = FormatNumber(high, label);
        fixed (byte* text = label) draw.AddText(origin + new Vector2(4, 2) * scale, ColorTextDim, text, text + length);
        length = FormatNumber(low, label);
        fixed (byte* text = label) draw.AddText(new Vector2(origin.X + 4 * scale, origin.Y + size.Y - ImGui.GetTextLineHeight() - 2 * scale), ColorTextDim, text, text + length);

        // The sample the mouse is at: marked here and on the image; its values are in the table below.
        int at = -1;
        if (hovered && count > 1)
        {
            float mouse = Math.Clamp(ImGui.GetMousePos().X - origin.X, 0, columns - 1);
            at = Math.Clamp((int)Math.Round((double)mouse / (columns - 1) * (count - 1)), 0, count - 1);
            float x = origin.X + (float)at / (count - 1) * (columns - 1) + 0.5f;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), 0x90FFFFFF);
            _profileHover = at;
        }

        // Under it: where the line starts and ends, and where the mouse is.
        ImGui.TextUnformatted("0"u8);
        int m = AddPixels(0, first.Line.Length);
        m = Add(m, " px"u8);
        m = AddScaled(m, first.Line.Length);
        float width;
        fixed (byte* text = _utf8) width = ImGui.CalcTextSize(text, text + m).X;
        ImGui.SameLine(MathF.Max(rowStart + 20 * scale, rowStart + size.X - width));
        Put(m);
        if (at >= 0)
        {
            (double px, double py) = first.PointAt(at);
            m = Add(0, "At "u8);
            m = AddPixels(m, first.DistanceAt(at));
            m = Add(m, " px"u8);
            m = AddScaled(m, first.DistanceAt(at));
            m = Add(m, "   x "u8);
            m = AddPixels(m, px);
            m = Add(m, "  y "u8);
            m = AddPixels(m, py);
            PutWrapped(m);
        }
        else
        {
            Remark("Move the mouse over the plot: the values at that point of the line are in the table."u8);
        }

        if (ImGui.BeginTable("profile"u8, 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn(""u8);
            ImGui.TableSetupColumn("Mean"u8);
            ImGui.TableSetupColumn("Std dev"u8);
            ImGui.TableSetupColumn("Min"u8);
            ImGui.TableSetupColumn("Max"u8);
            ImGui.TableSetupColumn("At the mouse"u8);
            ImGui.TableHeadersRow();
            foreach (Curve curve in _curves)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                RowName(curve.Name, curve.Item, curve.Color);
                MetricCell(1, curve.Summary.Mean);
                MetricCell(2, curve.Summary.StdDev);
                MetricCell(3, curve.Summary.Min);
                MetricCell(4, curve.Summary.Max);
                if (at >= 0 && at < curve.Values.Length) MetricCell(5, curve.Values[at]);
            }
            ImGui.EndTable();
        }
        if (first.Covered < first.Count && Utf8.TryWrite(_utf8, $"{first.Count - first.Covered} of {first.Count} samples are off the image.\0", out _))
        {
            fixed (byte* text = _utf8) TextDim(text);
        }
        if (ImGui.Button("Save the profile... (CSV)"u8)) _deferred = SaveProfile;
        Remark("A sample every pixel of length, blended from the four pixels around it."u8);
    }

    // ---------------------------------------------------------------- an area: the values inside it

    /// <summary>A channel of one of the images in the histogram: how many pixels fall into each of its bars.</summary>
    readonly record struct Bars(ChannelStatistics Channel, float[] Counts, uint Color, int Rank, int Item);

    readonly List<Bars> _bars = [];
    bool _barsStale = true;
    ChannelView _barsChannel;
    double _barsLow, _barsHigh, _barsMost;
    int _barCount;

    /// <summary>
    /// Lays the histograms of the channels on show out over one range of values, in bars: made
    /// when the numbers or the channel change, not every frame (a 16-bit image has 65536 bins).
    /// </summary>
    void UpdateBars()
    {
        if (!_barsStale && _barsChannel == _display.Channel) return;
        (_barsStale, _barsChannel) = (false, _display.Channel);
        _bars.Clear();
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        bool whole = true, eightBit = true;
        for (int a = 0; a < _areas.Count && a < _areaSources.Count; a++)
        {
            AreaStatistics area = _areas[a];
            int item = _areaSources[a].Item;
            bool colour = area.Channels.Count > 1;
            whole &= area.Whole;
            eightBit &= area.FullValue == 255;
            foreach (ChannelStatistics channel in area.Channels)
            {
                if (!ChannelShown(channel.Name, colour)) continue;
                _bars.Add(new Bars(channel, [], ChannelColor(channel.Name, item), a, item));
                low = Math.Min(low, channel.Min);
                high = Math.Max(high, channel.Max);
            }
        }
        _barsMost = 0;
        _barCount = 0;
        if (!(low <= high)) return;

        // Whole values: a bar a value where there are few enough of them, and 8-bit images over
        // all of 0 to 255, where one is used to seeing them. Otherwise 256 bars over the range.
        if (whole && eightBit) (low, high) = (0, 255);
        if (whole) (low, high) = (Math.Floor(low) - 0.5, Math.Ceiling(high) + 0.5);
        else if (!(high > low)) (low, high) = (low - 0.5, high + 0.5);
        _barCount = whole && high - low <= 256 ? (int)(high - low) : 256;
        (_barsLow, _barsHigh) = (low, high);
        for (int b = 0; b < _bars.Count; b++)
        {
            ValueHistogram histogram = _bars[b].Channel.Histogram;
            var counts = new float[_barCount];
            for (int i = 0; i < histogram.Counts.Length; i++)
            {
                long count = histogram.Counts[i];
                if (count == 0) continue;
                int bar = Math.Clamp((int)((histogram.ValueAt(i) - low) / (high - low) * _barCount), 0, _barCount - 1);
                counts[bar] += count;
                _barsMost = Math.Max(_barsMost, counts[bar]);
            }
            _bars[b] = _bars[b] with { Counts = counts };
        }
    }

    void DrawArea(in MeasureShape shape, float scale)
    {
        // While a shape is dragged, the numbers of where it was a moment ago are shown until those of
        // where it is are there; the numbers of another measurement are not shown in place of this one's.
        if (_areas.Count == 0 || (_areaShape != shape && !_dragMeasure))
        {
            if (_areaError != null)
            {
                Utf8Of(_areaError);
                fixed (byte* text = _utf8) TextIn(new Vector4(1, 0.5f, 0.4f, 1), text);
            }
            else
            {
                ImGui.TextDisabled("Computing..."u8);
            }
            return;
        }
        long least = long.MaxValue, most = 0, ignored = 0;
        bool blended = false, whole = true;
        double milliseconds = 0;
        foreach (AreaStatistics area in _areas)
        {
            least = Math.Min(least, area.Pixels);
            most = Math.Max(most, area.Pixels);
            ignored += area.Ignored;
            blended |= !area.PixelAligned;
            whole &= area.Whole;
            milliseconds += area.Milliseconds;
        }
        if (most == 0)
        {
            Remark(ignored > 0 ? "No pixel inside has a value that is a number."u8 : "No pixel of the image is inside."u8);
            return;
        }
        int n;
        if (least == most ? Utf8.TryWrite(_utf8, $"{most} pixels are inside.", out n)
                : Utf8.TryWrite(_utf8, $"{least} to {most} pixels are inside: not every image has pixels everywhere in it.", out n)) PutWrapped(n);
        if (ignored > 0 && Utf8.TryWrite(_utf8, $"{ignored} pixels left out: not a number\0", out _))
        {
            fixed (byte* text = _utf8) TextDim(text);
        }
        if (blended) Remark("An image is scaled, turned or moved by part of a pixel to lie where it does: its pixels are blended."u8);

        UpdateBars();
        var size = new Vector2(MathF.Max(ImGui.GetContentRegionAvail().X, 160 * scale), 150 * scale);
        Vector2 origin = ImGui.GetCursorScreenPos();
        float rowStart = ImGui.GetCursorPosX();
        ImGui.Dummy(size);
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xFF181818);
        if (_barsMost > 0 && _barCount > 0)
        {
            double logMost = Math.Log(1 + _barsMost);
            float barWidth = size.X / _barCount, floor = origin.Y + size.Y;
            float Height(float count) => (float)(_histogramLog ? Math.Log(1 + count) / logMost : count / _barsMost) * (size.Y - 2);
            draw.PushClipRect(origin, origin + size, true);
            // The first image's as filled bars, see-through so that three colours can lie over each
            // other; every other image's as a line along the tops of its bars, in its line style.
            foreach (Bars bars in _bars)
            {
                if (bars.Rank != 0) continue;
                uint fill = (bars.Color & 0x00FFFFFF) | (_bars.Count > 1 ? 0x90000000 : 0xD0000000);
                for (int i = 0; i < _barCount; i++)
                {
                    if (bars.Counts[i] <= 0) continue;
                    float x = origin.X + i * barWidth;
                    draw.AddRectFilled(new Vector2(x, floor - MathF.Max(1, Height(bars.Counts[i]))), new Vector2(MathF.Max(x + barWidth - (barWidth >= 3 ? 1 : 0), x + 1), floor), fill);
                }
            }
            foreach (Bars bars in _bars)
            {
                if (bars.Rank == 0) continue;
                float previous = floor, stroke = MathF.Max(1, 1.5f * scale);
                for (int i = 0; i < _barCount; i++)
                {
                    float x = origin.X + i * barWidth, y = bars.Counts[i] > 0 ? floor - MathF.Max(1, Height(bars.Counts[i])) : floor;
                    bool drawn = StrokeDrawn(bars.Rank, i * barWidth, scale);
                    if (y != previous && drawn) draw.AddLine(new Vector2(x, previous), new Vector2(x, y), bars.Color, scale);
                    if (y < floor && drawn) draw.AddLine(new Vector2(x, y), new Vector2(x + barWidth, y), bars.Color, stroke);
                    previous = y;
                }
                if (previous != floor) draw.AddLine(new Vector2(origin.X + size.X - 1, previous), new Vector2(origin.X + size.X - 1, floor), bars.Color, scale);
            }
            draw.PopClipRect();
        }

        // Under it: the ends of the range, and what the mouse is at.
        double left = whole ? _barsLow + 0.5 : _barsLow, right = whole ? _barsHigh - 0.5 : _barsHigh;
        n = FormatNumber(left, _utf8);
        Put(n);
        if (size.X > 300 * scale)
        {
            ImGui.SameLine(rowStart + size.X / 2 - 70 * scale);
            ImGui.TextDisabled(_histogramLog ? "pixel values (log scale)"u8 : "pixel values"u8);
        }
        n = FormatNumber(right, _utf8);
        float width;
        fixed (byte* text = _utf8) width = ImGui.CalcTextSize(text, text + n).X;
        ImGui.SameLine(MathF.Max(rowStart + 20 * scale, rowStart + size.X - width));
        Put(n);
        int bar = -1;
        if (hovered && _barCount > 0 && _barsMost > 0)
        {
            bar = Math.Clamp((int)((ImGui.GetMousePos().X - origin.X) / size.X * _barCount), 0, _barCount - 1);
            double span = (_barsHigh - _barsLow) / _barCount, from = _barsLow + bar * span;
            float x = origin.X + (bar + 0.5f) * size.X / _barCount;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), 0x90FFFFFF);
            n = Add(0, "Value "u8);
            if (whole && span <= 1)
            {
                n = Add(n, from + 0.5);
            }
            else
            {
                n = Add(n, whole ? Math.Ceiling(from) : from);
                n = Add(n, " to "u8);
                n = Add(n, whole ? Math.Ceiling(from + span) - 1 : from + span);
            }
            n = Add(n, ": the table counts the pixels that have it."u8);
            PutWrapped(n);
        }
        else
        {
            Remark("Move the mouse over the histogram: the table counts the pixels that have the value there."u8);
        }

        if (ImGui.BeginTable("area"u8, 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn(""u8);
            ImGui.TableSetupColumn("Mean"u8);
            ImGui.TableSetupColumn("Std dev"u8);
            ImGui.TableSetupColumn("Min"u8);
            ImGui.TableSetupColumn("Max"u8);
            ImGui.TableSetupColumn("Median"u8);
            ImGui.TableSetupColumn("Pixels at the mouse"u8);
            ImGui.TableHeadersRow();
            for (int a = 0; a < _areas.Count && a < _areaSources.Count; a++)
            {
                int item = _areaSources[a].Item;
                foreach (ChannelStatistics channel in _areas[a].Channels)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    RowName(channel.Name, item, ChannelColor(channel.Name, item));
                    MetricCell(1, channel.Mean);
                    MetricCell(2, channel.StdDev);
                    MetricCell(3, channel.Min);
                    MetricCell(4, channel.Max);
                    MetricCell(5, channel.Median);
                    if (bar < 0) continue;
                    // Of the channels the histogram shows.
                    foreach (Bars bars in _bars)
                        if (bars.Channel == channel && bar < bars.Counts.Length) MetricCell(6, bars.Counts[bar]);
                }
            }
            ImGui.EndTable();
        }
        ImGui.Checkbox("Log scale"u8, ref _histogramLog);
        Flow(ButtonWidth("Save the numbers... (CSV)"u8));
        if (ImGui.Button("Save the numbers... (CSV)"u8)) _deferred = SaveAreaStatistics;
        Flow(ButtonWidth("Save the histogram... (CSV)"u8));
        if (ImGui.Button("Save the histogram... (CSV)"u8)) _deferred = SaveAreaHistogram;
        if (Utf8.TryWrite(_utf8, $"{milliseconds:0} ms\0", out _))
        {
            Flow(60 * scale);
            fixed (byte* text = _utf8) TextDim(text);
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// "name_what" from the file the values are of; "first_vs_second_what" from two; from more,
    /// "first_and_3_more_what".
    /// </summary>
    string MeasureName(string what, ReadOnlySpan<MeasureSource> sources)
    {
        if (!Comparing || sources.Length == 0) return $"{Path.GetFileNameWithoutExtension(_shown?.Path ?? "image")}_{what}";
        string first = SourceName(sources[0]);
        return sources.Length == 1 ? $"{first}_{what}"
            : sources.Length == 2 ? $"{first}_vs_{SourceName(sources[1])}_{what}"
            : $"{first}_and_{sources.Length - 1}_more_{what}";
    }

    /// <summary>The path of the file a source is of.</summary>
    string SourcePath(in MeasureSource source) =>
        source.Item >= 0 && source.Item < _compare.Count ? _compare[source.Item].Path : _shown?.Path ?? "";

    string SourceName(in MeasureSource source) => Path.GetFileNameWithoutExtension(SourcePath(source)) is { Length: > 0 } name ? name : "image";

    static readonly (string Name, string Extension)[] CsvType = [("CSV table", "csv")];

    void SaveText(string name, string testName, Func<string> text) =>
        SaveTo(name + ".csv", testName, CsvType, path => Export(Path.GetFileName(path), () => File.WriteAllText(path, text())), () => { });

    /// <summary>Saves the measurements, a row each, as CSV.</summary>
    void SaveShapeList()
    {
        if (_shapes.Count == 0)
        {
            Notice("There are no measurements to save.");
            return;
        }
        MeasureShape[] shapes = [.. _shapes];
        double pixelSize = _pixelSize;
        string unit = _unit;
        SaveText(MeasureName("measurements", default), "measurements.csv", () => MeasureReport.ListCsv(shapes, pixelSize, unit));
    }

    /// <summary>Saves the values along the selected line, a row per sample with the values of every image on show, as CSV.</summary>
    void SaveProfile()
    {
        if (_profiles.Count == 0 || _selectedShape < 0 || _selectedShape >= _shapes.Count || _profiles[0].Line != _shapes[_selectedShape])
        {
            Notice("Select a line first: its profile is what is saved.");
            return;
        }
        LineProfile[] profiles = [.. _profiles];
        // In a comparison the columns say which image they are of, by the number it has on screen.
        int[]? numbers = null;
        if (Comparing)
        {
            numbers = new int[_profileSources.Count];
            for (int i = 0; i < numbers.Length; i++) numbers[i] = _profileSources[i].Item + 1;
        }
        double pixelSize = _pixelSize;
        string unit = _unit;
        SaveText(MeasureName("profile", CollectionsMarshal.AsSpan(_profileSources)), "profile.csv", () => MeasureReport.ProfileCsv(profiles, numbers, pixelSize, unit));
    }

    bool TryGetAreaToSave(out AreaStatistics[] areas, out string[] names)
    {
        (areas, names) = ([], []);
        if (_areas.Count == 0 || _selectedShape < 0 || _selectedShape >= _shapes.Count || _areaShape != _shapes[_selectedShape] || _areaRunning)
        {
            Notice("Select a rectangle or a circle first, and wait for its numbers.");
            return false;
        }
        areas = [.. _areas];
        names = new string[areas.Length];
        for (int i = 0; i < names.Length; i++) names[i] = i < _areaSources.Count ? SourcePath(_areaSources[i]) : "";
        return true;
    }

    /// <summary>Saves the numbers of the selected area, a row per image and channel, as CSV.</summary>
    void SaveAreaStatistics()
    {
        if (!TryGetAreaToSave(out AreaStatistics[] areas, out string[] names)) return;
        SaveText(MeasureName("area", CollectionsMarshal.AsSpan(_areaSources)), "area.csv", () => MeasureReport.StatisticsCsv(areas, names));
    }

    /// <summary>Saves how often each value occurs in the selected area as CSV.</summary>
    void SaveAreaHistogram()
    {
        if (!TryGetAreaToSave(out AreaStatistics[] areas, out string[] names)) return;
        SaveText(MeasureName("histogram", CollectionsMarshal.AsSpan(_areaSources)), "histogram.csv", () => MeasureReport.HistogramCsv(areas, names));
    }
}
