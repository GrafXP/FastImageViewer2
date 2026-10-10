using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
using Hexa.NET.ImGui;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Fiv.App;

// Comparing: a set of 2 to 16 images on screen together, in panes next to each other or
// combined in one pane. While a set is active the window shows it instead of walking the
// folder; the folder, its panels and the image cache carry on underneath, and leaving the
// comparison returns to the folder at the image that was active.
//
// All images of a set share the canvas (see CompareGeometry): zoom and position are kept in
// canvas coordinates, so one view state moves every pane alike and the same canvas point is
// under the cursor in each. The display controls are shared too.
internal sealed unsafe partial class MainWindow
{
    public const int MaxCompare = 16;
    const nuint BlinkTimerId = 5;

    /// <summary>A number key held this long shows its image only until the key is released.</summary>
    const double PeekHoldMs = 250;

    sealed class CompareItem(string path, long size, long modified)
    {
        public string Path = path;
        public long Size = size, Modified = modified;
        public LoadedImage? Image;

        /// <summary>Its own zoom and position, in use when the panes are not linked.</summary>
        public ViewState View;

        /// <summary>Where it has been moved to from its place on the canvas, to lie on another image (see MainWindow.Align).</summary>
        public AlignPose Pose;

        /// <summary>Where the file is in the list that next and previous walk, or -1.</summary>
        public int ListPosition = -1;

        /// <summary>Its values are among those a measurement shows: the plot of a line, the histogram and the numbers of an area (see MainWindow.Measure).</summary>
        public bool Measured = true;
    }

    /// <summary>A rectangle of the view and the one or two images of the set shown in it.</summary>
    struct Pane
    {
        public PaneRect Rect;
        public int First, Second; // Second is -1 unless the pane combines two images
    }

    readonly List<CompareItem> _compare = [];
    readonly CompareSelection _pick = new();
    readonly Pane[] _panes = new Pane[MaxCompare];
    int _paneCount;
    CompareRenderer? _compareRenderer;
    CompareMode _compareMode = CompareMode.SideBySide;
    SizeMatch _sizeMatch = SizeMatch.TopLeft;
    double _canvasWidth, _canvasHeight;
    int _singleItem = -1; // the image the single view shows: the active one once it can be drawn
    bool _linked = true, _linkedCursor = true, _paneLabels = true, _showComparePanel = true;
    bool _splitRows, _blink, _blinkAll;
    float _splitAt = 0.5f, _opacity = 0.5f;
    int _checkerSize = 64, _blinkMs = 500;
    bool _dragDivider;
    int _dragItem = -1;
    bool _compareReady;   // every image on show has been presented at full resolution
    bool _windowTouched;  // the user set the brightness window; it is no longer chosen automatically
    string? _compareError;

    bool Comparing => _compare.Count >= 2;

    static readonly Vector3 Backdrop = new(0.11f, 0.11f, 0.12f);

    // ---------------------------------------------------------------- the set

    /// <summary>Starts comparing these files, given with size and modified time as the folder lists them (0 if unknown).</summary>
    void StartCompare(List<(string Path, long Size, long Modified)> files)
    {
        string? current = _shown?.Path;
        StopBlink();
        _compare.Clear();
        int active = 0;
        foreach ((string path, long size, long modified) in files)
        {
            if (_compare.Count == MaxCompare) break;
            if (_compare.Exists(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (current != null && string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) active = _compare.Count;
            _compare.Add(new CompareItem(path, size, modified));
        }
        if (_compare.Count < 2)
        {
            _compare.Clear();
            return;
        }
        _pick.Reset(_compare.Count, active);
        _singleItem = -1;
        _compareReady = false;
        _compareError = null;
        _dragItem = -1;
        _dragDivider = false;
        _splitAt = 0.5f;
        _opacity = 0.5f;
        _differenceGain = 1;
        ResetMetrics();
        ResetAlign();
        _fast = false;
        if (!_hwnd.IsNull) PInvoke.KillTimer(_hwnd, SettleTimerId);

        // The canvas is fitted to the panes, and the brightness window starts over for the set.
        _view.Fit = true;
        _display.WindowLow = 0;
        _display.WindowHigh = 1;
        _windowTouched = false;
        _autoWindowFor = null;

        BindCompareToList();
        if (!Comparing)
        {
            StopCompare(); // files of the set are not in the folder they were said to be in
            return;
        }
        Request();
        UpdateTitle();
        Invalidate();
    }

    /// <summary>
    /// Ends the comparison. With <paramref name="resume"/> the window goes back to walking the
    /// folder, at the image that was active; without, the caller is about to show something else.
    /// </summary>
    void StopCompare(bool resume = true)
    {
        if (_compare.Count == 0) return;
        string path = _compare[Math.Clamp(_pick.Active, 0, _compare.Count - 1)].Path;
        StopBlink();
        _compare.Clear();
        _pick.Reset(0);
        _paneCount = 0;
        _singleItem = -1;
        _dragItem = -1;
        _dragDivider = false;
        _compareError = null;
        _compareRenderer?.ReleaseTargets();
        ResetMetrics();
        ResetAlign();

        // Zoom, position and brightness window were the set's. Whatever is shown next is shown
        // as an image newly arrived at: fitted to the view, with its own window.
        LoadedImage? last = _shown;
        _shown = null;
        _shownIndex = -1;
        _pipeline.Pinned = null;
        if (last is { Orphaned: true }) last.Dispose();
        UpdateTitle();
        if (!resume) return;

        int position = ListPositionOf(path);
        if (position >= 0) _fileIndex = position;
        _fast = false;
        _scrollBrowserToCurrent = _scrollStripToCurrent = true;
        Request();
        SelectCurrent();
        Invalidate();
    }

    /// <summary>The C key: compares what is selected, or stops comparing.</summary>
    void ToggleCompare()
    {
        if (Comparing) StopCompare();
        else CompareSelected();
    }

    /// <summary>
    /// Compares the files selected in the panels. With nothing selected beyond the image on show,
    /// that image and the next one; with files that were opened together, all of them.
    /// </summary>
    void CompareSelected()
    {
        var files = new List<(string Path, long Size, long Modified)>();
        if (_folder is { } folder)
        {
            if (_selectionListing == folder.Listing && _selection.Count > 1)
            {
                files = SelectedFiles(folder);
            }
            else if (folder.Count > 1 && _fileIndex >= 0 && _fileIndex < folder.Count)
            {
                int first = _fileIndex + 1 < folder.Count ? _fileIndex : _fileIndex - 1;
                for (int position = first; position <= first + 1; position++) files.Add((folder.PathAt(position), folder.SizeAt(position), folder.ModifiedAt(position)));
            }
        }
        else
        {
            foreach (string file in _files) files.Add((file, 0, 0));
        }
        if (files.Count < 2)
        {
            Notice("Nothing to compare: select two or more images (Ctrl+click), or drop them here.");
            return;
        }
        if (files.Count > MaxCompare) Notice($"Comparing the first {MaxCompare} of {files.Count} images.");
        StartCompare(files);
    }

    /// <summary>The selected files of the folder as the panels show it, in its order.</summary>
    List<(string Path, long Size, long Modified)> SelectedFiles(FolderIndex folder)
    {
        var files = new List<(string Path, long Size, long Modified)>();
        if (_selectionListing != folder.Listing) return files;
        for (int position = 0; position < folder.Count; position++)
            if (_selection.Contains(folder.ListingIndexAt(position))) files.Add((folder.PathAt(position), folder.SizeAt(position), folder.ModifiedAt(position)));
        return files;
    }

    /// <summary>
    /// The mode of a comparison that was not asked for as one: images selected in a panel, or
    /// opened together from Explorer. Two side by side, more in the grid.
    /// </summary>
    CompareMode ModeForSeveral(int count) => _options.CompareMode ?? (count > 2 ? CompareMode.Grid : CompareMode.SideBySide);

    /// <summary>
    /// The selection in the panels was changed by hand (Ctrl or Shift and a click, select all,
    /// invert). Two to sixteen selected images are compared at once; when one is left, it is
    /// shown on its own. More than sixteen are a selection for the file actions, not a comparison.
    /// </summary>
    void CompareSelection()
    {
        if (_folder is not { } folder) return;
        var files = SelectedFiles(folder);
        if (ComparedWhenOpened(files.Count))
        {
            SetCompareMode(ModeForSeveral(files.Count));
            StartCompare(files);
        }
        else if (Comparing)
        {
            if (files.Count == 1)
            {
                Navigate(folder.Find(Path.GetFileName(files[0].Path.AsSpan()))); // ends the comparison
                return;
            }
            StopCompare();
            if (files.Count > MaxCompare) Notice($"{files.Count} images are selected: up to {MaxCompare} are compared.");
        }
    }

    /// <summary>Adds a file to the set; with no set yet, starts one with the image on show.</summary>
    void AddToCompare(string path)
    {
        if (!Comparing)
        {
            if (_shown == null || string.Equals(_shown.Path, path, StringComparison.OrdinalIgnoreCase)) return;
            StartCompare([(_shown.Path, 0, 0), (path, 0, 0)]);
            return;
        }
        int existing = _compare.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing < 0)
        {
            if (_compare.Count >= MaxCompare)
            {
                Notice($"At most {MaxCompare} images can be compared at once.");
                return;
            }
            _compare.Add(new CompareItem(path, 0, 0) { View = _view });
            _pick.Added();
            _compareReady = false;
            BindCompareToList();
            existing = _compare.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) return;
        }
        _pick.Select(existing);
        OnComparePick();
        UpdateTitle();
    }

    void RemoveFromCompare(int index)
    {
        if (index < 0 || index >= _compare.Count) return;
        if (_compare.Count <= 2)
        {
            // One image is no comparison: the other one stays on show.
            if (index == _pick.Active) _pick.Select(_pick.Other);
            StopCompare();
            return;
        }
        _compare.RemoveAt(index);
        _pick.Removed(index);
        _singleItem = -1;
        _dragItem = -1;
        OnComparePick();
        UpdateTitle();
    }

    /// <summary>Where a file is in the list that next and previous walk, or -1.</summary>
    int ListPositionOf(string path)
    {
        if (_folder is { } folder) return InFolder(path, folder.Directory) ? folder.Find(Path.GetFileName(path.AsSpan())) : -1;
        return _files.FindIndex(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
    }

    static bool InFolder(string path, string directory) =>
        Path.GetDirectoryName(path.AsSpan()).Equals(Path.TrimEndingDirectorySeparator(directory.AsSpan()), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Looks the compared files up in the folder as it is now: their place in the list, and the
    /// size and time by which a file that changed on disk is recognised and loaded again. Files
    /// the folder no longer has leave the set.
    /// </summary>
    void BindCompareToList()
    {
        FolderIndex? all = _folderAll;
        for (int i = _compare.Count - 1; i >= 0; i--)
        {
            CompareItem item = _compare[i];
            item.ListPosition = ListPositionOf(item.Path);
            if (all == null || !InFolder(item.Path, all.Directory)) continue;
            int position = all.Find(Path.GetFileName(item.Path.AsSpan()));
            if (position < 0)
            {
                _compare.RemoveAt(i);
                _pick.Removed(i);
                _singleItem = -1;
                continue;
            }
            item.Size = all.SizeAt(position);
            item.Modified = all.ModifiedAt(position);
        }
    }

    /// <summary>The folder was read again, sorted or filtered while comparing.</summary>
    void RebindCompare()
    {
        BindCompareToList();
        if (!Comparing)
        {
            StopCompare(); // files of the set were deleted: what is left, if anything, is shown on its own
            return;
        }
        Request();
        UpdateTitle();
        Invalidate();
    }

    /// <summary>The file at <paramref name="oldPath"/> was renamed; the set follows it.</summary>
    void RenameInCompare(string oldPath, string newPath)
    {
        foreach (CompareItem item in _compare)
            if (string.Equals(item.Path, oldPath, StringComparison.OrdinalIgnoreCase)) item.Path = newPath;
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Tells the pipeline to have every image of the set ready in full: the active one first, then the one it is compared with.</summary>
    void RequestCompare()
    {
        _wants.Clear();
        int active = _pick.Active, other = _pick.Other, rank = 2;
        for (int i = 0; i < _compare.Count; i++)
        {
            CompareItem item = _compare[i];
            _wants.Add(new ImagePipeline.Want(item.Path, item.Size, item.Modified, i == active ? 0 : i == other ? 1 : rank++, true));
        }
        LayoutCompare();
        PaneRect pane = _paneCount > 0 ? _panes[0].Rect : new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        _pipeline.Request(CollectionsMarshal.AsSpan(_wants), pane.Width, pane.Height);
        AdvanceCompare();
    }

    /// <summary>
    /// Takes in what the pipeline has for the set. The image the status bar, the title and the
    /// file actions refer to, and the one the single view draws, becomes the active one as soon
    /// as it can be drawn; until then the previous one stays, so switching never flashes empty.
    /// </summary>
    void AdvanceCompare()
    {
        bool anything = false;
        foreach (CompareItem item in _compare)
        {
            item.Image = _pipeline.Find(item.Path);
            anything |= item.Image is { } loaded && (loaded.BestGpu != null || loaded.Error != null);
        }
        int active = _pick.Active;
        LoadedImage? image = _compare[active].Image;
        _target = image;
        if (image != null && (image.BestGpu != null || image.Error != null) && (_shown != image || _singleItem != active))
        {
            LoadedImage? previous = _shown;
            _shown = image;
            _singleItem = active;
            _pipeline.Pinned = image;
            if (previous != image)
            {
                if (previous is { Orphaned: true }) previous.Dispose();
                _imagesShown++;
                UpdateTitle();
            }
        }
        _shownIndex = _singleItem >= 0 ? _compare[_singleItem].ListPosition : -1;
        ApplyCompareAutoWindow();
        if (anything) EnsureVisible();
        Invalidate();
    }

    /// <summary>
    /// 16-bit and float images open with black and white at the smallest and largest value of
    /// the whole set, so that all of them are shown with the same window.
    /// </summary>
    void ApplyCompareAutoWindow()
    {
        if (_windowTouched || !CompareValueRange(out float low, out float high)) return;
        _display.WindowLow = low;
        _display.WindowHigh = high;
    }

    /// <summary>The value range of the set, if all its loaded images are 16-bit or float grayscale.</summary>
    bool CompareValueRange(out float low, out float high)
    {
        low = float.MaxValue;
        high = float.MinValue;
        foreach (CompareItem item in _compare)
        {
            if (item.Image is not { Full: not null } image) continue;
            if (image.Layout is not (PixelLayout.Gray16 or PixelLayout.GrayFloat)) return false;
            low = Math.Min(low, image.ValueRange.Min);
            high = Math.Max(high, image.ValueRange.Max);
        }
        return low < high;
    }

    // ---------------------------------------------------------------- selection

    /// <summary>The active image changed: load order, what is on show, and the timing of the switch follow it.</summary>
    void OnComparePick()
    {
        Request();
        Invalidate();
    }

    void SetCompareMode(CompareMode mode)
    {
        if (mode == _compareMode) return;
        if (mode != CompareMode.Single) StopBlink();
        _compareMode = mode;
        if (!mode.IsOverlay()) _compareRenderer?.ReleaseTargets(); // only the modes that combine two images need them
        if (!Comparing) return;
        _dragItem = -1;
        _dragDivider = false;
        _compareReady = false;
        AdvanceCompare();
    }

    /// <summary>One view for all panes, or one each. Linking takes the active image's view for all.</summary>
    void SetLinked(bool linked)
    {
        if (linked == _linked) return;
        if (linked)
        {
            if (Comparing) _view = ViewOf(_pick.Active);
        }
        else
        {
            foreach (CompareItem item in _compare) item.View = _view;
        }
        _linked = linked;
    }

    void ToggleBlink()
    {
        if (_blink)
        {
            StopBlink();
            return;
        }
        if (!Comparing) return;
        _blink = true;
        SetCompareMode(CompareMode.Single); // blinking is switching in place
        PInvoke.SetTimer(_hwnd, BlinkTimerId, (uint)_blinkMs, null);
    }

    void StopBlink()
    {
        if (!_blink) return;
        _blink = false;
        if (!_hwnd.IsNull) PInvoke.KillTimer(_hwnd, BlinkTimerId);
    }

    void SetBlinkInterval(int milliseconds)
    {
        _blinkMs = Math.Clamp(milliseconds, Settings.MinBlinkMs, Settings.MaxBlinkMs);
        if (_blink) PInvoke.SetTimer(_hwnd, BlinkTimerId, (uint)_blinkMs, null); // restarts the timer with the new interval
    }

    void OnBlinkTimer()
    {
        if (!Comparing || !_blink)
        {
            StopBlink();
            return;
        }
        if (_blinkAll ? _pick.Step(1) : _pick.Toggle()) OnComparePick();
    }

    /// <summary>Up and Down change the one parameter the current mode has.</summary>
    void AdjustMix(int direction, bool fine)
    {
        switch (_compareMode)
        {
            case CompareMode.Split:
                _splitAt = Math.Clamp(_splitAt + direction * (fine ? 0.01f : 0.05f), 0, 1);
                break;
            case CompareMode.Blend:
                _opacity = Math.Clamp(MathF.Round((_opacity + direction * (fine ? 0.01f : 0.1f)) * 100) / 100, 0, 1);
                break;
            case CompareMode.Checker:
                _checkerSize = Math.Clamp(direction > 0 ? _checkerSize * 2 : _checkerSize / 2, Settings.MinCheckerSize, Settings.MaxCheckerSize);
                break;
            case CompareMode.Single when _blink:
                SetBlinkInterval(direction > 0 ? _blinkMs * 2 / 3 : _blinkMs * 3 / 2); // up is faster
                break;
            case CompareMode.Difference:
                // Twice or half the gain; with Shift a fourth of that step.
                _differenceGain = Math.Clamp(_differenceGain * MathF.Pow(2, direction * (fine ? 0.25f : 1f)), 1, MaxGain);
                _autoGain = false;
                break;
            case CompareMode.Ssim:
                _similarityGain = Math.Clamp(_similarityGain * MathF.Pow(2, direction * (fine ? 0.25f : 1f)), 1, MaxSimilarityGain);
                break;
        }
    }

    // ---------------------------------------------------------------- layout and transforms

    /// <summary>The view an image is drawn with: the shared one, or its own where each pane has its own.</summary>
    ref ViewState ViewOf(int item)
    {
        if (_linked || !_compareMode.HasPanes() || item < 0 || item >= _compare.Count) return ref _view;
        return ref _compare[item].View;
    }

    /// <summary>
    /// Works out the canvas and the panes from the set, the mode and the view rectangle, and
    /// keeps views that are fitted to the window fitted. Cheap, and called before anything that
    /// uses panes, so that they never lag behind the state.
    /// </summary>
    void LayoutCompare()
    {
        _paneCount = 0;
        if (!Comparing) return;
        double width = 0, height = 0;
        foreach (CompareItem item in _compare)
        {
            if (item.Image is not { HasSize: true } image) continue;
            width = Math.Max(width, image.UprightWidth);
            height = Math.Max(height, image.UprightHeight);
        }
        _canvasWidth = width;
        _canvasHeight = height;

        var area = new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        (int first, int second) = _pick.Pair;
        switch (_compareMode)
        {
            case CompareMode.Single:
                _panes[_paneCount++] = new Pane { Rect = area, First = _singleItem, Second = -1 };
                break;
            case CompareMode.SideBySide or CompareMode.Grid:
            {
                bool grid = _compareMode == CompareMode.Grid;
                int count = grid ? _compare.Count : 2, gap = Math.Max(1, (int)Math.Round(2 * _dpiScale));
                (int columns, int rows) = CompareGeometry.BestGrid(count, area.Width, area.Height, width, height, gap);
                for (int i = 0; i < count; i++)
                    _panes[_paneCount++] = new Pane { Rect = CompareGeometry.Pane(i, columns, rows, area, gap), First = grid ? i : i == 0 ? first : second, Second = -1 };
                break;
            }
            default:
                _panes[_paneCount++] = new Pane { Rect = area, First = first, Second = second };
                break;
        }
        if (width <= 0) return;
        PaneRect pane = _panes[0].Rect;
        if (_view.Fit) _view.FitTo(width, height, pane.Width, pane.Height);
        foreach (CompareItem item in _compare)
            if (item.View.Fit) item.View.FitTo(width, height, pane.Width, pane.Height);
    }

    /// <summary>Where an image's placement puts it on the canvas: upright coordinates to canvas coordinates.</summary>
    Affine2 PlacementOf(LoadedImage image) =>
        CompareGeometry.Placement(_sizeMatch, image.UprightWidth, image.UprightHeight, _canvasWidth, _canvasHeight);

    /// <summary>Stored pixel coordinates of an image to canvas coordinates, as its orientation and its placement have it: before any alignment.</summary>
    Affine2 PlacedOnCanvas(LoadedImage image) => PlacementOf(image).After(image.Orientation.ToUpright(image.Width, image.Height));

    /// <summary>The canvas point an image's alignment turns and scales about: its centre where the placement puts it.</summary>
    (double X, double Y) PoseCentre(LoadedImage image) => PlacementOf(image).Apply(image.UprightWidth / 2, image.UprightHeight / 2);

    /// <summary>
    /// Stored pixel coordinates of an image of the set, which must have its size, to canvas
    /// coordinates: orientation, placement, and on top of them its alignment.
    /// </summary>
    Affine2 ImageToCanvas(int item)
    {
        CompareItem entry = _compare[item];
        LoadedImage image = entry.Image!;
        Affine2 placed = PlacedOnCanvas(image);
        if (entry.Pose.IsIdentity) return placed;
        (double x, double y) = PoseCentre(image);
        return entry.Pose.About(x, y).After(placed);
    }

    /// <summary>Canvas coordinates to pixels of a pane, counted from the pane's corner.</summary>
    Affine2 CanvasToPane(int item, in PaneRect pane) => ViewOf(item).ToScreen(pane.Width, pane.Height);

    /// <summary>Canvas coordinates to window pixels.</summary>
    Affine2 CanvasToScreen(int item, in PaneRect pane) => Affine2.ScaleTranslate(1, 1, pane.X, pane.Y).After(CanvasToPane(item, pane));

    /// <summary>Screen pixels per image pixel, which decides how the image is sampled.</summary>
    double ZoomOf(int item, LoadedImage image) => ViewOf(item).Zoom * PlacementOf(image).A * _compare[item].Pose.Scale;

    int PaneAt(int x, int y)
    {
        for (int p = 0; p < _paneCount; p++)
            if (_panes[p].Rect.Contains(x, y)) return p;
        return -1;
    }

    /// <summary>The divider of the split view, in pixels from the pane's left (or top) edge.</summary>
    float DividerAt(in PaneRect pane) => MathF.Round(_splitAt * (_splitRows ? pane.Height : pane.Width));

    bool NearDivider(int x, int y)
    {
        if (_compareMode != CompareMode.Split || _paneCount == 0) return false;
        PaneRect pane = _panes[0].Rect;
        if (!pane.Contains(x, y)) return false;
        float distance = _splitRows ? y - pane.Y - DividerAt(pane) : x - pane.X - DividerAt(pane);
        return MathF.Abs(distance) <= 6 * (float)_dpiScale;
    }

    /// <summary>The canvas point under the mouse, if the mouse is over a pane.</summary>
    bool TryGetCursorCanvas(out double x, out double y)
    {
        x = y = 0;
        if (_canvasWidth <= 0) return false;
        int p = PaneAt(_mouseX, _mouseY);
        if (p < 0 || _panes[p].First < 0) return false;
        (x, y) = CanvasToScreen(_panes[p].First, _panes[p].Rect).Inverse().Apply(_mouseX + 0.5, _mouseY + 0.5);
        return true;
    }

    /// <summary>The pixel of an image of the set that is under the mouse, in this pane or, through the canvas, in another.</summary>
    bool TryGetComparePixel(int item, out int x, out int y, out PixelValue value)
    {
        x = y = 0;
        value = default;
        if (item < 0 || item >= _compare.Count || _compare[item].Image is not { HasSize: true } image) return false;
        if (!TryGetCursorCanvas(out double canvasX, out double canvasY)) return false;
        (double ix, double iy) = ImageToCanvas(item).Inverse().Apply(canvasX, canvasY);
        x = (int)Math.Floor(ix);
        y = (int)Math.Floor(iy);
        return image.TryReadPixel(x, y, out value);
    }

    // ---------------------------------------------------------------- drawing

    GpuImage? Drawable(int item) =>
        item >= 0 && item < _compare.Count && _compare[item].Image is { HasSize: true } image ? image.BestGpu : null;

    /// <summary>Draws the panes. Called between BeginFrame and the UI.</summary>
    void RenderCompare()
    {
        LayoutCompare();
        UpdateMetrics();
        UpdateAlignScore();
        if (_canvasWidth <= 0) return;
        for (int p = 0; p < _paneCount; p++)
        {
            Pane pane = _panes[p];
            PaneRect r = pane.Rect;
            if (pane.Second < 0)
            {
                if (Drawable(pane.First) is not { } texture) continue;
                LoadedImage image = _compare[pane.First].Image!;
                _renderer!.Draw(texture, CanvasToScreen(pane.First, r).After(ImageToCanvas(pane.First)), ZoomOf(pane.First, image), _display, r.X, r.Y, r.Width, r.Height);
                continue;
            }

            // Two images in one pane: each into its own target, then combined.
            GpuImage? a = Drawable(pane.First), b = Drawable(pane.Second);
            Affine2 aToPane = Affine2.Identity, bToPane = Affine2.Identity;
            double zoomA = 1, zoomB = 1;
            if (a != null)
            {
                aToPane = CanvasToPane(pane.First, r).After(ImageToCanvas(pane.First));
                zoomA = ZoomOf(pane.First, _compare[pane.First].Image!);
            }
            if (b != null)
            {
                bToPane = CanvasToPane(pane.Second, r).After(ImageToCanvas(pane.Second));
                zoomB = ZoomOf(pane.Second, _compare[pane.Second].Image!);
            }
            MixSettings mix = _compareMode switch
            {
                CompareMode.Split => new MixSettings { Kind = _splitRows ? MixKind.SplitRows : MixKind.SplitColumns, Amount = DividerAt(r) },
                CompareMode.Blend => new MixSettings { Kind = MixKind.Blend, Amount = _opacity },
                CompareMode.Difference => DifferenceMix(),
                _ => new MixSettings { Kind = MixKind.Checker, Amount = _checkerSize, OriginX = r.Width / 2, OriginY = r.Height / 2 },
            };
            try
            {
                if (_compareMode == CompareMode.Ssim)
                {
                    RenderSimilarity(pane, r);
                }
                else
                {
                    _compareRenderer ??= new CompareRenderer(_gpu!, _renderer!);
                    _compareRenderer.Draw(a, aToPane, zoomA, b, bToPane, zoomB, _display, mix, r.X, r.Y, r.Width, r.Height, Backdrop);
                }
                _compareError = null;
            }
            catch (GraphicsException e)
            {
                // Most likely no video memory for the two targets. The other modes still work.
                _compareError = "This compare mode could not be drawn: " + e.Message;
                _gpu!.BindBackBuffer();
                _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
            }
        }
    }

    /// <summary>After a frame of the comparison was presented: timestamps for the overlay, the log and the benchmarks.</summary>
    void AfterCompareFrame(long presented)
    {
        bool complete = _paneCount > 0;
        bool activeDrawn = false;
        for (int p = 0; p < _paneCount; p++)
        {
            for (int slot = 0; slot < 2; slot++)
            {
                int item = slot == 0 ? _panes[p].First : _panes[p].Second;
                if (slot == 1 && item < 0) continue;
                LoadedImage? image = item >= 0 && item < _compare.Count ? _compare[item].Image : null;
                if (image?.BestGpu == null)
                {
                    complete &= image?.Error != null;
                    continue;
                }
                if (image.FirstFrame == 0) image.FirstFrame = presented;
                if (image.GpuFull != null && image.FullFrame == 0) image.FullFrame = presented;
                complete &= image.GpuFull != null;
                activeDrawn |= item == _pick.Active;
            }
        }
        if (activeDrawn) Perf.StampOnce(ref _stampedFirstFrame, "first frame (D3D)", presented);
        if (activeDrawn && !_requestShown)
        {
            // The switch to the active image is on screen.
            _requestShown = true;
            _requestFirstFrame = presented;
            double ms = Stopwatch.GetElapsedTime(_requested, presented).TotalMilliseconds;
            _steps++;
            _stepMs += ms;
            _stepMaxMs = Math.Max(_stepMaxMs, ms);
            _lastStepMs = ms;
            LogStep(_compare[_pick.Active].Image!, ms);
        }
        if (complete && !_compareReady)
        {
            _compareReady = true;
            Perf.StampOnce(ref _stampedFullFrame, "full frame", presented);
            CompareLoaded(presented);
        }
    }

    /// <summary>Everything on show is there in full: report and, in benchmark runs, leave.</summary>
    void CompareLoaded(long presented)
    {
        try
        {
            if (_options.PerfOut != null && !_perfOutWritten)
            {
                _perfOutWritten = true;
                File.WriteAllText(_options.PerfOut, Perf.Serialize());
            }
            if (_options.PerfLog != null)
            {
                File.AppendAllText(_options.PerfLog,
                    $"compare_ready mode={_compareMode} images={_compare.Count} panes={_paneCount} " +
                    $"qpf={Stopwatch.Frequency} requested={_requested} ready={presented} " +
                    $"ready_ms={Stopwatch.GetElapsedTime(_requested, presented).TotalMilliseconds:F1}\n");
            }
        }
        catch (IOException)
        {
        }
        if (_options.ExitWhenLoaded) PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
    }

    // ---------------------------------------------------------------- what is drawn over the panes

    const uint ColorPlate = 0xB0000000, ColorDivider = 0xFFFFFFFF, ColorOutline = 0xC0000000;

    /// <summary>
    /// Text on the image, on a dark plate. The text is in the shared buffer, null-terminated.
    /// With <paramref name="fromRight"/> the position is the plate's right edge. Returns the plate's height.
    /// </summary>
    float Plate(ImDrawListPtr draw, float x, float y, uint color = ColorText, bool fromRight = false)
    {
        fixed (byte* text = _utf8)
        {
            Vector2 size = ImGui.CalcTextSize(text);
            float pad = MathF.Round(4 * (float)_dpiScale);
            float width = size.X + 2 * pad, height = size.Y + pad;
            if (fromRight) x -= width;
            draw.AddRectFilled(new Vector2(x, y), new Vector2(x + width, y + height), ColorPlate, 3);
            draw.AddText(new Vector2(x + pad, y + pad / 2), color, text);
            return height;
        }
    }

    /// <summary>Writes "2  name.jpg   6000 x 4000" for an image of the set into the shared buffer.</summary>
    bool WriteLabel(int item)
    {
        CompareItem entry = _compare[item];
        ReadOnlySpan<char> name = Path.GetFileName(entry.Path.AsSpan());
        if (entry.Image is not { HasSize: true } image) return Utf8.TryWrite(_utf8, $"{item + 1}  {name}\0", out _);
        if (!Utf8.TryWrite(_utf8, $"{item + 1}  {name}   {image.UprightWidth:0} x {image.UprightHeight:0}", out int n)) return false;
        // An image that has been moved says by how much: what is compared is no longer the file as it is.
        if (!entry.Pose.IsIdentity && Utf8.TryWrite(_utf8.AsSpan(n, _utf8.Length - n - 1), $"   moved  ", out int more))
        {
            n += more;
            n += WritePose(entry.Pose, _utf8.AsSpan(n, _utf8.Length - n - 1));
        }
        _utf8[n] = 0;
        return true;
    }

    /// <summary>Writes "2   x 120  y 45   R 10  G 20  B 30" for the pixel of an image under the linked cursor. False if there is none.</summary>
    bool WriteReadout(int item, bool numbered)
    {
        if (!TryGetComparePixel(item, out int x, out int y, out PixelValue value)) return false;
        int n;
        if (!(numbered ? Utf8.TryWrite(_utf8, $"{item + 1}   x {x}  y {y}   ", out n) : Utf8.TryWrite(_utf8, $"x {x}  y {y}   ", out n))) return false;
        n += FormatValue(value, _utf8.AsSpan(n, _utf8.Length - n - 16));
        if (value.FromPreview && Utf8.TryWrite(_utf8.AsSpan(n), $"  (preview)", out int more)) n += more;
        _utf8[n] = 0;
        return true;
    }

    /// <summary>Labels, readouts, the linked cursor, the active pane's frame and the divider. Drawn behind the panels, over the image.</summary>
    void DrawCompareOverlays()
    {
        LayoutCompare();
        if (_paneCount == 0) return;
        float scale = (float)_dpiScale;
        float margin = MathF.Round(6 * scale);
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        bool cursor = TryGetCursorCanvas(out double canvasX, out double canvasY);
        int hovered = PaneAt(_mouseX, _mouseY);

        for (int p = 0; p < _paneCount; p++)
        {
            Pane pane = _panes[p];
            PaneRect r = pane.Rect;
            Vector2 p0 = new(r.X, r.Y), p1 = new(r.Right, r.Bottom);
            draw.PushClipRect(p0, p1, true);

            if (pane.Second < 0)
            {
                DrawPaneState(draw, pane.First, r);
                if (pane.First >= 0)
                {
                    if (_pixelValues && _compare[pane.First].Image is { HasSize: true } image)
                        DrawPixelValues(draw, r, CanvasToScreen(pane.First, r), _canvasWidth, _canvasHeight, image, ImageToCanvas(pane.First).Inverse(), null, Affine2.Identity);
                    DrawRegion(draw, pane.First, r, scale);
                    DrawImagePlates(draw, pane.First, r.X + margin, r.Y + margin, fromRight: false, numbered: false);
                    if (_linkedCursor && cursor && p != hovered)
                    {
                        (double sx, double sy) = CanvasToScreen(pane.First, r).Apply(canvasX, canvasY);
                        DrawCrosshair(draw, (float)sx, (float)sy, scale);
                    }
                }
                if (_compareMode.HasPanes() && pane.First == _pick.Active) draw.AddRect(p0, p1, ColorCurrent);
            }
            else
            {
                // Two images in the pane: the first is named in the left corner, the second in the right one.
                if (_pixelValues && pane.First >= 0 && pane.Second >= 0 &&
                    _compare[pane.First].Image is { HasSize: true } imageA && _compare[pane.Second].Image is { HasSize: true } imageB)
                {
                    DrawPixelValues(draw, r, CanvasToScreen(pane.First, r), _canvasWidth, _canvasHeight,
                        imageA, ImageToCanvas(pane.First).Inverse(), imageB, ImageToCanvas(pane.Second).Inverse());
                }
                DrawRegion(draw, pane.First, r, scale);
                DrawPaneState(draw, pane.First, r, pane.Second);
                DrawImagePlates(draw, pane.First, r.X + margin, r.Y + margin, fromRight: false, numbered: true);
                DrawImagePlates(draw, pane.Second, r.Right - margin, r.Y + margin, fromRight: true, numbered: true);
                if (_compareMode == CompareMode.Split) DrawDivider(draw, r, scale);
                if (_compareMode is CompareMode.Difference or CompareMode.Ssim)
                {
                    DrawColorBar(draw, r, scale);
                    // What the two differ by under the cursor, between their two readouts.
                    if (WriteDifferenceReadout())
                    {
                        fixed (byte* text = _utf8)
                        {
                            float width = ImGui.CalcTextSize(text).X + 2 * MathF.Round(4 * scale);
                            Plate(draw, MathF.Floor(r.X + (r.Width - width) / 2), r.Y + margin, ColorDelta);
                        }
                    }
                    if (_compareMode == CompareMode.Ssim && !SimilarityReady && _compareError == null && Drawable(pane.First) != null && Drawable(pane.Second) != null)
                    {
                        Utf8Of(_metricsError ?? (TryMetricsKey(out _) ? "Computing the structural similarity..." : "Waiting for both images in full..."));
                        fixed (byte* text = _utf8)
                        {
                            Vector2 size = ImGui.CalcTextSize(text);
                            Plate(draw, MathF.Floor(r.X + (r.Width - size.X) / 2), MathF.Floor(r.Y + (r.Height - size.Y) / 2), ColorTextDim);
                        }
                    }
                }
            }
            draw.PopClipRect();
        }

        if (_dragDivider || (!_panning && NearDivider(_mouseX, _mouseY)))
            ImGui.SetMouseCursor(_splitRows ? ImGuiMouseCursor.ResizeNs : ImGuiMouseCursor.ResizeEw);
        else if ((_regionArmed || _alignRegionArmed || _dragRegion) && hovered >= 0)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (_dragAlign || (_alignMode && hovered >= 0))
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); // a drag moves the image, not the view
    }

    /// <summary>
    /// An image's name and, under it, the position and values of its pixel under the linked cursor.
    /// The readout is always shown; with the names switched off it says which image it belongs to.
    /// </summary>
    void DrawImagePlates(ImDrawListPtr draw, int item, float x, float y, bool fromRight, bool numbered)
    {
        if (item < 0 || item >= _compare.Count) return;
        if (_paneLabels && WriteLabel(item)) y += Plate(draw, x, y, fromRight: fromRight) + MathF.Round(2 * (float)_dpiScale);
        if (WriteReadout(item, numbered && !_paneLabels)) Plate(draw, x, y, fromRight: fromRight);
    }

    /// <summary>The name and size of an image shown on its own, in the corner where a pane of a comparison has them.</summary>
    void DrawSingleLabel()
    {
        if (!_paneLabels || Comparing || _shown is not { } image) return;
        ReadOnlySpan<char> name = Path.GetFileName(image.Path.AsSpan());
        if (!(image.HasSize ? Utf8.TryWrite(_utf8, $"{name}   {image.UprightWidth:0} x {image.UprightHeight:0}\0", out _) : Utf8.TryWrite(_utf8, $"{name}\0", out _))) return;
        float margin = MathF.Round(6 * (float)_dpiScale);
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(new Vector2(ViewX, ViewY), new Vector2(ViewX + ViewWidth, ViewY + ViewHeight), true);
        Plate(draw, ViewX + margin, ViewY + margin);
        draw.PopClipRect();
    }

    /// <summary>What a pane says while it has no picture: that it is loading, or why it cannot be shown.</summary>
    void DrawPaneState(ImDrawListPtr draw, int first, in PaneRect r, int second = -1)
    {
        string? text = _compareError;
        if (text == null)
        {
            for (int slot = 0; slot < 2 && text == null; slot++)
            {
                int item = slot == 0 ? first : second;
                if (item < 0 || item >= _compare.Count)
                {
                    if (slot == 0) text = "Loading";
                    continue;
                }
                LoadedImage? image = _compare[item].Image;
                if (image?.Error != null) text = image.Error;
                else if (image?.BestGpu == null) text = "Loading";
            }
        }
        if (text == null) return;
        Utf8Of(text);
        fixed (byte* bytes = _utf8)
        {
            Vector2 size = ImGui.CalcTextSize(bytes);
            Plate(draw, MathF.Floor(r.X + (r.Width - size.X) / 2), MathF.Floor(r.Y + (r.Height - size.Y) / 2), ColorTextDim);
        }
    }

    /// <summary>Marks, in a pane the mouse is not in, the point that is under the mouse in the pane it is in.</summary>
    static void DrawCrosshair(ImDrawListPtr draw, float x, float y, float scale)
    {
        x = MathF.Floor(x) + 0.5f;
        y = MathF.Floor(y) + 0.5f;
        float gap = 4 * scale, arm = 14 * scale;
        for (int pass = 0; pass < 2; pass++)
        {
            uint color = pass == 0 ? ColorOutline : ColorDivider;
            float thickness = pass == 0 ? 3 * scale : scale;
            draw.AddLine(new Vector2(x - arm, y), new Vector2(x - gap, y), color, thickness);
            draw.AddLine(new Vector2(x + gap, y), new Vector2(x + arm, y), color, thickness);
            draw.AddLine(new Vector2(x, y - arm), new Vector2(x, y - gap), color, thickness);
            draw.AddLine(new Vector2(x, y + gap), new Vector2(x, y + arm), color, thickness);
        }
    }

    void DrawDivider(ImDrawListPtr draw, in PaneRect r, float scale)
    {
        float at = DividerAt(r);
        Vector2 from = _splitRows ? new Vector2(r.X, r.Y + at) : new Vector2(r.X + at, r.Y);
        Vector2 to = _splitRows ? new Vector2(r.Right, r.Y + at) : new Vector2(r.X + at, r.Bottom);
        draw.AddLine(from, to, ColorOutline, 3 * scale);
        draw.AddLine(from, to, ColorDivider, scale);
        // A grip in the middle of the line shows that it can be dragged.
        Vector2 middle = (from + to) / 2;
        Vector2 half = _splitRows ? new Vector2(14, 4) * scale : new Vector2(4, 14) * scale;
        draw.AddRectFilled(middle - half - Vector2.One, middle + half + Vector2.One, ColorOutline, 3 * scale);
        draw.AddRectFilled(middle - half, middle + half, ColorDivider, 3 * scale);
    }

    static ReadOnlySpan<byte> ModeLabel(CompareMode mode) => mode switch
    {
        CompareMode.Single => "Single"u8,
        CompareMode.SideBySide => "Side by side"u8,
        CompareMode.Grid => "Grid"u8,
        CompareMode.Split => "Split"u8,
        CompareMode.Blend => "Blend"u8,
        CompareMode.Difference => "Difference"u8,
        CompareMode.Ssim => "SSIM"u8,
        _ => "Checkerboard"u8,
    };

    static string ModeName(CompareMode mode) => mode switch
    {
        CompareMode.Single => "single",
        CompareMode.SideBySide => "side by side",
        CompareMode.Grid => "grid",
        CompareMode.Split => "split",
        CompareMode.Blend => "blend",
        CompareMode.Difference => "difference",
        CompareMode.Ssim => "structural similarity",
        _ => "checkerboard",
    };

    float _comparePanelHeight; // as drawn last, so that a notice can be put above it

    /// <summary>
    /// The compare controls: mode, its parameter, and what is linked. At the bottom of the view,
    /// in the middle: the names and readouts of the panes are in their top corners.
    /// </summary>
    void DrawComparePanel()
    {
        float scale = (float)_dpiScale;
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight - 8 * scale), ImGuiCond.Always, new Vector2(0.5f, 1));
        ImGui.SetNextWindowBgAlpha(0.9f);
        if (ImGui.Begin("Compare"u8, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            _comparePanelHeight = ImGui.GetWindowSize().Y;
            Vector4 on = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
            for (CompareMode mode = CompareMode.Single; mode <= CompareModeExtensions.Last; mode++)
            {
                if (mode != CompareMode.Single) ImGui.SameLine();
                bool current = mode == _compareMode;
                if (current) ImGui.PushStyleColor(ImGuiCol.Button, on);
                if (ImGui.Button(ModeLabel(mode)))
                {
                    CompareMode chosen = mode;
                    _deferred = () => SetCompareMode(chosen);
                }
                if (current) ImGui.PopStyleColor();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("|"u8);
            ImGui.SameLine();
            if (ImGui.Button("Swap"u8)) _pick.Swapped = !_pick.Swapped;
            ImGui.SameLine();
            if (ImGui.Button("Stop"u8)) _deferred = () => StopCompare();

            ImGui.PushItemWidth(180 * scale);
            switch (_compareMode)
            {
                case CompareMode.Single:
                {
                    bool blink = _blink;
                    if (ImGui.Checkbox("Blink (K)"u8, ref blink)) _deferred = ToggleBlink;
                    ImGui.SameLine();
                    int interval = _blinkMs;
                    if (ImGui.SliderInt("ms"u8, ref interval, Settings.MinBlinkMs, 2000)) SetBlinkInterval(interval);
                    if (_compare.Count > 2)
                    {
                        ImGui.SameLine();
                        ImGui.Checkbox("through all"u8, ref _blinkAll);
                    }
                    break;
                }
                case CompareMode.Split:
                    ImGui.SliderFloat("divider"u8, ref _splitAt, 0, 1);
                    ImGui.SameLine();
                    if (ImGui.RadioButton("left | right"u8, !_splitRows)) _splitRows = false;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("top | bottom"u8, _splitRows)) _splitRows = true;
                    break;
                case CompareMode.Blend:
                    ImGui.SliderFloat("opacity of the second image"u8, ref _opacity, 0, 1);
                    break;
                case CompareMode.Checker:
                    ImGui.SliderInt("cell size"u8, ref _checkerSize, Settings.MinCheckerSize, Settings.MaxCheckerSize);
                    break;
                case CompareMode.Difference:
                    for (DifferenceKind kind = DifferenceKind.Absolute; kind <= DifferenceKind.Relative; kind++)
                    {
                        if (kind != DifferenceKind.Absolute) ImGui.SameLine();
                        if (ImGui.RadioButton(KindLabel(kind), _differenceKind == kind)) _differenceKind = kind;
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled("|"u8);
                    ImGui.SameLine();
                    ImGui.Checkbox("mark what is over the threshold (W)"u8, ref _mask);
                    if (ImGui.SliderFloat("gain"u8, ref _differenceGain, 1, MaxGain, "%.4g x"u8, ImGuiSliderFlags.Logarithmic)) _autoGain = false;
                    ImGui.SameLine();
                    if (ImGui.Button("Auto"u8)) AutoGain();
                    ImGui.SameLine();
                    DrawColormapChoice();
                    break;
                case CompareMode.Ssim:
                    ImGui.SliderFloat("gain"u8, ref _similarityGain, 1, MaxSimilarityGain, "%.4g x"u8, ImGuiSliderFlags.Logarithmic);
                    ImGui.SameLine();
                    DrawColormapChoice();
                    break;
                default:
                {
                    bool linked = _linked;
                    if (ImGui.Checkbox("Link zoom and position (L)"u8, ref linked)) SetLinked(linked);
                    ImGui.SameLine();
                    ImGui.Checkbox("Linked cursor"u8, ref _linkedCursor);
                    break;
                }
            }
            ImGui.PopItemWidth();

            ImGui.TextDisabled("Different sizes:"u8);
            ImGui.SameLine();
            if (ImGui.RadioButton("top-left"u8, _sizeMatch == SizeMatch.TopLeft)) _sizeMatch = SizeMatch.TopLeft;
            ImGui.SameLine();
            if (ImGui.RadioButton("centred"u8, _sizeMatch == SizeMatch.Centre)) _sizeMatch = SizeMatch.Centre;
            ImGui.SameLine();
            if (ImGui.RadioButton("scaled to fit"u8, _sizeMatch == SizeMatch.ScaleToFit)) _sizeMatch = SizeMatch.ScaleToFit;
            ImGui.SameLine();
            ImGui.TextDisabled("|"u8);
            ImGui.SameLine();
            ImGui.Checkbox("Labels"u8, ref _paneLabels);
            ImGui.SameLine();
            ImGui.Checkbox("Metrics (N)"u8, ref _showMetrics);
            ImGui.SameLine();
            ImGui.Checkbox("Pixel values (V)"u8, ref _pixelValues);
            ImGui.SameLine();
            bool align = _alignMode;
            if (ImGui.Checkbox("Align (A)"u8, ref align)) SetAlignMode(align);
        }
        ImGui.End();
    }

    /// <summary>The colour map of the difference or similarity view, as a drop-down list.</summary>
    void DrawColormapChoice()
    {
        Colormap current = CurrentMap;
        Utf8Of(current.DisplayName());
        ImGui.SetNextItemWidth(130 * (float)_dpiScale);
        bool open;
        fixed (byte* preview = _utf8) open = ImGui.BeginCombo("colours (G)"u8, preview);
        if (!open) return;
        for (int i = 0; i < Colormaps.Count; i++)
        {
            var map = (Colormap)i;
            Utf8Of(map.DisplayName());
            fixed (byte* label = _utf8)
                if (ImGui.Selectable(label, map == current)) SetCurrentMap(map);
        }
        ImGui.EndCombo();
    }

    // ---------------------------------------------------------------- input

    /// <summary>The keys that mean something else while comparing. True if the key was one of them.</summary>
    bool CompareKey(VIRTUAL_KEY key, bool shift, bool control, bool repeat)
    {
        if (control) return false;
        int digit = key is >= VIRTUAL_KEY.VK_1 and <= VIRTUAL_KEY.VK_9 ? key - VIRTUAL_KEY.VK_1
            : key is >= VIRTUAL_KEY.VK_NUMPAD1 and <= VIRTUAL_KEY.VK_NUMPAD9 ? key - VIRTUAL_KEY.VK_NUMPAD1 : -1;
        if (digit >= 0)
        {
            // Down: go to that image. Up, if the key was held: back again (OnKeyUp).
            if (!repeat && digit < _compare.Count)
            {
                StopBlink();
                if (_pick.Press((int)key, digit, Stopwatch.GetTimestamp())) OnComparePick();
            }
            return true;
        }
        if (AlignKey(key, shift)) return true; // while aligning, the arrow keys and their neighbours move the image
        switch (key)
        {
            case VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_NEXT or VIRTUAL_KEY.VK_SPACE:
                StopBlink();
                if (_pick.Step(1)) OnComparePick();
                return true;
            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_BACK:
                StopBlink();
                if (_pick.Step(-1)) OnComparePick();
                return true;
            case VIRTUAL_KEY.VK_HOME:
                StopBlink();
                if (_pick.Select(0)) OnComparePick();
                return true;
            case VIRTUAL_KEY.VK_END:
                StopBlink();
                if (_pick.Select(_compare.Count - 1)) OnComparePick();
                return true;
            case VIRTUAL_KEY.VK_UP:
                AdjustMix(1, shift);
                return true;
            case VIRTUAL_KEY.VK_DOWN:
                AdjustMix(-1, shift);
                return true;
            case VIRTUAL_KEY.VK_M:
            {
                const int modes = (int)CompareModeExtensions.Last + 1;
                SetCompareMode((CompareMode)(((int)_compareMode + (shift ? modes - 1 : 1)) % modes));
                return true;
            }
            case VIRTUAL_KEY.VK_X:
                _pick.Swapped = !_pick.Swapped;
                return true;
            case VIRTUAL_KEY.VK_K:
                ToggleBlink();
                return true;
            case VIRTUAL_KEY.VK_L:
                SetLinked(!_linked);
                Notice(_linked ? "Zoom and position linked." : "Each pane has its own zoom and position.");
                return true;
            case VIRTUAL_KEY.VK_P:
                _showComparePanel = !_showComparePanel;
                return true;
        }
        return DifferenceKey(key, shift);
    }

    void CompareKeyUp(VIRTUAL_KEY key)
    {
        long hold = (long)(PeekHoldMs * Stopwatch.Frequency / 1000);
        if (_pick.Release((int)key, Stopwatch.GetTimestamp(), hold)) OnComparePick();
    }

    /// <summary>Zooms at the centre of the pane: the active image's pane if each has its own view.</summary>
    void CompareZoom(double value, bool absolute)
    {
        LayoutCompare();
        if (_paneCount == 0 || _canvasWidth <= 0) return;
        PaneRect pane = _panes[0].Rect;
        ref ViewState view = ref ViewOf(_pick.Active);
        view.ZoomAt(absolute ? value : view.Zoom * value, pane.Width / 2.0, pane.Height / 2.0, pane.Width, pane.Height);
    }

    void CompareWheel(double factor, int x, int y)
    {
        LayoutCompare();
        int p = PaneAt(x, y);
        if (p < 0 || _panes[p].First < 0 || _canvasWidth <= 0) return;
        PaneRect pane = _panes[p].Rect;
        ref ViewState view = ref ViewOf(_panes[p].First);
        view.ZoomAt(view.Zoom * factor, x - pane.X, y - pane.Y, pane.Width, pane.Height);
    }

    /// <summary>
    /// The left button went down: on the divider it drags the divider, elsewhere it makes the
    /// pane's image the active one and pans; while aligning it moves the image that is being aligned.
    /// </summary>
    void CompareMouseDown(int x, int y, bool shift)
    {
        LayoutCompare();
        _dragItem = -1;
        // A rectangle, if one was asked for: the one the alignment goes by, or the numbers'. With
        // Shift a drag draws the numbers' too, except while aligning, where Shift turns the image.
        if (_alignRegionArmed && BeginRegion(x, y, forAlign: true)) return;
        if ((_regionArmed || (shift && !_alignMode)) && BeginRegion(x, y)) return;
        _dragDivider = NearDivider(x, y);
        if (_dragDivider) return;
        int p = PaneAt(x, y);
        if (p < 0) return;
        _dragItem = _panes[p].First;
        if (_alignMode && _dragItem >= 0 && BeginAlignDrag(turn: shift)) return; // and the pair stays as it is
        if (_compareMode.HasPanes() && _dragItem >= 0 && _pick.Select(_dragItem))
        {
            StopBlink();
            OnComparePick();
        }
    }

    void CompareDrag(int x, int y, int dx, int dy)
    {
        if (_dragRegion)
        {
            DragRegion(x, y);
        }
        else if (_dragDivider)
        {
            if (_paneCount == 0) return;
            PaneRect pane = _panes[0].Rect;
            _splitAt = Math.Clamp(_splitRows ? (y - pane.Y) / (float)pane.Height : (x - pane.X) / (float)pane.Width, 0, 1);
        }
        else if (_dragAlign)
        {
            AlignDrag(x, y, dx, dy);
        }
        else if (_dragItem >= 0 && _canvasWidth > 0)
        {
            ViewOf(_dragItem).Pan(dx, dy, _canvasWidth, _canvasHeight);
        }
    }

    void CompareDoubleClick(int x, int y)
    {
        LayoutCompare();
        int p = PaneAt(x, y);
        if (p < 0 || _panes[p].First < 0 || _canvasWidth <= 0 || NearDivider(x, y)) return;
        PaneRect pane = _panes[p].Rect;
        ref ViewState view = ref ViewOf(_panes[p].First);
        if (view.Fit || view.Zoom != 1) view.ZoomAt(1, x - pane.X, y - pane.Y, pane.Width, pane.Height);
        else view.Fit = true;
    }

    /// <summary>A right click makes the pane under it the active one before its menu opens.</summary>
    void CompareRightClick(int x, int y)
    {
        LayoutCompare();
        int p = PaneAt(x, y);
        if (p >= 0 && _compareMode.HasPanes() && _panes[p].First >= 0 && _pick.Select(_panes[p].First)) OnComparePick();
    }
}
