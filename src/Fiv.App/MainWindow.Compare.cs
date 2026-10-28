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
//
// The modes that show several images at once take the images that are shown, all of them
// unless some are taken out, beginning with the reference (see CompareSelection): side by side
// and in the grid a pane each; split, blend, overlay and checkerboard all in one pane; the
// difference and the similarity a pane for each image against the reference.
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

        /// <summary>Its opacity over the images before it in the blend, once the blend is no longer an equal mix.</summary>
        public float Opacity = 0.5f;

        /// <summary>Its opacity in the overlay, where every image counts by its own (see OverlayMix): 1 opaque, 0 not there.</summary>
        public float OverlayOpacity = 1;

        /// <summary>What its last automatic alignment said (see MainWindow.Align).</summary>
        public string? AlignReport;

        /// <summary>How alike it is to the reference as it lies, and what that was worked out for (see MainWindow.Align).</summary>
        public double Score = double.NaN;
        public ScoreKey ScoreKey;
        public bool ScoreKnown;
    }

    /// <summary>A rectangle of the view and the images of the set shown in it.</summary>
    struct Pane
    {
        public PaneRect Rect;

        /// <summary>The image shown; of a pane that compares two, the reference; of a stack, the first of it.</summary>
        public int First;

        /// <summary>The image compared with the reference, or -1.</summary>
        public int Second;

        /// <summary>The pane lays every image shown into one (see <see cref="_order"/>).</summary>
        public bool Stack;
    }

    readonly List<CompareItem> _compare = [];
    readonly CompareSelection _pick = new();
    readonly Pane[] _panes = new Pane[MaxCompare];
    int _paneCount;

    /// <summary>The images shown, in the order the views take them: the reference first.</summary>
    readonly int[] _order = new int[MaxCompare];
    int _orderCount;

    CompareRenderer? _compareRenderer;
    readonly MixLayer[] _layers = new MixLayer[MaxCompare];
    readonly float[] _overlayOpacities = new float[MaxCompare], _overlayPasses = new float[MaxCompare];
    CompareMode _compareMode = CompareMode.SideBySide;
    SizeMatch _sizeMatch = SizeMatch.TopLeft;
    double _canvasWidth, _canvasHeight;
    int _singleItem = -1; // the image the single view shows: the active one once it can be drawn
    bool _linked = true, _linkedCursor = true, _paneLabels = true, _showComparePanel = true;
    bool _splitRows, _blink, _blinkAll;

    /// <summary>The dividers of the split, as fractions of the pane from its left or top edge, in increasing order: one fewer than there are strips.</summary>
    readonly float[] _dividers = new float[MaxCompare - 1];
    int _dividersFor;          // the number of strips they are laid out for: another number starts them over, evenly
    bool _blendEqual = true;   // the blend is an equal mix of the images shown, until an opacity is set by hand
    int _checkerSize = 64, _blinkMs = 500;
    int _dragDivider = -1;
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
        _dragDivider = -1;
        _dividersFor = 0;
        _blendEqual = true;
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
        _orderCount = 0;
        _singleItem = -1;
        _dragItem = -1;
        _dragDivider = -1;
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
        if (!mode.IsOverlay()) _compareRenderer?.ReleaseTargets(); // only the modes that combine images need them
        if (!Comparing) return;
        _dragItem = -1;
        _dragDivider = -1;
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

    /// <summary>Takes an image into the views that show several, or out of them; says why not if it cannot be.</summary>
    void SetShown(int item, bool shown)
    {
        if (_pick.SetShown(item, shown))
        {
            _compareReady = false;
            Invalidate();
        }
        else if (!shown && _pick.IsShown(item))
        {
            Notice(item == _pick.Reference ? "The reference stays in the views: make another image the reference first." : "One image at least stays in the views.");
        }
    }

    /// <summary>Makes an image the reference: the others are compared with it, and aligned to it.</summary>
    void SetReference(int item)
    {
        if (!_pick.SetReference(item)) return;
        OnReferenceChanged();
    }

    /// <summary>X, Shift+X: the next or the previous image shown becomes the reference; of two, they swap.</summary>
    void StepReference(int delta)
    {
        if (!_pick.StepReference(delta))
        {
            Notice("Only the reference is in the views: there is no other image to make the reference.");
            return;
        }
        OnReferenceChanged();
        Notice($"Reference: {_pick.Reference + 1}  {Path.GetFileName(_compare[_pick.Reference].Path)}");
    }

    void OnReferenceChanged()
    {
        _compareReady = false;
        _alignLastAction = AlignAction.None; // what is moved has changed: undo starts over
        if (_alignRunning) StopAutoAlign("Stopped: the reference changed."); // the poses on their way are to the reference that was
        Invalidate();
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

    /// <summary>Where an image is among those shown, in the order the views take them; -1 if it is not shown.</summary>
    int PositionOf(int item) => Array.IndexOf(_order, item, 0, _orderCount);

    /// <summary>
    /// Up and Down change the one parameter the current mode has. Of a split with several
    /// dividers, the one after the active image's strip; of a blend, the active image's opacity,
    /// or the top one's if the active image is at the bottom or not shown; of the overlay, the
    /// active image's opacity, or the first one's in the view if the active one is not.
    /// </summary>
    void AdjustMix(int direction, bool fine)
    {
        switch (_compareMode)
        {
            case CompareMode.Split when _orderCount > 1:
            {
                int position = PositionOf(_pick.Active), divider = position >= 0 && position < _orderCount - 1 ? position : _orderCount - 2;
                MoveDivider(divider, _dividers[divider] + direction * (fine ? 0.01f : 0.05f));
                break;
            }
            case CompareMode.Blend when _orderCount > 1:
            {
                int position = PositionOf(_pick.Active);
                if (position <= 0) position = _orderCount - 1;
                SetOpacity(position, MathF.Round((OpacityOf(position) + direction * (fine ? 0.01f : 0.1f)) * 100) / 100);
                break;
            }
            case CompareMode.Overlay when _orderCount > 0:
            {
                CompareItem item = _compare[PositionOf(_pick.Active) >= 0 ? _pick.Active : _order[0]];
                item.OverlayOpacity = Math.Clamp(MathF.Round((item.OverlayOpacity + direction * (fine ? 0.01f : 0.1f)) * 100) / 100, 0, 1);
                break;
            }
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

    // ---------------------------------------------------------------- split and blend

    /// <summary>Lays the dividers out evenly for the strips there are, if they are laid out for another number.</summary>
    void EnsureDividers()
    {
        if (_dividersFor == _orderCount) return;
        _dividersFor = _orderCount;
        for (int i = 0; i < _orderCount - 1; i++) _dividers[i] = (i + 1) / (float)_orderCount;
    }

    /// <summary>Moves a divider, as far as the dividers next to it and the pane's edges let it go.</summary>
    void MoveDivider(int divider, float at)
    {
        if (divider < 0 || divider >= _orderCount - 1) return;
        float low = divider > 0 ? _dividers[divider - 1] : 0, high = divider < _orderCount - 2 ? _dividers[divider + 1] : 1;
        _dividers[divider] = Math.Clamp(at, low, high);
    }

    /// <summary>The opacity of the image at a place in the blend over those before it: an equal mix until one is set by hand. The first lies over nothing.</summary>
    float OpacityOf(int position) =>
        position <= 0 ? 1 : _blendEqual ? 1f / (position + 1) : _compare[_order[position]].Opacity;

    /// <summary>Sets an opacity by hand. The others keep the share of the mix they had.</summary>
    void SetOpacity(int position, float opacity)
    {
        if (position <= 0 || position >= _orderCount) return;
        if (_blendEqual)
        {
            for (int k = 1; k < _orderCount; k++) _compare[_order[k]].Opacity = OpacityOf(k);
            _blendEqual = false;
        }
        _compare[_order[position]].Opacity = Math.Clamp(opacity, 0, 1);
    }

    // ---------------------------------------------------------------- layout and transforms

    /// <summary>The view an image is drawn with: the shared one, or its own where each pane has its own.</summary>
    ref ViewState ViewOf(int item)
    {
        if (_linked || !_compareMode.HasPanes() || item < 0 || item >= _compare.Count) return ref _view;
        return ref _compare[item].View;
    }

    /// <summary>The image a pane is of: the one compared with the reference, or the one it shows.</summary>
    static int PaneItem(in Pane pane) => pane.Second >= 0 ? pane.Second : pane.First;

    /// <summary>
    /// Works out the canvas and the panes from the set, the mode and the view rectangle, and
    /// keeps views that are fitted to the window fitted. Cheap, and called before anything that
    /// uses panes, so that they never lag behind the state.
    /// </summary>
    void LayoutCompare()
    {
        _paneCount = 0;
        _orderCount = 0;
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
        _orderCount = _pick.Order(_order);
        EnsureDividers();

        var area = new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        int gap = Math.Max(1, (int)Math.Round(2 * _dpiScale));
        switch (_compareMode)
        {
            case CompareMode.Single:
                _panes[_paneCount++] = new Pane { Rect = area, First = _singleItem, Second = -1 };
                break;
            case CompareMode.SideBySide or CompareMode.Grid:
            {
                (int columns, int rows) = _compareMode == CompareMode.Grid
                    ? CompareGeometry.BestGrid(_orderCount, area.Width, area.Height, width, height, gap)
                    : CompareGeometry.BestRow(_orderCount, area.Width, area.Height, width, height, gap);
                for (int i = 0; i < _orderCount; i++)
                    _panes[_paneCount++] = new Pane { Rect = CompareGeometry.Pane(i, columns, rows, area, gap), First = _order[i], Second = -1 };
                break;
            }
            case CompareMode.Difference or CompareMode.Ssim:
            {
                // A pane for each image against the reference; the reference alone if it is the only one shown.
                int compared = Math.Max(1, _orderCount - 1);
                (int columns, int rows) = CompareGeometry.BestGrid(compared, area.Width, area.Height, width, height, gap);
                for (int i = 0; i < compared; i++)
                    _panes[_paneCount++] = new Pane { Rect = CompareGeometry.Pane(i, columns, rows, area, gap), First = _order[0], Second = _orderCount > 1 ? _order[i + 1] : -1 };
                break;
            }
            default:
                _panes[_paneCount++] = new Pane { Rect = area, First = _order[0], Second = -1, Stack = true };
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

    /// <summary>A divider of the split, in pixels from the pane's left (or top) edge.</summary>
    float DividerAt(in PaneRect pane, int divider) => MathF.Round(_dividers[divider] * (_splitRows ? pane.Height : pane.Width));

    /// <summary>The divider of the split within reach of a point of the window, or -1.</summary>
    int DividerNear(int x, int y)
    {
        if (_compareMode != CompareMode.Split || _paneCount == 0 || !_panes[0].Stack || _surface) return -1;
        PaneRect pane = _panes[0].Rect;
        if (!pane.Contains(x, y)) return -1;
        float at = _splitRows ? y - pane.Y : x - pane.X, best = 6 * (float)_dpiScale;
        int nearest = -1;
        for (int i = 0; i < _orderCount - 1; i++)
        {
            float divider = DividerAt(pane, i), distance = MathF.Abs(at - divider);
            // Of dividers on top of each other, the one that can go where the mouse is: the first to its left, the last to its right.
            if (distance < best || (distance == best && nearest >= 0 && at > divider))
            {
                best = distance;
                nearest = i;
            }
        }
        return nearest;
    }

    /// <summary>The canvas point under the mouse, if the mouse is over a pane.</summary>
    bool TryGetCursorCanvas(out double x, out double y)
    {
        x = y = 0;
        if (_canvasWidth <= 0 || SurfaceOnShow) return false; // in 3D the canvas is not where the flat panes would have it
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

    /// <summary>
    /// The images shown, as the combine pass lays them into the stack pane at <paramref name="r"/>:
    /// each through <paramref name="canvasToTarget"/> into the target, with its strip of a split
    /// (where the pane has it, through <paramref name="paneToTarget"/>), or the opacity of its pass
    /// in a blend or in the overlay. Into <see cref="_layers"/>; returns how many.
    /// </summary>
    int StackLayers(in PaneRect r, in Affine2 canvasToTarget, in Affine2 paneToTarget)
    {
        if (_compareMode == CompareMode.Overlay)
        {
            // Each image counts by its own opacity: drawn as layer over layer, with the opacities that make that.
            for (int k = 0; k < _orderCount; k++) _overlayOpacities[k] = _compare[_order[k]].OverlayOpacity;
            OverlayMix.PassOpacities(_overlayOpacities.AsSpan(0, _orderCount), _overlayPasses);
        }
        for (int k = 0; k < _orderCount; k++)
        {
            int item = _order[k];
            GpuImage? texture = Drawable(item);
            Affine2 toTarget = Affine2.Identity;
            double zoom = 1;
            if (texture != null)
            {
                toTarget = canvasToTarget.After(ImageToCanvas(item));
                zoom = canvasToTarget.A * PlacementOf(_compare[item].Image!).A * _compare[item].Pose.Scale;
            }
            float amount = 0;
            if (_compareMode == CompareMode.Split && k < _orderCount - 1)
            {
                (double x, double y) = paneToTarget.Apply(DividerAt(r, k), DividerAt(r, k));
                amount = (float)(_splitRows ? y : x);
            }
            else if (_compareMode == CompareMode.Blend)
            {
                amount = OpacityOf(k);
            }
            else if (_compareMode == CompareMode.Overlay)
            {
                amount = _overlayPasses[k];
            }
            _layers[k] = new MixLayer(texture, toTarget, zoom, amount);
        }
        return _orderCount;
    }

    /// <summary>How the stack is combined; its checkerboard's cells centred on <paramref name="originX"/>, <paramref name="originY"/> and <paramref name="cellSize"/> wide.</summary>
    MixSettings StackMix(float cellSize, float originX, float originY) => _compareMode switch
    {
        CompareMode.Split => new MixSettings { Kind = _splitRows ? MixKind.SplitRows : MixKind.SplitColumns },
        CompareMode.Blend or CompareMode.Overlay => new MixSettings { Kind = MixKind.Blend },
        _ => new MixSettings { Kind = MixKind.Checker, Amount = cellSize, OriginX = originX, OriginY = originY },
    };

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
            if (SurfaceOnShow)
            {
                RenderSurfacePane(pane);
                continue;
            }
            if (pane.Second < 0 && !pane.Stack)
            {
                if (Drawable(pane.First) is not { } texture) continue;
                LoadedImage image = _compare[pane.First].Image!;
                _renderer!.Draw(texture, CanvasToScreen(pane.First, r).After(ImageToCanvas(pane.First)), ZoomOf(pane.First, image), _display, r.X, r.Y, r.Width, r.Height);
                continue;
            }
            try
            {
                _compareRenderer ??= new CompareRenderer(_gpu!, _renderer!);
                if (pane.Stack)
                {
                    // All the images shown in one pane: a pass each.
                    int count = StackLayers(r, CanvasToPane(pane.First, r), Affine2.Identity);
                    _compareRenderer.Draw(_layers.AsSpan(0, count), _display, StackMix(_checkerSize, r.Width / 2f, r.Height / 2f), r.X, r.Y, r.Width, r.Height, Backdrop);
                }
                else if (_compareMode == CompareMode.Ssim)
                {
                    RenderSimilarity(pane, r);
                }
                else
                {
                    // An image against the reference: each into its own target, then their difference.
                    GpuImage? a = Drawable(pane.First), b = Drawable(pane.Second);
                    MixLayer first = a != null ? new MixLayer(a, CanvasToPane(pane.First, r).After(ImageToCanvas(pane.First)), ZoomOf(pane.First, _compare[pane.First].Image!)) : default;
                    MixLayer second = b != null ? new MixLayer(b, CanvasToPane(pane.Second, r).After(ImageToCanvas(pane.Second)), ZoomOf(pane.Second, _compare[pane.Second].Image!)) : default;
                    _compareRenderer.Draw([first, second], _display, DifferenceMix(), r.X, r.Y, r.Width, r.Height, Backdrop);
                }
                _compareError = null;
            }
            catch (GraphicsException e)
            {
                // Most likely no video memory for the targets. The other modes still work.
                _compareError = "This compare mode could not be drawn: " + e.Message;
                _gpu!.BindBackBuffer();
                _gpu.SetViewport(0, 0, _gpu.Width, _gpu.Height);
            }
        }
    }

    /// <summary>The images of a pane, into <paramref name="items"/> (room for <see cref="MaxCompare"/>); returns how many.</summary>
    int PaneImages(in Pane pane, Span<int> items)
    {
        if (pane.Stack)
        {
            _order.AsSpan(0, _orderCount).CopyTo(items);
            return _orderCount;
        }
        items[0] = pane.First;
        if (pane.Second < 0) return 1;
        items[1] = pane.Second;
        return 2;
    }

    /// <summary>After a frame of the comparison was presented: timestamps for the overlay, the log and the benchmarks.</summary>
    void AfterCompareFrame(long presented)
    {
        bool complete = _paneCount > 0;
        bool activeDrawn = false;
        Span<int> items = stackalloc int[MaxCompare];
        for (int p = 0; p < _paneCount; p++)
        {
            int count = PaneImages(_panes[p], items);
            for (int slot = 0; slot < count; slot++)
            {
                int item = items[slot];
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
        // In the overlay, how much of it is there.
        if (_compareMode == CompareMode.Overlay && Utf8.TryWrite(_utf8.AsSpan(n, _utf8.Length - n - 1), $"   {entry.OverlayOpacity * 100:0} %", out int opacity)) n += opacity;
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

    /// <summary>Labels, readouts, the linked cursor, the active pane's frame and the dividers. Drawn behind the panels, over the image.</summary>
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

            if (pane.Stack)
            {
                DrawStackOverlays(draw, pane, r, margin, scale);
            }
            else if (pane.Second < 0)
            {
                DrawPaneState(draw, pane, r);
                if (pane.First >= 0)
                {
                    if (_pixelValues && !_surface && _compare[pane.First].Image is { HasSize: true } image)
                        DrawPixelValues(draw, r, CanvasToScreen(pane.First, r), _canvasWidth, _canvasHeight, image, ImageToCanvas(pane.First).Inverse(), null, Affine2.Identity);
                    if (!_surface) DrawRegion(draw, pane.First, r, scale);
                    DrawImagePlates(draw, pane.First, r.X + margin, r.Y + margin, fromRight: false, numbered: false);
                    if (_compareMode.IsPairwise()) CentreNote(draw, r, "Tick another image in the compare panel (P) to compare it with this one, the reference.");
                }
            }
            else
            {
                // An image against the reference: the reference is named in the left corner, the image in the right one (below the 3D button).
                if (_pixelValues && !_surface && _compare[pane.First].Image is { HasSize: true } imageA && _compare[pane.Second].Image is { HasSize: true } imageB)
                {
                    DrawPixelValues(draw, r, CanvasToScreen(pane.First, r), _canvasWidth, _canvasHeight,
                        imageA, ImageToCanvas(pane.First).Inverse(), imageB, ImageToCanvas(pane.Second).Inverse());
                }
                if (!_surface) DrawRegion(draw, pane.First, r, scale);
                DrawPaneState(draw, pane, r);
                DrawImagePlates(draw, pane.First, r.X + margin, r.Y + margin, fromRight: false, numbered: true);
                DrawImagePlates(draw, pane.Second, r.Right - margin, BelowSurfaceButton(r.Right, r.Y + margin, margin), fromRight: true, numbered: true);
                DrawColorBar(draw, pane, r, scale);
                // What the two differ by under the cursor, between their two readouts.
                if (WriteDifferenceReadout(pane))
                {
                    fixed (byte* text = _utf8)
                    {
                        float width = ImGui.CalcTextSize(text).X + 2 * MathF.Round(4 * scale);
                        Plate(draw, MathF.Floor(r.X + (r.Width - width) / 2), r.Y + margin, ColorDelta);
                    }
                }
                if (_compareMode == CompareMode.Ssim && SimilarityOf(pane.First, pane.Second) == null && _compareError == null && Drawable(pane.First) != null && Drawable(pane.Second) != null)
                {
                    string? error = TryMetricsKey(pane.First, pane.Second, out MetricsKey key) && _metricsFailed.TryGetValue(key, out string? failed) ? failed : null;
                    CentreNote(draw, r, error ?? (TryMetricsKey(pane.First, pane.Second, out _) ? "Computing the structural similarity..." : "Waiting for both images in full..."));
                }
            }

            // The point under the mouse, in the panes the mouse is not in.
            if (_linkedCursor && cursor && p != hovered && pane.First >= 0)
            {
                (double sx, double sy) = CanvasToScreen(pane.First, r).Apply(canvasX, canvasY);
                DrawCrosshair(draw, (float)sx, (float)sy, scale);
            }
            if (_paneCount > 1 && PaneItem(pane) == _pick.Active) draw.AddRect(p0, p1, ColorCurrent);
            draw.PopClipRect();
        }

        if (_dragDivider >= 0 || (!_panning && DividerNear(_mouseX, _mouseY) >= 0))
            ImGui.SetMouseCursor(_splitRows ? ImGuiMouseCursor.ResizeNs : ImGuiMouseCursor.ResizeEw);
        else if ((_regionArmed || _alignRegionArmed || _dragRegion) && hovered >= 0)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (_dragAlign || (_alignMode && hovered >= 0))
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); // a drag moves the images, not the view
    }

    /// <summary>
    /// Over the pane that lays every image shown into one: the names and readouts, the dividers
    /// of a split, the region. Of two images, the first is named in the left corner and the
    /// second in the right one; of more, a split names each in its strip, and the blend and the
    /// checkerboard list them down the left side.
    /// </summary>
    void DrawStackOverlays(ImDrawListPtr draw, in Pane pane, in PaneRect r, float margin, float scale)
    {
        if (_pixelValues && !_surface && _orderCount >= 1 && _compare[_order[0]].Image is { HasSize: true } first)
        {
            // Of two, both and their difference; of more, the reference's.
            LoadedImage? second = _orderCount == 2 && _compare[_order[1]].Image is { HasSize: true } image ? image : null;
            DrawPixelValues(draw, r, CanvasToScreen(pane.First, r), _canvasWidth, _canvasHeight,
                first, ImageToCanvas(_order[0]).Inverse(), second, second != null ? ImageToCanvas(_order[1]).Inverse() : Affine2.Identity);
        }
        if (!_surface) DrawRegion(draw, pane.First, r, scale);
        DrawPaneState(draw, pane, r);
        bool numbered = _orderCount > 1;
        if (_orderCount <= 2)
        {
            DrawImagePlates(draw, _order[0], r.X + margin, r.Y + margin, fromRight: false, numbered);
            if (_orderCount == 2) DrawImagePlates(draw, _order[1], r.Right - margin, BelowSurfaceButton(r.Right, r.Y + margin, margin), fromRight: true, numbered);
        }
        else if (_compareMode == CompareMode.Split && !_surface)
        {
            for (int k = 0; k < _orderCount; k++)
            {
                float start = k > 0 ? DividerAt(r, k - 1) : 0, end = k < _orderCount - 1 ? DividerAt(r, k) : _splitRows ? r.Height : r.Width;
                if (end <= start) continue;
                Vector2 c0 = _splitRows ? new Vector2(r.X, r.Y + start) : new Vector2(r.X + start, r.Y);
                Vector2 c1 = _splitRows ? new Vector2(r.Right, r.Y + end) : new Vector2(r.X + end, r.Bottom);
                draw.PushClipRect(c0, c1, true);
                DrawImagePlates(draw, _order[k], c0.X + margin, BelowSurfaceButton(c1.X, c0.Y + margin, margin), fromRight: false, numbered);
                draw.PopClipRect();
            }
        }
        else
        {
            float y = r.Y + margin, gap = MathF.Round(4 * scale);
            for (int k = 0; k < _orderCount && y < r.Bottom; k++) y = DrawImagePlates(draw, _order[k], r.X + margin, y, fromRight: false, numbered) + gap;
        }
        if (_compareMode == CompareMode.Split && !_surface)
            for (int i = 0; i < _orderCount - 1; i++) DrawDivider(draw, r, i, scale);
    }

    /// <summary>A height <paramref name="y"/> moved below the 3D button in the view's top right corner, for what reaches as far right as <paramref name="right"/>.</summary>
    float BelowSurfaceButton(float right, float y, float margin) => right >= ViewX + ViewWidth - 1 ? MathF.Max(y, _surfacePanelBottom + margin) : y;

    /// <summary>A line of text on a plate in the middle of a pane.</summary>
    void CentreNote(ImDrawListPtr draw, in PaneRect r, string text)
    {
        Utf8Of(text);
        fixed (byte* bytes = _utf8)
        {
            Vector2 size = ImGui.CalcTextSize(bytes);
            Plate(draw, MathF.Floor(r.X + (r.Width - size.X) / 2), MathF.Floor(r.Y + (r.Height - size.Y) / 2), ColorTextDim);
        }
    }

    /// <summary>
    /// An image's name and, under it, the position and values of its pixel under the linked cursor.
    /// The readout is always shown; with the names switched off it says which image it belongs to.
    /// Returns where below them the next line could go.
    /// </summary>
    float DrawImagePlates(ImDrawListPtr draw, int item, float x, float y, bool fromRight, bool numbered)
    {
        if (item < 0 || item >= _compare.Count) return y;
        float gap = MathF.Round(2 * (float)_dpiScale);
        if (_paneLabels && WriteLabel(item)) y += Plate(draw, x, y, fromRight: fromRight) + gap;
        if (WriteReadout(item, numbered && !_paneLabels)) y += Plate(draw, x, y, fromRight: fromRight) + gap;
        return y;
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

    /// <summary>What a pane says while it has no picture: that it is loading, or why one of its images cannot be shown.</summary>
    void DrawPaneState(ImDrawListPtr draw, in Pane pane, in PaneRect r)
    {
        string? text = _compareError;
        if (text == null)
        {
            Span<int> items = stackalloc int[MaxCompare];
            int count = PaneImages(pane, items);
            for (int slot = 0; slot < count && text == null; slot++)
            {
                int item = items[slot];
                if (item < 0 || item >= _compare.Count)
                {
                    if (slot == 0) text = "Loading";
                    continue;
                }
                LoadedImage? image = _compare[item].Image;
                if (image?.Error != null) text = count > 1 ? $"{item + 1}: {image.Error}" : image.Error;
                else if (image?.BestGpu == null) text = "Loading";
            }
        }
        if (text != null) CentreNote(draw, r, text);
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

    void DrawDivider(ImDrawListPtr draw, in PaneRect r, int divider, float scale)
    {
        float at = DividerAt(r, divider);
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
        CompareMode.Overlay => "Overlay"u8,
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
        CompareMode.Overlay => "overlay",
        CompareMode.Difference => "difference",
        CompareMode.Ssim => "structural similarity",
        _ => "checkerboard",
    };

    float _comparePanelHeight; // as drawn last, so that a notice can be put above it

    /// <summary>
    /// The compare controls: mode, its parameters, what is linked, and the images of the set:
    /// which of them the views take in, which is the reference, and their opacities in the blend.
    /// At the bottom of the view, in the middle: the names and readouts of the panes are in their top corners.
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
            if (ImGui.Button(_orderCount == 2 ? "Swap (X)"u8 : "Next reference (X)"u8)) _deferred = () => StepReference(1);
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
                    if (_orderCount == 2)
                    {
                        float divider = _dividers[0];
                        if (ImGui.SliderFloat("divider"u8, ref divider, 0, 1)) MoveDivider(0, divider);
                        ImGui.SameLine();
                    }
                    else if (_orderCount > 2)
                    {
                        if (ImGui.Button("Equal strips"u8)) _dividersFor = 0;
                        ImGui.SameLine();
                    }
                    if (ImGui.RadioButton("left | right"u8, !_splitRows)) _splitRows = false;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("top | bottom"u8, _splitRows)) _splitRows = true;
                    break;
                case CompareMode.Blend:
                    if (_orderCount == 2)
                    {
                        float opacity = OpacityOf(1);
                        if (ImGui.SliderFloat("opacity of the second image"u8, ref opacity, 0, 1)) SetOpacity(1, opacity);
                    }
                    else if (_orderCount > 2)
                    {
                        ImGui.BeginDisabled(_blendEqual);
                        if (ImGui.Button("Equal mix"u8)) _blendEqual = true;
                        ImGui.EndDisabled();
                        ImGui.SameLine();
                        ImGui.TextDisabled("the reference at the bottom, each image after it over those before, with the opacity below"u8);
                    }
                    break;
                case CompareMode.Overlay:
                {
                    bool allOpaque = true;
                    for (int k = 0; k < _orderCount; k++) allOpaque &= _compare[_order[k]].OverlayOpacity >= 1;
                    ImGui.BeginDisabled(allOpaque);
                    if (ImGui.Button("All 100 %"u8))
                        foreach (CompareItem item in _compare) item.OverlayOpacity = 1;
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextDisabled("each image counts by its opacity, set below (Up / Down: the active one's); all at 100 %%: an equal mix"u8); // a format: %% is one %
                    break;
                }
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

            DrawCompareImages(scale);
        }
        ImGui.End();
    }

    /// <summary>
    /// The images of the set as a table: whether the views that show several take it in, which
    /// one is the reference, and in the blend and the overlay each one's opacity. A click on a
    /// name makes that image the active one.
    /// </summary>
    void DrawCompareImages(float scale)
    {
        if (!Utf8.TryWrite(_utf8, $"Images: {_orderCount} of {_compare.Count} in the views; the reference is {_pick.Reference + 1}###images\0", out _)) return;
        bool open;
        fixed (byte* label = _utf8) open = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
        if (!open) return;
        bool blend = _compareMode == CompareMode.Blend, overlay = _compareMode == CompareMode.Overlay;
        if (!ImGui.BeginTable("compared"u8, blend || overlay ? 4 : 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) return;
        ImGui.TableSetupColumn("In the views (Ctrl+1..9)"u8);
        ImGui.TableSetupColumn("Image"u8);
        ImGui.TableSetupColumn("Reference (X)"u8);
        if (blend) ImGui.TableSetupColumn("Opacity over those before"u8);
        if (overlay) ImGui.TableSetupColumn("Opacity"u8);
        ImGui.TableHeadersRow();
        for (int i = 0; i < _compare.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.TableNextRow();
            bool reference = i == _pick.Reference, shown = _pick.IsShown(i);

            ImGui.TableSetColumnIndex(0);
            ImGui.BeginDisabled(reference || (shown && _pick.ShownCount <= 1));
            if (ImGui.Checkbox("##shown"u8, ref shown))
            {
                int item = i;
                bool wanted = shown;
                _deferred = () => SetShown(item, wanted);
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(1);
            int n = Utf8.TryWrite(_utf8, $"{i + 1}  {Path.GetFileName(_compare[i].Path.AsSpan())}", out int written) ? written : 0;
            _utf8[n] = 0;
            fixed (byte* text = _utf8)
            {
                if (ImGui.Selectable(text, i == _pick.Active) && _pick.Select(i))
                {
                    StopBlink();
                    OnComparePick();
                }
            }

            ImGui.TableSetColumnIndex(2);
            if (ImGui.RadioButton("##reference"u8, reference) && !reference)
            {
                int item = i;
                _deferred = () => SetReference(item);
            }

            if (blend)
            {
                ImGui.TableSetColumnIndex(3);
                int position = PositionOf(i);
                if (position == 0)
                {
                    ImGui.TextDisabled("at the bottom"u8);
                }
                else if (position > 0)
                {
                    float opacity = OpacityOf(position);
                    ImGui.SetNextItemWidth(160 * scale);
                    if (ImGui.SliderFloat("##opacity"u8, ref opacity, 0, 1)) SetOpacity(position, opacity);
                }
            }
            else if (overlay && shown)
            {
                ImGui.TableSetColumnIndex(3);
                float percent = _compare[i].OverlayOpacity * 100;
                ImGui.SetNextItemWidth(220 * scale);
                if (ImGui.SliderFloat("##overlay"u8, ref percent, 0, 100, "%.0f %%"u8)) _compare[i].OverlayOpacity = Math.Clamp(percent / 100, 0, 1);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
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

    /// <summary>The digit of a number key, 1 to 9, as 0 to 8; -1 for any other key.</summary>
    static int DigitOf(VIRTUAL_KEY key) =>
        key is >= VIRTUAL_KEY.VK_1 and <= VIRTUAL_KEY.VK_9 ? key - VIRTUAL_KEY.VK_1
        : key is >= VIRTUAL_KEY.VK_NUMPAD1 and <= VIRTUAL_KEY.VK_NUMPAD9 ? key - VIRTUAL_KEY.VK_NUMPAD1 : -1;

    /// <summary>The keys that mean something else while comparing. True if the key was one of them.</summary>
    bool CompareKey(VIRTUAL_KEY key, bool shift, bool control, bool repeat)
    {
        int digit = DigitOf(key);
        if (control)
        {
            // Ctrl and a number: that image out of the views that show several, or back in.
            if (digit < 0 || shift) return false;
            if (!repeat && digit < _compare.Count) SetShown(digit, !_pick.IsShown(digit));
            return true;
        }
        if (shift && key is VIRTUAL_KEY.VK_3 or VIRTUAL_KEY.VK_NUMPAD3)
        {
            ToggleSurface(); // 3 alone is the third image
            return true;
        }
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
        if (AlignKey(key, shift)) return true; // while aligning, the arrow keys and their neighbours move the images
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
                StepReference(shift ? -1 : 1);
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
    /// The left button went down: on a divider it drags the divider, elsewhere it makes the
    /// pane's image the active one and pans; while aligning it moves the images that are being aligned.
    /// </summary>
    void CompareMouseDown(int x, int y, bool shift)
    {
        LayoutCompare();
        _dragItem = -1;
        // A rectangle, if one was asked for: the one the alignment goes by, or the numbers'. With
        // Shift a drag draws the numbers' too, except while aligning, where Shift turns the image.
        if (_alignRegionArmed && BeginRegion(x, y, forAlign: true)) return;
        if ((_regionArmed || (shift && !_alignMode)) && BeginRegion(x, y)) return;
        _dragDivider = DividerNear(x, y);
        if (_dragDivider >= 0) return;
        int p = PaneAt(x, y);
        if (p < 0) return;
        _dragItem = _panes[p].First;
        if (_alignMode && _dragItem >= 0 && BeginAlignDrag(turn: shift)) return; // and the active image stays as it is
        int item = PaneItem(_panes[p]);
        if (_paneCount > 1 && item >= 0 && _pick.Select(item))
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
        else if (_dragDivider >= 0)
        {
            if (_paneCount == 0) return;
            PaneRect pane = _panes[0].Rect;
            MoveDivider(_dragDivider, _splitRows ? (y - pane.Y) / (float)pane.Height : (x - pane.X) / (float)pane.Width);
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
        if (p < 0 || _panes[p].First < 0 || _canvasWidth <= 0 || DividerNear(x, y) >= 0) return;
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
        if (p >= 0 && _paneCount > 1 && PaneItem(_panes[p]) >= 0 && _pick.Select(PaneItem(_panes[p]))) OnComparePick();
    }
}
