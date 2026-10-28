using System.Diagnostics;
using System.Numerics;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Render;
using Fiv.Surface;
using Hexa.NET.ImGui;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Fiv.App;

// The 3D height map. What is on show becomes a surface: an image on its own, raised by the value
// it shows (its brightness, or the channel chosen with H), and while comparing every pane: an
// image in a pane of its own raised by its own values, all of them seen by one camera, and a view
// that combines two images raised by what it shows: the difference, the dissimilarity, or the
// brightness of the split, the blend or the checkerboard. A click on the 3D button (or 3; Shift+3
// while comparing, where 3 is the third image) tilts the picture back and lets it rise; another,
// and it settles flat into the 2D view again. In between, the mouse turns the camera about the
// surface, moves it and brings it closer.
//
// An image is drawn from its own textures, so it carries every pixel of it. Its heights come from
// a grid of at most 2048 values on the long side (Fiv.Surface.HeightField), made on a worker from
// the decoded pixels, and made again when the image, its full version, the channel or the
// smoothing changes. Black, white, gamma and exposure act on them in the shader as on the
// colours: what looks bright stands high. A view that combines two images is drawn flat, colours
// and heights, into a canvas of at most 2048 texels (SurfaceCanvas) by the pass that draws it in
// 2D, in every frame, so that all of its settings act at once; that canvas is then raised.
//
// The ground is the upright image, or the canvas of a comparison; a unit is one of its pixels.
// One height scale for all, so that the surfaces of a comparison can be compared.
internal sealed unsafe partial class MainWindow
{
    const double SurfaceTurnMs = 700, SurfaceResetMs = 450, SurfaceRiseMs = 350;
    const float SurfacePitch = 50 * MathF.PI / 180, SurfaceYaw = 20 * MathF.PI / 180;
    const float MinSurfaceDistance = 0.01f, MaxSurfaceDistance = 20; // times the ground's long side
    static readonly int[] MeshSizes = [256, 512, 1024, 2048];

    // A combined view laid flat holds the colours and heights as they are shown: nothing more is done to them.
    static readonly DisplaySettings FlatDisplay = DisplaySettings.Default with { Sharp = false };

    bool _surface;            // the 3D view is on, also while it turns in or out
    bool _surfaceWaiting;     // asked for: it turns in once the heights of the images on show are there
    bool _surfaceOptions = true;
    SurfaceRenderer? _surfaceRenderer;
    SurfaceCanvas? _surfaceCanvas;
    SurfaceCamera _camera;
    (double Width, double Height) _cameraFor; // the size of the ground the camera was set for
    CameraTurn? _turn;
    (float From, float To, long Start, double Ms)? _riseTurn;
    float _rise = 1;          // how far the surfaces have risen with the turn, 0..1
    bool _surfaceDragPan;
    float _surfacePanelBottom; // where the 3D panel ended in the last frame, in window pixels

    // The heights of the images, on the GPU or on their way: of the image on show, or of the images of a comparison.
    readonly Dictionary<LoadedImage, Heights> _heights = [];
    readonly List<LoadedImage> _onShow = [], _heightsGone = [];
    int _heightsRun;

    float _relief, _shading;
    int _smoothing, _meshCells;
    bool _invertHeight, _wireframe, _colorByHeight;
    Colormap _surfaceMap;

    /// <summary>The camera on its way from one view to another; one that <see cref="Leaves"/> ends in the 2D view.</summary>
    sealed record CameraTurn(SurfaceCamera From, SurfaceCamera To, long Start, double Ms, bool Leaves);

    /// <summary>What a height grid is made of: the decoded pixels, the value taken of them, the smoothing, the largest size.</summary>
    readonly record struct HeightKey(DecodedImage? Pixels, HeightSource Source, int Smoothing, int Size);

    /// <summary>The height grid of one image: the one on the GPU and what it is of, and the one on its way.</summary>
    sealed class Heights
    {
        public GpuImage? Texture;
        public HeightKey Key, Wanted;
        public int Run;
        public CancellationTokenSource? Cancel;
        public bool Building;

        /// <summary>There are none and will be none, for these pixels and settings: it lies flat.</summary>
        public bool Failed;

        /// <summary>When they came, if after the turn: the image rises then, by itself. 0: it rises and settles with the turn.</summary>
        public long ArrivedAt;
    }

    bool SurfaceOnShow => _surface && !_surfaceWaiting;
    bool SurfaceLeaving => _turn is { Leaves: true };

    static HeightSource HeightSourceOf(ChannelView channel) => channel switch
    {
        ChannelView.Red => HeightSource.Red,
        ChannelView.Green => HeightSource.Green,
        ChannelView.Blue => HeightSource.Blue,
        ChannelView.Alpha => HeightSource.Alpha,
        _ => HeightSource.Brightness,
    };

    // ---------------------------------------------------------------- on and off

    /// <summary>The 3D button, 3 and Shift+3: into 3D, or back to 2D.</summary>
    void ToggleSurface()
    {
        if (!_surface) EnterSurface();
        else if (!SurfaceLeaving) LeaveSurface();
    }

    void EnterSurface()
    {
        if (_gpu == null || !SurfaceDrawable())
        {
            Notice(!Comparing && _shown?.Error != null ? "This image could not be loaded." : "The image is not loaded yet.");
            return;
        }
        SetMeasure(false); // its shapes are drawn on the flat image
        _surface = true;
        _surfaceWaiting = true;
        _turn = null;
        _riseTurn = null;
        Invalidate(); // the picture turns once the heights are there: StepSurface
    }

    /// <summary>Something can be drawn in 3D: an image on show with its pixels, or a pane of the comparison with an image.</summary>
    bool SurfaceDrawable()
    {
        if (!Comparing) return _shown is { HasSize: true } image && image.BestGpu != null && (image.Full ?? image.Preview) != null;
        LayoutCompare();
        for (int p = 0; p < _paneCount; p++)
            if (Drawable(_panes[p].First) != null || Drawable(_panes[p].Second) != null) return true;
        for (int k = 0; k < _orderCount; k++)
            if (Drawable(_order[k]) != null) return true;
        return false;
    }

    /// <summary>The heights are there: the picture tilts back from the 2D view and rises.</summary>
    void StartTurnIn()
    {
        _surfaceWaiting = false;
        _camera = TopDownCamera(FlatView);
        _cameraFor = GroundSize;
        long now = Stopwatch.GetTimestamp();
        _turn = new CameraTurn(_camera, StartingCamera(StartingView), now, SurfaceTurnMs, Leaves: false);
        _rise = 0;
        _riseTurn = (0, 1, now, SurfaceTurnMs);
        foreach (Heights entry in _heights.Values) entry.ArrivedAt = 0; // all rise with the turn
        Invalidate();
    }

    /// <summary>Back to 2D: the camera goes back to where the 2D view sees the image from while the surface settles flat, and then the 2D view takes over.</summary>
    void LeaveSurface()
    {
        if (!_surface) return;
        if (_surfaceWaiting || GroundSize.Width <= 0)
        {
            EndSurface();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        _turn = new CameraTurn(_camera.WithTurnWrapped(), TopDownCamera(FlatView), now, SurfaceTurnMs, Leaves: true);
        _riseTurn = (_rise, 0, now, SurfaceTurnMs);
        Invalidate();
    }

    /// <summary>Ends the 3D view at once and gives back what it held.</summary>
    void EndSurface()
    {
        if (!_surface) return;
        _surface = _surfaceWaiting = false;
        _turn = null;
        _riseTurn = null;
        _rise = 1;
        foreach (LoadedImage image in _heights.Keys.ToList()) DropHeights(image);
        _surfaceRenderer?.ReleaseTargets();
        _surfaceCanvas?.Release();
        Invalidate();
    }

    // ---------------------------------------------------------------- the ground and the camera

    /// <summary>The size of the ground: the upright image, or the canvas of a comparison.</summary>
    (double Width, double Height) GroundSize =>
        Comparing ? (_canvasWidth, _canvasHeight) : _shown is { HasSize: true } image ? (image.UprightWidth, image.UprightHeight) : (0, 0);

    float LongSide => (float)Math.Max(GroundSize.Width, GroundSize.Height);

    /// <summary>The rectangle the camera's view is worked out for: the view, or the first pane of a comparison (the panes are of a size).</summary>
    PaneRect SurfaceArea
    {
        get
        {
            if (Comparing)
            {
                LayoutCompare();
                if (_paneCount > 0) return _panes[0].Rect;
            }
            return new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        }
    }

    /// <summary>The rectangle of the window point (<paramref name="x"/>, <paramref name="y"/>): its pane while comparing, the view otherwise.</summary>
    PaneRect SurfaceAreaAt(int x, int y)
    {
        if (!Comparing) return new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        LayoutCompare();
        int p = PaneAt(x, y);
        return p >= 0 ? _panes[p].Rect : SurfaceArea;
    }

    /// <summary>The 2D view the 3D view takes the place of: the shared one, or while comparing the active image's.</summary>
    ViewState FlatView => Comparing ? ViewOf(_pick.Active) : _view;

    /// <summary>The camera that sees the flat ground as this 2D view shows it.</summary>
    SurfaceCamera TopDownCamera(in ViewState view)
    {
        // The ground point at the centre of the view, where ViewState.ToScreen puts it (on whole pixels).
        PaneRect area = SurfaceArea;
        Affine2 toScreen = view.ToScreen(area.Width, area.Height);
        double x = (area.Width / 2.0 - toScreen.Tx) / view.Zoom, z = (area.Height / 2.0 - toScreen.Ty) / view.Zoom;
        return SurfaceCamera.TopDown((float)x, (float)z, view.Zoom, area.Height);
    }

    /// <summary>Where the 3D view starts from: the 2D view tilted back and turned a little, and a little further off, so that what rises stays in sight.</summary>
    SurfaceCamera StartingCamera(in ViewState view)
    {
        SurfaceCamera camera = TopDownCamera(view);
        camera.Pitch = SurfacePitch;
        camera.Yaw = SurfaceYaw;
        camera.Distance *= 1.15f;
        return camera;
    }

    /// <summary>
    /// The 2D view that shows the whole ground as large as the view allows, a small image made
    /// larger too, which the 2D view does not do. The 3D view starts from it unless the 2D view
    /// was zoomed in on a part of the image: then it starts from that part.
    /// </summary>
    ViewState FilledView()
    {
        ViewState view = default;
        (double width, double height) = GroundSize;
        if (width <= 0 || height <= 0) return view;
        PaneRect area = SurfaceArea;
        view.FitTo(width, height, area.Width, area.Height);
        view.Zoom = Math.Min(area.Width / width, area.Height / height);
        return view;
    }

    ViewState StartingView => FlatView.Fit ? FilledView() : FlatView;

    /// <summary>Double click, F and the button: back to the starting view of the whole ground.</summary>
    void ResetSurfaceCamera()
    {
        if (!SurfaceOnShow || SurfaceLeaving) return;
        _turn = new CameraTurn(_camera.WithTurnWrapped(), StartingCamera(FilledView()), Stopwatch.GetTimestamp(), SurfaceResetMs, Leaves: false);
    }

    /// <summary>
    /// A ground of another size (another image, an image added to the comparison): the camera
    /// starts over for it. Of the same size it stays where it is, so that the images of a folder
    /// or of a comparison can be gone through as they are seen.
    /// </summary>
    void KeepCamera()
    {
        (double width, double height) ground = GroundSize;
        if (ground.width <= 0 || _cameraFor == ground) return;
        _camera = StartingCamera(StartingView);
        _cameraFor = ground;
        _turn = null;
    }

    /// <summary>Keeps the target on the ground and the distance within reason.</summary>
    void ClampCamera()
    {
        (double width, double height) = GroundSize;
        _camera.Target = new Vector3(Math.Clamp(_camera.Target.X, 0, (float)width), 0, Math.Clamp(_camera.Target.Z, 0, (float)height));
        _camera.Distance = Math.Clamp(_camera.Distance, LongSide * MinSurfaceDistance, LongSide * MaxSurfaceDistance);
    }

    static float Ease(double t) => (float)(t * t * (3 - 2 * t));

    /// <summary>Moves the turns on to where they are by now. True while one is under way, or an image still rises: it wants another frame.</summary>
    bool AdvanceTurns()
    {
        bool going = false;
        if (_riseTurn is { } rise)
        {
            double t = Math.Min(1, Stopwatch.GetElapsedTime(rise.Start).TotalMilliseconds / rise.Ms);
            _rise = rise.From + (rise.To - rise.From) * Ease(t);
            if (t < 1) going = true;
            else _riseTurn = null;
        }
        if (_turn is { } turn)
        {
            double t = Math.Min(1, Stopwatch.GetElapsedTime(turn.Start).TotalMilliseconds / turn.Ms);
            _camera = SurfaceCamera.Lerp(turn.From, turn.To, Ease(t));
            if (t < 1) going = true;
            else
            {
                _turn = null;
                if (turn.Leaves)
                {
                    EndSurface();
                    return false;
                }
            }
        }
        foreach (Heights entry in _heights.Values) going |= entry.ArrivedAt != 0;
        return going;
    }

    /// <summary>How far an image's surface has risen: not at all until its heights are there, then up to 1 within a moment; all of it times the rise of the turn.</summary>
    float RiseOf(Heights entry)
    {
        if (entry.Texture == null) return 0;
        if (entry.ArrivedAt == 0) return _rise;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(entry.ArrivedAt).TotalMilliseconds / SurfaceRiseMs);
        if (t >= 1) entry.ArrivedAt = 0; // risen: from now on it follows the turn alone
        return _rise * Ease(t);
    }

    // ---------------------------------------------------------------- heights

    /// <summary>The images whose heights the surfaces on show need: the image on its own, or the images of the panes that show one each.</summary>
    void FindImagesOnShow()
    {
        _onShow.Clear();
        if (!Comparing)
        {
            if (_shown is { HasSize: true } shown && shown.BestGpu != null) _onShow.Add(shown);
            return;
        }
        LayoutCompare();
        for (int p = 0; p < _paneCount; p++)
            if (_panes[p].Second < 0 && !_panes[p].Stack && Drawable(_panes[p].First) != null) _onShow.Add(_compare[_panes[p].First].Image!);
    }

    bool HeightsReady()
    {
        foreach (LoadedImage image in _onShow)
            if (!_heights.TryGetValue(image, out Heights? entry) || (entry.Texture == null && !entry.Failed)) return false;
        return true;
    }

    /// <summary>
    /// Asks for the heights of an image unless the ones there, or on their way, are of it as it is
    /// now: of its full pixels once it has them, of the channel shown, smoothed as set. Smaller
    /// when more than four images are compared: each pane is small then, and they all have theirs.
    /// </summary>
    void UpdateHeights(LoadedImage image)
    {
        if (!_heights.TryGetValue(image, out Heights? entry)) _heights[image] = entry = new Heights();
        DecodedImage? pixels = image.Full ?? image.Preview;
        if (pixels == null)
        {
            entry.Failed = true; // nothing to make them of: it lies flat
            return;
        }
        int size = Comparing && _compare.Count > 4 ? HeightField.MaxSize / 2 : HeightField.MaxSize;
        var wanted = new HeightKey(pixels, HeightSourceOf(_display.Channel), _smoothing, size);
        if ((entry.Texture != null && wanted == entry.Key) || wanted == entry.Wanted) return;
        entry.Cancel?.Cancel();
        var cancel = entry.Cancel = new CancellationTokenSource();
        int run = entry.Run = ++_heightsRun;
        entry.Wanted = wanted;
        entry.Building = true;
        entry.Failed = false;
        GraphicsDevice gpu = _gpu!;
        pixels.AddReference(); // kept alive, whatever the cache does with the image meanwhile
        Task.Run(() =>
        {
            GpuImage? texture = null;
            string? error = null;
            try
            {
                using DecodedImage? grid = HeightField.Build(pixels, wanted.Source, wanted.Smoothing, wanted.Size, cancel.Token);
                if (grid != null && !cancel.IsCancellationRequested)
                {
                    // With mip levels: far off, the light is worked out from the heights as coarsely as the screen shows them.
                    using var mips = new MipChain(grid);
                    texture = GpuImage.Create(gpu.Device, grid, mips);
                }
            }
            catch (Exception e)
            {
                CrashLog.Write(e);
                error = e.Message;
            }
            finally
            {
                pixels.Dispose();
            }
            RunOnUi(() => OnHeights(image, run, wanted, texture, error));
        });
    }

    /// <summary>A worker has made heights. UI thread.</summary>
    void OnHeights(LoadedImage image, int run, HeightKey key, GpuImage? texture, string? error)
    {
        if (!_surface || !_heights.TryGetValue(image, out Heights? entry) || entry.Run != run)
        {
            texture?.Dispose();
            return;
        }
        entry.Building = false;
        if (texture == null)
        {
            // Failed: it stays wanted, so that it is not tried again and again; other pixels or settings try afresh.
            entry.Failed = true;
            if (error != null) Notice("The heights could not be made: " + error);
            return;
        }
        bool first = entry.Texture == null;
        entry.Texture?.Dispose();
        entry.Texture = texture;
        entry.Key = key;
        entry.Wanted = default;
        // An image that comes after the turn rises by itself; a better version of one on show takes its place as it stands.
        if (first) entry.ArrivedAt = _surfaceWaiting ? 0 : Stopwatch.GetTimestamp();
        Invalidate();
    }

    void DropHeights(LoadedImage image)
    {
        if (!_heights.Remove(image, out Heights? entry)) return;
        entry.Cancel?.Cancel();
        entry.Texture?.Dispose();
    }

    /// <summary>Lets go of the heights of images that are no longer the one on show, or of the comparison.</summary>
    void PruneHeights()
    {
        _heightsGone.Clear();
        foreach (LoadedImage image in _heights.Keys)
        {
            bool kept = !Comparing && image == _shown;
            for (int i = 0; i < _compare.Count && !kept; i++) kept = Comparing && _compare[i].Image == image;
            if (!kept) _heightsGone.Add(image);
        }
        foreach (LoadedImage image in _heightsGone) DropHeights(image);
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>In a frame, before anything is drawn: the heights are asked for, the turn starts once they are there, the turns move on. May end the 3D view.</summary>
    void StepSurface()
    {
        if (!_surface) return;
        FindImagesOnShow();
        foreach (LoadedImage image in _onShow) UpdateHeights(image);
        PruneHeights();
        if (_surfaceWaiting)
        {
            if (!HeightsReady()) return;
            StartTurnIn();
        }
        KeepCamera();
        if (AdvanceTurns()) Invalidate();
    }

    /// <summary>The settings all surfaces of a frame share, for an area of this size.</summary>
    SurfaceSettings SurfaceSettingsFor(in PaneRect area) => new()
    {
        WorldToClip = _camera.WorldToClip((float)area.Width / area.Height, _camera.Distance * 1e-3f),
        // Not much finer than the area has pixels: the small panes of a grid would draw millions of triangles for nothing.
        MeshCells = Math.Min(_meshCells, Math.Max(256, Math.Max(area.Width, area.Height))),
        Shading = _shading,
        Invert = _invertHeight,
        Wireframe = _wireframe,
        Colors = _colorByHeight ? _surfaceMap : null,
    };

    /// <summary>Draws the image on show as a surface, in place of the 2D picture.</summary>
    void RenderSurface(LoadedImage image) =>
        DrawImageSurface(image, image.BestGpu!, image.Orientation.ToUpright(image.Width, image.Height), new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight));

    /// <summary>An image as a surface in an area of the window, lying on the ground as <paramref name="toGround"/> puts it.</summary>
    void DrawImageSurface(LoadedImage image, GpuImage texture, in Affine2 toGround, in PaneRect area)
    {
        _heights.TryGetValue(image, out Heights? entry);
        float rise = entry != null ? RiseOf(entry) : 0;
        SurfaceSettings settings = SurfaceSettingsFor(area);
        settings.ImageToGround = toGround;
        settings.HeightScale = _relief * rise * LongSide;
        _surfaceRenderer ??= new SurfaceRenderer(_gpu!);
        _surfaceRenderer.Draw(texture, entry?.Texture, settings, _display, area.X, area.Y, area.Width, area.Height, Backdrop);
    }

    /// <summary>A pane of the comparison as a surface: its image on the canvas, or the images it combines laid flat and raised.</summary>
    void RenderSurfacePane(in Pane pane)
    {
        if (pane.Second < 0 && !pane.Stack)
        {
            if (Drawable(pane.First) is { } texture) DrawImageSurface(_compare[pane.First].Image!, texture, ImageToCanvas(pane.First), pane.Rect);
            return;
        }
        try
        {
            DrawFlatSurface(pane);
            _compareError = null;
        }
        catch (GraphicsException e)
        {
            // Most likely no video memory for the canvas. The views of one image each still work.
            _compareError = "This compare mode could not be drawn in 3D: " + e.Message;
            _gpu!.BindBackBuffer();
            _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
        }
    }

    /// <summary>
    /// A view that combines images, drawn flat over the whole canvas by the passes that draw it
    /// in 2D, then raised by what it shows. The dividers and the cells are where the pane has them now.
    /// </summary>
    void DrawFlatSurface(in Pane pane)
    {
        PaneRect r = pane.Rect;
        int canvasWidth = (int)Math.Ceiling(_canvasWidth), canvasHeight = (int)Math.Ceiling(_canvasHeight);
        (int width, int height) = Resampler.FitWithin(canvasWidth, canvasHeight, HeightField.MaxSize);
        double sx = (double)width / canvasWidth, sy = (double)height / canvasHeight;
        Affine2 canvasToFlat = Affine2.ScaleTranslate(sx, sy, 0, 0);
        Affine2 paneToFlat = canvasToFlat.After(CanvasToPane(pane.First, r).Inverse());

        MixLayer Flat(int item) => Drawable(item) is { } texture
            ? new MixLayer(texture, canvasToFlat.After(ImageToCanvas(item)), sx * PlacementOf(_compare[item].Image!).A * _compare[item].Pose.Scale, 1)
            : default;

        MixSettings mix;
        int count = 2;
        if (pane.Stack)
        {
            count = StackLayers(r, canvasToFlat, paneToFlat);
            bool any = false;
            for (int k = 0; k < count; k++) any |= _layers[k].Image != null;
            if (!any) return;
            (double x, double y) = paneToFlat.Apply(r.Width / 2.0, r.Height / 2.0);
            mix = StackMix((float)(_checkerSize / ViewOf(pane.First).Zoom * sx), (float)x, (float)y);
        }
        else
        {
            _layers[0] = Flat(pane.First);
            _layers[1] = Flat(pane.Second);
            if (_layers[0].Image == null && _layers[1].Image == null) return;
            if (_compareMode == CompareMode.Ssim && SimilarityOf(pane.First, pane.Second) is { } similarity)
            {
                // As RenderSimilarity: the map over its rectangle, and of a turned image only where it has pixels.
                _layers[0] = new MixLayer(similarity.Gpu, canvasToFlat.After(Affine2.ScaleTranslate(1, 1, similarity.Area.X, similarity.Area.Y)), sx);
                if (_compare[pane.Second].Pose.IsIdentity) _layers[1] = default;
                mix = new MixSettings { Kind = MixKind.Heat, Amount = _similarityGain, Colors = _differenceMap, WithinSecond = _layers[1].Image != null };
            }
            else if (_compareMode == CompareMode.Ssim)
            {
                mix = new MixSettings { Kind = MixKind.Blend }; // the reference as it is, until the similarity is there
                count = 1;
            }
            else
            {
                mix = DifferenceMix();
            }
        }
        _surfaceCanvas ??= new SurfaceCanvas(_gpu!);
        _surfaceCanvas.Ensure(width, height, canvasWidth, canvasHeight);
        _compareRenderer ??= new CompareRenderer(_gpu!, _renderer!);
        _compareRenderer.DrawFlat(_layers.AsSpan(0, count), _display, mix, _surfaceCanvas, Backdrop);

        SurfaceSettings settings = SurfaceSettingsFor(r);
        settings.ImageToGround = Affine2.Identity; // the canvas is the ground
        settings.HeightScale = _relief * _rise * LongSide;
        // These heights are drawn, not smoothed: reading them from a coarser mip level does it.
        settings.HeightLevel = _smoothing > 0 ? 1 + MathF.Log2(_smoothing) : 0;
        _surfaceRenderer ??= new SurfaceRenderer(_gpu!);
        _surfaceRenderer.Draw(_surfaceCanvas.Colors!, _surfaceCanvas.Heights, settings, FlatDisplay, r.X, r.Y, r.Width, r.Height, Backdrop);
    }

    // ---------------------------------------------------------------- input

    /// <summary>The left button went down over the image in 3D: a drag turns the camera, with Shift moves it. It takes over from a turn under way.</summary>
    void SurfaceMouseDown(bool shift)
    {
        _surfaceDragPan = shift;
        if (!SurfaceLeaving) _turn = null;
    }

    void SurfaceDrag(int dx, int dy)
    {
        if (!SurfaceOnShow || SurfaceLeaving) return;
        if (_surfaceDragPan) _camera.Pan(dx, dy, SurfaceArea.Height);
        else _camera.Orbit(dx, dy);
        ClampCamera();
    }

    /// <summary>Comes closer by <paramref name="factor"/> (below 1) or goes further off, at the window point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    void SurfaceZoom(double factor, int x, int y)
    {
        if (!SurfaceOnShow || SurfaceLeaving) return;
        _turn = null;
        PaneRect area = SurfaceAreaAt(x, y);
        // Limited before, not after: the ground under the mouse stays where it is.
        float f = Math.Clamp((float)factor, LongSide * MinSurfaceDistance / _camera.Distance, LongSide * MaxSurfaceDistance / _camera.Distance);
        float ndcX = 2f * (x - area.X) / area.Width - 1, ndcY = 1 - 2f * (y - area.Y) / area.Height;
        _camera.ZoomAt(f, ndcX, ndcY, (float)area.Width / area.Height);
        ClampCamera();
    }

    /// <summary>The keys of the 3D view, before the comparison's and the viewer's own. True if the key was one of them.</summary>
    bool SurfaceKey(VIRTUAL_KEY key, bool control)
    {
        if (!_surface || control) return false;
        PaneRect area = SurfaceArea;
        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE when !_showKeys && !_showSettings && !_showFileTypes:
                LeaveSurface();
                return true;
            case VIRTUAL_KEY.VK_F or VIRTUAL_KEY.VK_0 or VIRTUAL_KEY.VK_NUMPAD0:
                ResetSurfaceCamera();
                return true;
            case VIRTUAL_KEY.VK_OEM_PLUS or VIRTUAL_KEY.VK_ADD:
                SurfaceZoom(1 / WheelZoomStep, area.X + area.Width / 2, area.Y + area.Height / 2);
                return true;
            case VIRTUAL_KEY.VK_OEM_MINUS or VIRTUAL_KEY.VK_SUBTRACT:
                SurfaceZoom(WheelZoomStep, area.X + area.Width / 2, area.Y + area.Height / 2);
                return true;
            case VIRTUAL_KEY.VK_Z:
            case VIRTUAL_KEY.VK_1 or VIRTUAL_KEY.VK_NUMPAD1 when !Comparing: // while comparing, 1 is the first image
                return true; // 100 % means nothing on a surface seen at a slant
        }
        return false;
    }

    // ---------------------------------------------------------------- the button and the panel

    /// <summary>The 3D button in the view's upper right corner and, while the 3D view is on, its options below it.</summary>
    void DrawSurfacePanel()
    {
        _surfacePanelBottom = 0;
        if (_gpu == null || (Comparing ? !SurfaceDrawable() : _shown is not { HasSize: true } || _shown.BestGpu == null)) return;
        float scale = (float)_dpiScale;
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth - 10 * scale, ViewY + 10 * scale), ImGuiCond.Always, new Vector2(1, 0));
        bool on = _surface && !SurfaceLeaving;
        ImGui.SetNextWindowBgAlpha(on ? 0.9f : 0.6f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("3D"u8, flags))
        {
            if (ImGui.Button(on ? "2D"u8 : "3D"u8, new Vector2(48 * scale, 0))) ToggleSurface();
            if (ImGui.IsItemHovered())
            {
                if (on) ImGui.SetTooltip(Comparing ? "Back to the flat images (Shift+3, Esc)"u8 : "Back to the flat image (3, Esc)"u8);
                else ImGui.SetTooltip(Comparing ? "The panes as 3D height maps (Shift+3)"u8 : "The image as a 3D height map (3)"u8);
            }
            if (on && !_surfaceWaiting)
            {
                ImGui.SameLine();
                if (ImGui.Button("Starting view"u8)) ResetSurfaceCamera();
                ImGui.SameLine();
                ImGui.Checkbox("Options"u8, ref _surfaceOptions);
                if (_surfaceOptions) DrawSurfaceOptions(scale);
            }
            _surfacePanelBottom = ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y;
        }
        ImGui.End();
    }

    void DrawSurfaceOptions(float scale)
    {
        ImGui.PushItemWidth(200 * scale);
        float percent = _relief * 100;
        if (ImGui.SliderFloat("height, % of the long side"u8, ref percent, 0, 100, "%.0f"u8)) _relief = Math.Clamp(percent, 0, 100) / 100;
        if (ImGui.SliderInt("smoothing"u8, ref _smoothing, 0, 20)) _smoothing = Math.Clamp(_smoothing, 0, 20);
        ImGui.SliderFloat("light and shade"u8, ref _shading, 0, 1, "%.2f"u8);
        ImGui.PopItemWidth();

        ImGui.TextUnformatted("mesh"u8);
        foreach (int size in MeshSizes)
        {
            ImGui.SameLine();
            if (!Utf8.TryWrite(_utf8, $"{size}\0", out _)) continue;
            fixed (byte* label = _utf8)
                if (ImGui.RadioButton(label, _meshCells == size)) _meshCells = size;
        }

        // The colours shown, or a colour map by height.
        Utf8Of(_colorByHeight ? _surfaceMap.DisplayName() : Comparing ? "as shown" : "the image's");
        ImGui.SetNextItemWidth(130 * scale);
        bool open;
        fixed (byte* preview = _utf8) open = ImGui.BeginCombo("colours"u8, preview);
        if (open)
        {
            if (ImGui.Selectable(Comparing ? "as shown"u8 : "the image's"u8, !_colorByHeight)) _colorByHeight = false;
            for (int i = 0; i < Colormaps.Count; i++)
            {
                var map = (Colormap)i;
                Utf8Of(map.DisplayName());
                fixed (byte* label = _utf8)
                    if (ImGui.Selectable(label, _colorByHeight && map == _surfaceMap)) (_colorByHeight, _surfaceMap) = (true, map);
            }
            ImGui.EndCombo();
        }
        ImGui.Checkbox("upside down"u8, ref _invertHeight);
        ImGui.SameLine();
        ImGui.Checkbox("wireframe"u8, ref _wireframe);

        string source = _display.Channel is ChannelView.Color or ChannelView.Luma ? "brightness" : ChannelName(_display.Channel);
        bool written = Comparing && _compareMode is CompareMode.Difference or CompareMode.Ssim
            ? Utf8.TryWrite(_utf8, $"Height: the {(_compareMode == CompareMode.Ssim ? "dissimilarity" : "difference")}, by its colour scale (gain)\0", out _)
            : Utf8.TryWrite(_utf8, $"Height: {source}, through black, white and gamma (D, H)\0", out _);
        if (written)
        {
            fixed (byte* text = _utf8) TextDim(text);
        }
        ImGui.TextDisabled("Drag: turn   Shift+drag: move   Wheel: closer"u8);
        bool building = false;
        foreach (Heights entry in _heights.Values) building |= entry.Building;
        if (building) ImGui.TextDisabled("making the heights..."u8);
    }
}
