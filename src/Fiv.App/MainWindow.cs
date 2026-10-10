using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
using Fiv.Thumbnails;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Fiv.App;

/// <summary>
/// The viewer window. Start-up runs three things at once: this thread creates the window, one
/// thread creates the Direct3D device (the slowest step), the decode pool reads and decodes the
/// image. The first picture is painted with GDI if the device is not up yet; Direct3D takes over
/// when it is. Nothing is drawn unless something changed: every change invalidates the window,
/// and WM_PAINT, which Windows delivers once the input queue is empty, renders one frame.
///
/// Navigation: the window tells the <see cref="ImagePipeline"/> which image it is at and which
/// neighbours to prepare. The picture on screen changes only when the next one can be drawn, so
/// stepping to a prepared neighbour takes one frame and nothing ever flashes empty.
/// </summary>
internal sealed unsafe partial class MainWindow
{
    const uint WM_APP_PIPELINE = PInvoke.WM_APP + 1;
    const uint WM_APP_DEVICE = PInvoke.WM_APP + 2;
    const uint WM_APP_UI = PInvoke.WM_APP + 4;
    const uint WM_APP_FOLDER = PInvoke.WM_APP + 5;
    const uint WM_APP_FOLDER_CHANGED = PInvoke.WM_APP + 6;
    const uint WM_APP_THUMBS = PInvoke.WM_APP + 7;
    const uint WM_APP_PICKED = PInvoke.WM_APP + 8;

    /// <summary>Sent by tests: show the Open dialog (wParam 1: for a folder), as Ctrl+O would.</summary>
    const uint WM_APP_TEST_OPEN_DIALOG = PInvoke.WM_APP + 21;

    /// <summary>
    /// Sent by tests: the mouse is at this client position (x in the low word of lParam, y in the
    /// high word); wParam 1: and its left button goes down, 2: up; 4 added: with Shift held, 8 added: with Ctrl held
    /// (Ctrl for clicks in the panels only). Posted mouse messages do not do: with the real mouse elsewhere, Windows
    /// follows a WM_MOUSEMOVE with WM_MOUSELEAVE.
    /// </summary>
    const uint WM_APP_TEST_MOUSE = PInvoke.WM_APP + 22;
    bool _testShift, _testControl; // held, as far as a click in a panel is concerned, while a test's button is down

    /// <summary>
    /// Sent by tests: a key with its modifiers stated (wParam: virtual key; lParam: 1 Shift, 2 Ctrl,
    /// 4 the key is released). A posted WM_KEYDOWN takes Shift and Ctrl from the real keyboard,
    /// which whoever is working on the machine may be holding.
    /// </summary>
    const uint WM_APP_TEST_KEY = PInvoke.WM_APP + 23;

    /// <summary>Sent by the benchmark tool: start the frame statistics afresh, so that they cover only what follows.</summary>
    const uint WM_APP_RESET_STATISTICS = PInvoke.WM_APP + 20;
    const nuint ShowTimerId = 1, SettleTimerId = 2, FolderTimerId = 3, NoticeTimerId = 4;
    const uint ShowTimeoutMs = 150;

    /// <summary>Steps closer together than this count as running through the folder.</summary>
    const double FastStepMs = 200;

    /// <summary>After the last step, how long before full-resolution work starts again.</summary>
    const uint SettleMs = 150;

    /// <summary>A change in the folder is taken in this long after the first notification, so that a burst is read once.</summary>
    const uint FolderRefreshMs = 400;

    const double WheelZoomStep = 1.2;

    static MainWindow? s_instance;

    readonly Options _options;
    HWND _hwnd;
    nint _hwndForWorkers; // written once the window exists; read by worker threads to post wake-ups
    int _clientWidth, _clientHeight;
    int _windowX, _windowY, _windowWidth, _windowHeight;
    double _dpiScale = 1;
    bool _visible;
    bool _fullscreen;
    WINDOWPLACEMENT _savedPlacement;
    nint _savedStyle;
    HBRUSH _backgroundBrush;

    GraphicsDevice? _gpu;
    GraphicsDevice? _pendingGpu;
    string? _gpuError;
    ViewRenderer? _renderer;
    bool _presented; // a Direct3D frame has been presented; GDI can no longer draw into the window

    readonly ImagePipeline _pipeline;
    readonly List<ImagePipeline.Want> _wants = [];
    LoadedImage? _target;   // the image the user is at
    LoadedImage? _shown;    // the image on screen: the target, or what was there before it is ready
    int _shownIndex = -1;
    LoadedImage? _autoWindowFor;
    ViewState _view;
    DisplaySettings _display = DisplaySettings.Default;
    bool _keepView;

    // What next/previous walks through: the files given explicitly, or, after a single file
    // was opened, the index of its folder once that has been read in the background.
    readonly List<string> _files = [];
    FolderIndex? _folder;
    string? _folderFor;   // the file whose folder is being, or has been, indexed
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Token, FolderIndex? Index, FileSystemWatcher? Watcher, ThumbnailPack? Pack)> _folderResults = new();
    ThumbnailPack? _pendingPack; // opened with the folder, before there is a GPU to show thumbnails with
    FolderSort _sort = FolderSort.Name;
    bool _sortDescending;
    FileSystemWatcher? _watcher;
    bool _folderRefreshQueued, _folderRefreshRunning, _folderChangedAgain;
    int _fileIndex;

    int _direction = 1;
    bool _fast;
    long _lastStep;
    long _requested;      // when the current target was asked for
    bool _requestShown;   // a frame with the current target has been presented

    bool _panning;
    int _mouseX = -1, _mouseY = -1;

    bool _stampedRead, _stampedPreview, _stampedGdi, _stampedFirstFrame, _stampedFullDecoded, _stampedFullFrame;

    public MainWindow(Options options, Settings settings)
    {
        _options = options;
        _settings = settings;
        _showBrowser = !options.NoPanels && (options.ShowBrowser || settings.ShowBrowser);
        _showFilmstrip = !options.NoPanels && (options.ShowFilmstrip || settings.ShowFilmstrip);
        _browserMode = settings.BrowserList ? BrowserMode.List : BrowserMode.Grid;
        _thumbSize = settings.ThumbnailSize;
        _sort = settings.Sort;
        _sortDescending = settings.SortDescending;
        _keepView = settings.KeepView;
        _display.Sharp = settings.ExactPixels;
        _compareMode = options.CompareMode ?? settings.CompareMode;
        _sizeMatch = settings.CompareSizes;
        _linked = settings.CompareLinked;
        _linkedCursor = settings.CompareCursor;
        _paneLabels = settings.CompareLabels;
        _splitRows = settings.CompareSplitRows;
        _blinkMs = settings.CompareBlinkMs;
        _checkerSize = settings.CompareCheckerSize;
        _differenceKind = settings.DifferenceKind;
        _differenceMap = settings.DifferenceMap;
        _signedMap = settings.SignedMap;
        _threshold = settings.DifferenceThreshold;
        _mask = settings.DifferenceMask;
        _showMetrics = settings.ShowMetrics;
        _pixelValues = settings.PixelValues;
        _alignModel = settings.AlignModel;
        _files.AddRange(options.Files);
        _pipeline = new ImagePipeline(() => Post(WM_APP_PIPELINE),
            (long)(options.CacheMb ?? settings.CacheMb) << 20, (long)(options.VramMb ?? settings.VramMb) << 20);
    }

    bool _startMaximized;
    int _thumbsTrimmed;

    int StatusBarHeight => (int)Math.Round(24 * _dpiScale);

    // The part of the client area the image has: everything above the status bar until the
    // panels are laid out, then what the docking layout leaves in the middle.
    int _viewX, _viewY, _viewWidth, _viewHeight;
    bool _viewFromLayout;
    int ViewX => _viewX;
    int ViewY => _viewY;
    int ViewWidth => Math.Max(1, _viewWidth);
    int ViewHeight => Math.Max(1, _viewHeight);

    /// <summary>
    /// Before the panels exist, the image area is where they will leave it: as saved at the end
    /// of the last run, or as the default layout puts it. The first picture is then fitted and
    /// decoded for the right size, and does not jump when the panels appear.
    /// </summary>
    void UseWholeClientAsView()
    {
        if (_viewFromLayout) return;
        int height = _clientHeight - StatusBarHeight;
        bool saved = _settings.ViewLeft > 0.001 || _settings.ViewTop > 0.001 || _settings.ViewRight < 0.999 || _settings.ViewBottom < 0.95;
        if (!_showBrowser && !_showFilmstrip)
        {
            (_viewX, _viewY, _viewWidth, _viewHeight) = (0, 0, _clientWidth, height);
        }
        else if (saved)
        {
            _viewX = (int)Math.Round(_settings.ViewLeft * _clientWidth);
            _viewY = (int)Math.Round(_settings.ViewTop * _clientHeight);
            _viewWidth = (int)Math.Round(_settings.ViewRight * _clientWidth) - _viewX;
            _viewHeight = (int)Math.Round(_settings.ViewBottom * _clientHeight) - _viewY;
        }
        else
        {
            // The default layout: folder panel over the left 26 %, filmstrip over the bottom 15 %.
            _viewX = _showBrowser ? (int)Math.Round(_clientWidth * 0.26) : 0;
            _viewY = 0;
            _viewWidth = _clientWidth - _viewX;
            _viewHeight = (int)Math.Round(height * (_showFilmstrip ? 0.85 : 1.0));
        }
    }

    /// <summary>The docking layout says where the image goes.</summary>
    void SetViewRect(int x, int y, int width, int height)
    {
        _viewFromLayout = true;
        if (x == _viewX && y == _viewY && width == _viewWidth && height == _viewHeight) return;
        _viewX = x;
        _viewY = y;
        _viewWidth = width;
        _viewHeight = height;
        FitToView();
        Invalidate(); // the image was already drawn for the old rectangle in this frame
    }

    /// <summary>The view changed size: an image that is fitted to it stays fitted. (A comparison fits its panes when it lays them out.)</summary>
    void FitToView()
    {
        if (!Comparing && _view.Fit && _shown is { HasSize: true } image) _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
    }

    int FileCount => _folder?.Count ?? _files.Count;
    string FileAt(int index) => _folder != null ? _folder.PathAt(index) : _files[index];

    public int Run()
    {
        s_instance = this;
        PlanWindow();
        StartDeviceThread();
        if (_files.Count == 1 && Directory.Exists(_files[0]))
        {
            string directory = _files[0];
            _files.Clear();
            Settings.Remember(_settings.RecentFolders, directory);
            StartFolderIndex(directory, directory);
        }
        else if (_files.Count > 0)
        {
            Settings.Remember(_settings.RecentFiles, _files[0]);
            if (System.IO.Path.GetDirectoryName(_files[0]) is { Length: > 0 } folder) Settings.Remember(_settings.RecentFolders, folder);
            if (ComparedWhenOpened(_files.Count)) StartCompare(_files.ConvertAll(file => (file, 0L, 0L)));
            else Request();
        }
        CreateWindow();
        Perf.Stamp("window");

        // Whatever the workers finished before there was a window to notify.
        OnPipeline();
        OnFolderIndexed();
        OnDeviceReady();
        if (_files.Count == 0) EnsureVisible();
        else PInvoke.SetTimer(_hwnd, ShowTimerId, ShowTimeoutMs, null);

        // The file this start was given may be the first of several opened together: the others
        // are handed over by starts that came later, and are waiting for this loop to take them.
        if (_files.Count == 1 && !_options.NoGather) _together.Add(_files[0], Environment.TickCount64);

        MSG message;
        while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
        return 0;
    }

    // ---------------------------------------------------------------- window

    /// <summary>Decides the window's size before it exists, so that the first decode knows its viewport.</summary>
    void PlanWindow()
    {
        uint dpi = PInvoke.GetDpiForSystem();
        _dpiScale = dpi / 96.0;
        RECT frame = default;
        PInvoke.AdjustWindowRectExForDpi(&frame, WINDOW_STYLE.WS_OVERLAPPEDWINDOW, false, WINDOW_EX_STYLE.WS_EX_ACCEPTFILES, dpi);
        int frameWidth = frame.right - frame.left, frameHeight = frame.bottom - frame.top;

        // Where the window was last time, if that place is still on a screen; otherwise on the screen the mouse is on.
        HMONITOR monitor = default;
        RECT saved = new() { left = _settings.WindowX, top = _settings.WindowY, right = _settings.WindowX + _settings.WindowWidth, bottom = _settings.WindowY + _settings.WindowHeight };
        bool restore = _settings.WindowWidth >= 200 && _settings.WindowHeight >= 150;
        if (restore)
        {
            monitor = PInvoke.MonitorFromRect(&saved, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
            restore = !monitor.IsNull;
        }
        if (!restore)
        {
            System.Drawing.Point cursor;
            PInvoke.GetCursorPos(&cursor);
            monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        }
        MONITORINFO info = new() { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(monitor, &info);
        RECT work = info.rcWork;
        int workWidth = work.right - work.left, workHeight = work.bottom - work.top;
        if (restore)
        {
            (_windowX, _windowY, _windowWidth, _windowHeight) = (saved.left, saved.top, saved.right - saved.left, saved.bottom - saved.top);
        }
        else
        {
            _windowWidth = Math.Min(workWidth, (int)(workWidth * 0.85));
            _windowHeight = Math.Min(workHeight, (int)(workHeight * 0.88));
            _windowX = work.left + (workWidth - _windowWidth) / 2;
            _windowY = work.top + (workHeight - _windowHeight) / 2;
        }
        _startMaximized = restore && _settings.Maximized && !_options.NoActivate;
        if (_startMaximized)
        {
            // A maximised window's client area is the work area less the caption.
            _clientWidth = workWidth;
            _clientHeight = workHeight - (frameHeight - frameWidth);
        }
        else
        {
            _clientWidth = _windowWidth - frameWidth;
            _clientHeight = _windowHeight - frameHeight;
        }
        UseWholeClientAsView();
    }

    void CreateWindow()
    {
        var instance = new HINSTANCE(PInvoke.GetModuleHandle(null).Value);
        _backgroundBrush = PInvoke.CreateSolidBrush(new COLORREF(0x001F1C1C));
        fixed (char* className = SingleInstance.WindowClass(_options.Instance))
        fixed (char* title = "FastImageViewer2")
        {
            WNDCLASSEXW windowClass = default;
            windowClass.cbSize = (uint)sizeof(WNDCLASSEXW);
            windowClass.style = WNDCLASS_STYLES.CS_DBLCLKS;
            windowClass.lpfnWndProc = &WndProc;
            windowClass.hInstance = instance;
            windowClass.hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);
            windowClass.lpszClassName = className;
            PInvoke.RegisterClassEx(&windowClass);
            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_ACCEPTFILES, className, title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                _windowX, _windowY, _windowWidth, _windowHeight, default, default, instance, null);
        }
        if (_hwnd.IsNull) throw new InvalidOperationException("The window could not be created.");
        if (!_startMaximized)
        {
            RECT client;
            PInvoke.GetClientRect(_hwnd, &client);
            _clientWidth = client.right;
            _clientHeight = client.bottom;
        }
        _dpiScale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        UseWholeClientAsView();
        Interlocked.Exchange(ref _hwndForWorkers, (nint)_hwnd.Value);
    }

    /// <summary>Wakes the UI thread from a worker. A no-op before the window exists; Run() catches up on those.</summary>
    void Post(uint message)
    {
        nint window = Interlocked.CompareExchange(ref _hwndForWorkers, 0, 0);
        if (window != 0) PInvoke.PostMessage(new HWND((void*)window), message, default, default);
    }

    /// <summary>
    /// Asks for a frame because something changed. Two more follow it: ImGui lays out a window
    /// that appears or changes size over the next frames, and without them it would stay hidden
    /// until the next input.
    /// </summary>
    void Invalidate()
    {
        if (_hwnd.IsNull) return; // before the window exists there is nothing to repaint
        _extraFrames = 2;
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    /// <summary>The window stays hidden until there is a picture to show in it, or the timeout passes.</summary>
    void EnsureVisible()
    {
        if (_visible || _hwnd.IsNull) return;
        _visible = true;
        PInvoke.KillTimer(_hwnd, ShowTimerId);
        PInvoke.ShowWindow(_hwnd, _options.NoActivate ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE
            : _startMaximized ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOW);
    }

    void UpdateTitle()
    {
        if (_hwnd.IsNull) return;
        string title = _shown == null ? "FastImageViewer2"
            : Comparing ? $"{System.IO.Path.GetFileName(_shown.Path)} - comparing {_compare.Count} images - FastImageViewer2"
            : $"{System.IO.Path.GetFileName(_shown.Path)} - FastImageViewer2";
        fixed (char* text = title) PInvoke.SetWindowText(_hwnd, text);
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            WINDOWPLACEMENT placement = new() { length = (uint)sizeof(WINDOWPLACEMENT) };
            PInvoke.GetWindowPlacement(_hwnd, &placement);
            _savedPlacement = placement;
            _savedStyle = PInvoke.GetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            MONITORINFO info = new() { cbSize = (uint)sizeof(MONITORINFO) };
            PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), &info);
            PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, _savedStyle & ~(nint)WINDOW_STYLE.WS_OVERLAPPEDWINDOW);
            RECT r = info.rcMonitor;
            PInvoke.SetWindowPos(_hwnd, default, r.left, r.top, r.right - r.left, r.bottom - r.top,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }
        else
        {
            PInvoke.SetWindowLongPtr(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, _savedStyle);
            WINDOWPLACEMENT placement = _savedPlacement;
            PInvoke.SetWindowPlacement(_hwnd, &placement);
            PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
        }
        _fullscreen = !_fullscreen;
    }

    // ---------------------------------------------------------------- files and navigation

    /// <summary>Two to sixteen files opened together are compared; more are a list to walk through.</summary>
    static bool ComparedWhenOpened(int count) => count is >= 2 and <= MaxCompare;

    /// <summary>
    /// Opens what was dropped, picked, or handed over by another process: a folder, one file
    /// (and with it its folder), or several files, which are compared. With
    /// <paramref name="addToComparison"/> the files join the comparison instead of replacing what is shown.
    /// Without <paramref name="keepFolder"/> the files are a list of their own even if the folder on show has them all.
    /// </summary>
    void Open(IEnumerable<string> files, bool addToComparison = false, bool keepFolder = true)
    {
        var list = files.ToList();
        if (list.Count == 0) return;
        if (list.Count == 1 && Directory.Exists(list[0]))
        {
            OpenFolder(list[0]);
            return;
        }
        if (addToComparison && (Comparing || _shown != null))
        {
            foreach (string file in list) AddToCompare(file);
            return;
        }
        StopCompare(resume: false);
        bool compare = ComparedWhenOpened(list.Count);

        // Files of the folder already indexed: keep the index (and drop a filter that hides one of them).
        if (keepFolder && (list.Count == 1 || compare) && _folderAll is { } all && AllListed(list, all))
        {
            if (_folder == null || !AllListed(list, _folder))
            {
                ClearFilter();
                _folder = _folderAll;
            }
            _files.Clear();
            _files.AddRange(list);
            _fileIndex = _folder.Find(System.IO.Path.GetFileName(list[0].AsSpan()));
        }
        else
        {
            LeaveFolder();
            _files.Clear();
            _files.AddRange(list);
        }
        Settings.Remember(_settings.RecentFiles, list[0]);
        if (System.IO.Path.GetDirectoryName(list[0]) is { Length: > 0 } recentFolder) Settings.Remember(_settings.RecentFolders, recentFolder);
        _direction = 1;
        _fast = false;
        _scrollBrowserToCurrent = _scrollStripToCurrent = true;
        if (compare) StartCompare(list.ConvertAll(file => (file, 0L, 0L)));
        else Request();
        SelectCurrent();

        static bool AllListed(List<string> paths, FolderIndex folder)
        {
            foreach (string path in paths)
                if (!InFolder(path, folder.Directory) || folder.Find(System.IO.Path.GetFileName(path.AsSpan())) < 0) return false;
            return true;
        }
    }

    readonly Fiv.Comparing.OpenedTogether _together = new();
    bool _togetherKeepsFolder;

    /// <summary>
    /// Opens what another start of the viewer handed over. One file replaces what is shown, as
    /// any file opened does; the files that follow it closely were opened with it, as a
    /// selection in Explorer is, and all of them are compared: two side by side, more in the grid.
    /// </summary>
    void OpenForwarded(string[] files)
    {
        if (files.Length != 1 || _options.NoGather)
        {
            _together.Clear();
            Open(files);
            return;
        }
        if (Directory.Exists(files[0]))
        {
            _together.Clear();
            OpenFolder(files[0]);
            return;
        }
        if (!_together.Add(files[0], Environment.TickCount64)) return; // opened twice
        if (_together.Files.Count == 1)
        {
            Open(files);
            // Files of the folder on show stay in it; with any other folder, they are a list of their own,
            // as files named together on a command line are, however far its listing has got meanwhile.
            _togetherKeepsFolder = _folderAll != null;
            return;
        }
        SetCompareMode(ModeForSeveral(_together.Files.Count));
        Open(_together.Files, keepFolder: _togetherKeepsFolder);
    }

    /// <summary>Shows a folder: lists it, then its first image.</summary>
    void OpenFolder(string directory)
    {
        try
        {
            directory = System.IO.Path.GetFullPath(directory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        Settings.Remember(_settings.RecentFolders, directory);
        StopCompare(resume: false);
        LeaveFolder();
        _files.Clear();
        _target = null;
        _wants.Clear();
        _pipeline.Request(default, ViewWidth, ViewHeight);
        _direction = 1;
        _fast = false;
        _scrollBrowserToCurrent = _scrollStripToCurrent = true;
        StartFolderIndex(directory, directory);
        Invalidate();
    }

    /// <summary>Forgets the folder being browsed: its index, watcher, filter and selection.</summary>
    void LeaveFolder()
    {
        StopWatching();
        if (!_hwnd.IsNull) PInvoke.KillTimer(_hwnd, FolderTimerId);
        _folderRefreshQueued = _folderRefreshRunning = _folderChangedAgain = false;
        _folder = null;
        _folderAll = null;
        _folderFor = null;
        _fileIndex = 0;
        _shownIndex = -1; // positions of the old list mean nothing in the new one
        _selection.Clear();
        _selectionAnchor = -1;
        ClearFilter();
    }

    void ClearFilter()
    {
        _filterText = "";
        Array.Clear(_filter);
    }

    void Navigate(int index)
    {
        if (FileCount == 0) return;
        index = Math.Clamp(index, 0, FileCount - 1);
        // Going to an image of the folder ends a comparison, also when it is the image the folder was left at.
        bool leftComparison = Comparing;
        if (leftComparison) StopCompare(resume: false);
        else if (index == _fileIndex) return;
        long now = Stopwatch.GetTimestamp();
        _direction = index >= _fileIndex ? 1 : -1;
        _fast = !leftComparison && Stopwatch.GetElapsedTime(_lastStep, now).TotalMilliseconds < FastStepMs;
        _lastStep = now;
        _fileIndex = index;
        _scrollBrowserToCurrent = _scrollStripToCurrent = true;
        Request();
        SelectCurrent();
        // Full-resolution work is held back while the user runs through the folder.
        PInvoke.SetTimer(_hwnd, SettleTimerId, SettleMs, null);
    }

    /// <summary>The target changed: tell the pipeline, and show it at once if it is ready.</summary>
    void Request()
    {
        _requested = Stopwatch.GetTimestamp();
        _requestShown = false;
        UpdateWanted();
        AdvanceShown();
    }

    /// <summary>
    /// What the pipeline should have ready. Standing still, the neighbours wait for the target,
    /// so that nothing competes with the image the user is looking at:
    ///   stage 0, target has no texture yet: the target alone;
    ///   stage 1, target is on screen: previews of the next image in the direction of travel, the one after, and the previous one;
    ///   stage 2, target is there in full: also the full images of the two direct neighbours.
    /// Running through the folder (<see cref="_fast"/>), previews of the target and of five
    /// neighbours at once, four of them ahead, and no full images at all.
    /// </summary>
    void UpdateWanted()
    {
        if (Comparing)
        {
            RequestCompare(); // the whole set, in full
            return;
        }
        _wants.Clear();
        if (FileCount == 0)
        {
            _target = null;
            return;
        }
        _wantedStage = _fast ? 2 : PrefetchStage();
        bool full = !_fast;
        Want(_fileIndex, 0, full);
        if (!_options.NoPrefetch && (_fast || _wantedStage >= 1))
        {
            bool neighbourFull = full && _wantedStage >= 2;
            Want(_fileIndex + _direction, 1, neighbourFull);
            Want(_fileIndex + 2 * _direction, 2, false);
            Want(_fileIndex - _direction, 3, neighbourFull);
            if (_fast)
            {
                Want(_fileIndex + 3 * _direction, 4, false);
                Want(_fileIndex + 4 * _direction, 5, false);
            }
        }
        _target = _pipeline.Request(CollectionsMarshal.AsSpan(_wants), ViewWidth, ViewHeight);
    }

    int _wantedStage;

    int PrefetchStage()
    {
        if (_fileIndex < 0 || _fileIndex >= FileCount) return 0; // between folders: nothing is asked for
        LoadedImage? target = _pipeline.Find(FileAt(_fileIndex));
        if (target == null) return 0;
        if (target.GpuFull != null || target.Error != null) return 2;
        return target.BestGpu != null ? 1 : 0;
    }

    void Want(int index, int rank, bool full)
    {
        if (index < 0 || index >= FileCount) return;
        _wants.Add(_folder != null
            ? new ImagePipeline.Want(_folder.PathAt(index), _folder.SizeAt(index), _folder.ModifiedAt(index), rank, full)
            : new ImagePipeline.Want(_files[index], 0, 0, rank, full));
    }

    /// <summary>
    /// An image can go on screen when there is something to draw for it: a texture, or, before
    /// Direct3D has taken the window over, 8-bit pixels for GDI. A failed image shows its message.
    /// </summary>
    bool Showable(LoadedImage image) =>
        image.Error != null || image.BestGpu != null || (!_presented && GdiSource(image) != null);

    /// <summary>
    /// Puts the target on screen if it is ready. If not, and the user is stepping through the
    /// folder, shows the image nearest to the target that is ready, never one further back than
    /// what is on screen: with a key held down, every image that gets decoded in time is seen.
    /// </summary>
    void AdvanceShown()
    {
        if (Comparing || _target == null) return; // a comparison decides what it shows in AdvanceCompare
        if (Showable(_target))
        {
            if (_shown != _target) Show(_target, _fileIndex);
            _shownIndex = _fileIndex;
            return;
        }
        if (_shown == null || _shownIndex < 0) return;
        int step = Math.Sign(_fileIndex - _shownIndex);
        if (step == 0) return;
        for (int index = _fileIndex - step, tries = 0; index != _shownIndex && tries < 8 && index >= 0 && index < FileCount; index -= step, tries++)
        {
            if (_pipeline.Find(FileAt(index)) is { } image && Showable(image))
            {
                Show(image, index);
                return;
            }
        }
    }

    void Show(LoadedImage image, int index)
    {
        LoadedImage? previous = _shown;
        _shown = image;
        _shownIndex = index;
        _pipeline.Pinned = image;
        if (previous is { Orphaned: true }) previous.Dispose();
        _imagesShown++;

        if (image.HasSize && !(_keepView && previous != null)) _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
        _display.WindowLow = 0;
        _display.WindowHigh = 1;
        _autoWindowFor = null;
        ApplyAutoWindow();
        UpdateTitle();
        EnsureVisible();
        Invalidate();
    }

    /// <summary>16-bit and float images open with black and white at their smallest and largest value.</summary>
    void ApplyAutoWindow()
    {
        if (_shown == null || _autoWindowFor == _shown || _shown.Full == null) return;
        _autoWindowFor = _shown;
        if (_shown.Layout is PixelLayout.Gray16 or PixelLayout.GrayFloat)
        {
            _display.WindowLow = _shown.ValueRange.Min;
            _display.WindowHigh = _shown.ValueRange.Max;
        }
    }

    /// <summary>The pipeline finished something. UI thread.</summary>
    void OnPipeline()
    {
        if (!_pipeline.Drain(_shown, out bool shownChanged)) return;
        if (Comparing)
        {
            AdvanceCompare(); // any image of the set may be on screen: repaint
            return;
        }
        if (_target is { } target)
        {
            if (target.FileReadAt != 0) Perf.StampOnce(ref _stampedRead, "file read", target.FileReadAt);
            if (target.PreviewDecodedAt != 0) Perf.StampOnce(ref _stampedPreview, "preview decoded", target.PreviewDecodedAt);
            if (target.FullDecodedAt != 0) Perf.StampOnce(ref _stampedFullDecoded, "full decoded", target.FullDecodedAt);
        }
        LoadedImage? before = _shown;
        AdvanceShown(); // repaints if it changes the picture
        ApplyAutoWindow();
        // A frame for nothing would hold up the next real one by a refresh interval, so only
        // repaint when the image on screen is the one that changed (its full version arrived).
        if (_shown == before && shownChanged) Invalidate();
        if (!_fast && !_options.NoPrefetch && _target != null && PrefetchStage() > _wantedStage) UpdateWanted();
        StartFolderIndexIfDue();
    }

    /// <summary>
    /// A file opened on its own gets its folder read once Direct3D has shown it (or it has
    /// failed), not before: at a cold start the first picture and the graphics device come
    /// first, and reading the folder while the device is being created measurably delayed it.
    /// Without a usable GPU, the first GDI paint is the signal.
    /// </summary>
    void StartFolderIndexIfDue()
    {
        if (_files.Count != 1 || _folderAll != null || _folderFor != null || _target == null) return;
        if (_target.FirstFrame == 0 && _target.Error == null && !(_stampedGdi && _gpuError != null)) return;
        if (System.IO.Path.GetDirectoryName(_files[0]) is { Length: > 0 } directory) StartFolderIndex(directory, _files[0]);
    }

    // ---------------------------------------------------------------- folder

    /// <summary>
    /// Reads and sorts a folder on a background thread, and gets what goes with it: the watcher
    /// and the folder's thumbnail pack. <paramref name="token"/> says what the result is for (the
    /// file that was opened, or the folder); a result whose token is no longer current is dropped.
    /// </summary>
    void StartFolderIndex(string directory, string token)
    {
        if (_folderFor != token) _folderRequestedAt = Stopwatch.GetTimestamp(); // a new folder, not a re-read of the current one
        _folderFor = token;
        _folderRefreshRunning = true;
        FolderSort sort = _sort;
        bool descending = _sortDescending;
        bool watch = _watcher == null || !string.Equals(_watcher.Path, directory, StringComparison.OrdinalIgnoreCase);
        ThumbnailPack? currentPack = _thumbs?.Pack ?? _pendingPack;
        string thumbsRoot = _options.ThumbsDir;
        new Thread(() =>
        {
            FolderIndex? index = null;
            FileSystemWatcher? watcher = null;
            ThumbnailPack? opened = null;
            try
            {
                // The watcher first, so that a change between reading and watching is not missed.
                // (Creating one takes some 10 ms the first time, which is why it happens here.)
                if (watch) watcher = CreateWatcher(directory);
                index = FolderIndex.Build(directory, sort, descending);

                // Once per run: keep the thumbnail cache within its size limit, oldest folders first.
                if (Interlocked.Exchange(ref _thumbsTrimmed, 1) == 0) ThumbnailPack.Trim(thumbsRoot, (long)_settings.ThumbnailCacheMb << 20);

                // The pack knows the dimensions of every image it has seen before.
                ThumbnailPack pack = currentPack != null && string.Equals(currentPack.Folder, directory, StringComparison.OrdinalIgnoreCase)
                    ? currentPack
                    : opened = ThumbnailPack.Open(thumbsRoot, directory);
                FolderListing listing = index.Listing;
                for (int i = 0; i < listing.Count; i++)
                {
                    if (!pack.TryGet(listing.Name(i), listing.Size(i), listing.ModifiedFileTime(i), out PackEntry entry) || entry.Width <= 0) continue;
                    bool swap = entry.Orientation.SwapsAxes();
                    listing.SetDimensions(i, swap ? entry.Height : entry.Width, swap ? entry.Width : entry.Height);
                }
            }
            catch (IOException)
            {
                // No index: the file stays the only one to show.
            }
            _folderResults.Enqueue((token, index, watcher, opened));
            Post(WM_APP_FOLDER);
        }) { IsBackground = true, Name = "fiv index" }.Start();
    }

    /// <summary>Takes in a folder index, the first one or a refresh after a change on disk.</summary>
    void OnFolderIndexed()
    {
        while (_folderResults.TryDequeue(out var result))
        {
            // A result for a file or folder the user has since left is ignored.
            if (result.Token != _folderFor)
            {
                result.Watcher?.Dispose();
                result.Pack?.Dispose();
                continue;
            }
            _folderRefreshRunning = false;
            // A file opened on its own that the folder's index does not list (not an image type) stays on its own.
            bool unlisted = result.Index != null && _folderAll == null && _files.Count == 1 &&
                result.Index.Find(System.IO.Path.GetFileName(_files[0].AsSpan())) < 0;
            if (result.Index == null || unlisted)
            {
                result.Watcher?.Dispose();
                result.Pack?.Dispose();
                continue;
            }
            if (result.Watcher != null)
            {
                StopWatching();
                _watcher = result.Watcher;
            }
            if (result.Pack != null)
            {
                if (_thumbs != null)
                {
                    _thumbs.SetPack(result.Pack);
                }
                else
                {
                    _pendingPack?.Dispose();
                    _pendingPack = result.Pack;
                }
            }
            if (_folderAll == null)
            {
                _folderListedAt = Stopwatch.GetTimestamp();
                _thumbScreenLogged = false;
            }
            _folderAll = result.Index;
            ApplyView();
        }
        if (!_folderRefreshRunning && _folderChangedAgain)
        {
            _folderChangedAgain = false;
            OnFolderChanged();
        }
    }

    /// <summary>
    /// Makes a view of the folder (newly read, re-sorted or filtered) the list that the panels
    /// show and next/previous walk. The image on show stays if the view has it.
    /// </summary>
    void AdoptFolder(FolderIndex index, bool firstIfGone = false)
    {
        if (Comparing)
        {
            // The set stays on show; the new list is what next and previous walk once it is left.
            _folder = index;
            _fileIndex = Math.Clamp(_fileIndex, 0, Math.Max(0, index.Count - 1));
            _followName = null;
            RebindCompare();
            return;
        }
        string? current = FileCount > 0 ? FileAt(Math.Clamp(_fileIndex, 0, FileCount - 1)) : null;
        int position = current == null ? -1 : index.Find(System.IO.Path.GetFileName(current.AsSpan()));
        if (_followName != null)
        {
            // The image on show was just renamed: it is found under its new name.
            int renamed = index.Find(_followName);
            if (renamed >= 0)
            {
                _fileIndex = renamed;
                _followName = null;
                position = -1; // a new path: it has to be requested
                firstIfGone = false;
                _folder = index;
                _shownIndex = -1;
                Request();
                SelectCurrent();
                Invalidate();
                return;
            }
        }
        if (index.Count == 0)
        {
            // Everything was deleted, or nothing matches the filter.
            _folder = index;
            _target = null;
            _wants.Clear();
            _pipeline.Request(default, ViewWidth, ViewHeight);
            LoadedImage? last = _shown;
            _shown = null;
            _shownIndex = -1;
            _pipeline.Pinned = null;
            if (last is { Orphaned: true }) last.Dispose();
            UpdateTitle();
            Invalidate();
            return;
        }
        bool sameOrder = _folder != null && _folder.Sort == index.Sort && _folder.Descending == index.Descending;
        _folder = index;
        // The current file is gone (or filtered out): its former place in the list is the nearest thing to it.
        _fileIndex = position >= 0 ? position : firstIfGone ? 0 : Math.Clamp(_fileIndex, 0, index.Count - 1);
        _shownIndex = _shown != null ? index.Find(System.IO.Path.GetFileName(_shown.Path.AsSpan())) : -1;
        if (!sameOrder) _direction = 1;
        if (position < 0) Request();
        else UpdateWanted(); // same image, new neighbours; a changed file is reloaded because its size or time differs
        AdvanceShown();
        SelectCurrent();
        Invalidate();
    }

    /// <summary>A watcher that wakes the UI thread on any change in the folder. Null if the folder cannot be watched. Any thread.</summary>
    FileSystemWatcher? CreateWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += (_, _) => Post(WM_APP_FOLDER_CHANGED);
            watcher.Deleted += (_, _) => Post(WM_APP_FOLDER_CHANGED);
            watcher.Renamed += (_, _) => Post(WM_APP_FOLDER_CHANGED);
            watcher.Changed += (_, _) => Post(WM_APP_FOLDER_CHANGED);
            watcher.Error += (_, _) => Post(WM_APP_FOLDER_CHANGED); // notifications were lost: read the folder again
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A folder that cannot be watched is still browsable; it just does not update by itself.
            return null;
        }
    }

    void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>Something in the folder changed. Notifications come in bursts, so the folder is re-read a little later, once.</summary>
    void OnFolderChanged()
    {
        if (_folderAll == null) return;
        if (_folderRefreshRunning)
        {
            _folderChangedAgain = true;
            return;
        }
        if (_folderRefreshQueued) return;
        _folderRefreshQueued = true;
        PInvoke.SetTimer(_hwnd, FolderTimerId, FolderRefreshMs, null);
    }

    void RefreshFolder()
    {
        PInvoke.KillTimer(_hwnd, FolderTimerId);
        _folderRefreshQueued = false;
        if (_folderAll == null) return;
        StartFolderIndex(_folderAll.Directory, _folderFor ?? _folderAll.Directory);
    }

    void SetSort(FolderSort sort, bool descending)
    {
        _sort = sort;
        _sortDescending = descending;
        _syncTableSort = true;
        if (_folderAll == null) return;
        _folderAll = _folderAll.Sorted(sort, descending);
        ApplyView();
    }

    // ---------------------------------------------------------------- GPU

    void StartDeviceThread()
    {
        new Thread(() =>
        {
            GraphicsDevice gpu;
            try
            {
                if (_options.DelayGpuMs > 0) Thread.Sleep(_options.DelayGpuMs);
                gpu = GraphicsDevice.Create();
                Perf.Stamp("device");
                Interlocked.Exchange(ref _pendingGpu, gpu);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _gpuError, e.Message);
                return;
            }
            finally
            {
                Post(WM_APP_DEVICE);
            }

            // The picture does not wait for the UI: this thread prepares it while the UI thread
            // is already presenting frames.
            try
            {
                var ui = new Ui(gpu, _dpiScale, System.IO.Path.Combine(_options.ProfileDir, "layout.ini"));
                Perf.Stamp("ui prepared");
                Interlocked.Exchange(ref _pendingUi, ui);
                Post(WM_APP_UI);
            }
            catch (Exception e)
            {
                CrashLog.Write(e); // the viewer works without its overlays
            }
        }) { IsBackground = true, Name = "fiv device" }.Start();
    }

    void OnUiReady()
    {
        Ui? ui = Interlocked.Exchange(ref _pendingUi, null);
        if (ui == null || _ui != null) return;
        ui.AttachWindow(_hwnd);
        _ui = ui;
        Invalidate();
    }

    void OnDeviceReady()
    {
        GraphicsDevice? gpu = Interlocked.Exchange(ref _pendingGpu, null);
        if (gpu == null || _gpu != null) return; // not ready yet, or failed: GDI keeps painting
        _gpu = gpu;
        _gpu.AttachWindow(_hwnd, _clientWidth, _clientHeight);
        _renderer = new ViewRenderer(_gpu);
        Perf.Stamp("swap chain");
        _pipeline.SetDevice(_gpu); // uploads of what is already decoded start now
        Invalidate();
    }

    // ---------------------------------------------------------------- painting

    void OnPaint()
    {
        // Until Direct3D has something to show, a picture GDI can paint must not be replaced by an empty frame.
        // (A comparison is laid out in panes, which GDI does not draw: it waits for Direct3D.)
        bool useGpu = _renderer != null && (_presented || Comparing || _shown?.BestGpu != null || GdiSource(_shown) == null);
        if (useGpu)
        {
            // Validate first: rendering may invalidate again to ask for a follow-up frame.
            PInvoke.ValidateRect(_hwnd, (RECT*)null);
            RenderFrame();
        }
        else
        {
            PaintWithGdi();
        }
    }

    Affine2 ImageToScreen(LoadedImage image, in ViewState view) =>
        Affine2.ScaleTranslate(1, 1, ViewX, ViewY).After(view.ToScreen(ViewWidth, ViewHeight)).After(image.Orientation.ToUpright(image.Width, image.Height));

    void RenderFrame()
    {
        GraphicsDevice gpu = _gpu!;
        long started = Stopwatch.GetTimestamp();
        gpu.BeginFrame(0.11f, 0.11f, 0.12f);

        bool comparing = Comparing;
        LoadedImage? drawn = !comparing && _shown?.BestGpu != null ? _shown : null;
        if (comparing) RenderCompare();
        else if (drawn != null) _renderer!.Draw(drawn.BestGpu!, ImageToScreen(drawn, _view), _view.Zoom, _display, ViewX, ViewY, ViewWidth, ViewHeight);
        if (_captureView) CaptureView(); // the picture alone: before anything is drawn over it
        if (_thumbs != null) _thumbsUploaded += _thumbs.Upload();
        if (_ui != null)
        {
            _ui.NewFrame();
            DrawUi();
            _ui.Render();
        }
        _lastFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        gpu.Present(waitForVerticalBlank: true);
        _presented = true;
        long presented = Stopwatch.GetTimestamp();
        if (comparing && Comparing) AfterCompareFrame(presented);
        CountFrame(started, presented);
        if (drawn != null)
        {
            Perf.StampOnce(ref _stampedFirstFrame, "first frame (D3D)", presented);
            if (drawn.FirstFrame == 0) drawn.FirstFrame = presented;
            if (drawn == _target && !_requestShown)
            {
                _requestShown = true;
                _requestFirstFrame = presented;
                double ms = Stopwatch.GetElapsedTime(_requested, presented).TotalMilliseconds;
                _steps++;
                _stepMs += ms;
                _stepMaxMs = Math.Max(_stepMaxMs, ms);
                _lastStepMs = ms;
                LogStep(drawn, ms);
            }
            if (drawn.GpuFull != null && drawn.FullFrame == 0)
            {
                drawn.FullFrame = presented;
                if (drawn == _target)
                {
                    Perf.StampOnce(ref _stampedFullFrame, "full frame", presented);
                    Loaded(drawn);
                }
            }
        }

        if (_extraFrames > 0)
        {
            _extraFrames--;
            PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
        }
        if (drawn != null)
        {
            if (!_fast && !_options.NoPrefetch && PrefetchStage() > _wantedStage) UpdateWanted();
            StartFolderIndexIfDue();
        }
        if (_deferred is { } action)
        {
            // What a menu asked for that could not happen while the frame was being drawn.
            _deferred = null;
            action();
            Invalidate();
        }
    }

    // ---------------------------------------------------------------- measurements

    long _folderRequestedAt, _folderListedAt;
    bool _thumbScreenLogged;
    int _thumbsUploaded;
    double _thumbScreenMs = -1;
    double _lastFrameMs, _lastStepMs;
    int _frames, _intervals, _imagesShown, _steps;
    double _frameCpuMs, _frameMaxCpuMs, _frameMaxMs, _intervalMs, _intervalMaxMs, _stepMs, _stepMaxMs;
    long _lastPresented, _requestFirstFrame;
    bool _perfOutWritten;

    /// <summary>Frame statistics of the steady state, for --perf-log: loading is over, this is pan and zoom.</summary>
    void CountFrame(long started, long presented)
    {
        if (_shown?.FullFrame > 0)
        {
            double frameMs = Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds;
            _frames++;
            _frameCpuMs += _lastFrameMs;
            _frameMaxCpuMs = Math.Max(_frameMaxCpuMs, _lastFrameMs);
            _frameMaxMs = Math.Max(_frameMaxMs, frameMs);
            if (_lastPresented != 0)
            {
                double interval = Stopwatch.GetElapsedTime(_lastPresented, presented).TotalMilliseconds;
                if (interval < 100) // consecutive frames of one interaction, not a pause between two
                {
                    _intervals++;
                    _intervalMs += interval;
                    _intervalMaxMs = Math.Max(_intervalMaxMs, interval);
                    if (interval > 30) LogLongInterval(interval, frameMs);
                }
            }
        }
        _lastPresented = presented;
    }

    /// <summary>
    /// A frame that came long after the one before it, in the middle of an interaction: where the
    /// time went, for --perf-log. "frame" is from the start of drawing to Present returning; what
    /// is left of the interval passed before drawing began.
    /// </summary>
    void LogLongInterval(double interval, double frameMs)
    {
        if (_options.PerfLog == null) return;
        try
        {
            File.AppendAllText(_options.PerfLog, $"long_interval ms={interval:F1} cpu_ms={_lastFrameMs:F2} frame_ms={frameMs:F1} zoom={_view.Zoom:F2}\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>One line per image the user went to, for the navigation benchmark.</summary>
    void LogStep(LoadedImage image, double ms)
    {
        if (_options.PerfLog == null) return;
        try
        {
            File.AppendAllText(_options.PerfLog, $"step_ms={ms:F2} full={(image.GpuFull != null ? 1 : 0)} file={System.IO.Path.GetFileName(image.Path)}\n");
        }
        catch (IOException)
        {
        }
    }

    void WriteStatistics()
    {
        if (_options.PerfLog == null) return;
        try
        {
            File.AppendAllText(_options.PerfLog,
                $"images_shown={_imagesShown} last_index={_fileIndex + 1}/{FileCount} thumbs_uploaded={_thumbsUploaded} " +
                $"steps={_steps} step_mean_ms={_stepMs / Math.Max(_steps, 1):F2} step_max_ms={_stepMaxMs:F2} " +
                $"frames={_frames} cpu_mean_ms={_frameCpuMs / Math.Max(_frames, 1):F3} cpu_max_ms={_frameMaxCpuMs:F3} frame_max_ms={_frameMaxMs:F2} " +
                $"intervals={_intervals} interval_mean_ms={(_intervals > 0 ? _intervalMs / _intervals : 0):F2} interval_max_ms={_intervalMaxMs:F2}\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The image asked for is completely on screen: report and, in benchmark runs, leave.</summary>
    void Loaded(LoadedImage image)
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
                    $"qpf={Stopwatch.Frequency} requested={_requested} first_frame={_requestFirstFrame} full_frame={image.FullFrame} " +
                    $"file_read={image.FileReadAt} preview_decoded={image.PreviewDecodedAt} full_decoded={image.FullDecodedAt} " +
                    $"read_ms={image.ReadMs:F1} preview_ms={image.PreviewDecodeMs:F1} full_ms={image.FullDecodeMs:F1} mip_ms={image.MipMs:F1} upload_ms={image.UploadMs:F1} " +
                    $"file={System.IO.Path.GetFileName(image.Path)}\n");
            }
        }
        catch (IOException)
        {
        }
        if (_options.ExitWhenLoaded) PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
    }

    // ---------------------------------------------------------------- GDI

    /// <summary>The decoded pixels GDI can paint: 8-bit colour only, the smaller version if there are two.</summary>
    static DecodedImage? GdiSource(LoadedImage? image)
    {
        DecodedImage? source = image?.Preview ?? image?.Full;
        return source is { Layout: PixelLayout.Bgra8 } ? source : null;
    }

    void PaintWithGdi()
    {
        PAINTSTRUCT paint;
        HDC dc = PInvoke.BeginPaint(_hwnd, &paint);
        RECT client = new() { right = _clientWidth, bottom = _clientHeight };
        DecodedImage? source = Comparing ? null : GdiSource(_shown);
        if (source == null)
        {
            PInvoke.FillRect(dc, &client, _backgroundBrush);
            PInvoke.EndPaint(_hwnd, &paint);
            return;
        }

        LoadedImage image = _shown!;
        // Texel coordinates of the decoded pixels -> screen.
        Affine2 texelToImage = Affine2.ScaleTranslate((double)image.Width / source.Width, (double)image.Height / source.Height, 0, 0);
        Affine2 m = ImageToScreen(image, _view).After(texelToImage);

        BITMAPINFO bitmap = default;
        bitmap.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bitmap.bmiHeader.biWidth = source.Width;
        bitmap.bmiHeader.biHeight = -source.Height; // top-down
        bitmap.bmiHeader.biPlanes = 1;
        bitmap.bmiHeader.biBitCount = 32;

        (double x0, double y0) = m.Apply(0, 0);
        (double x1, double y1) = m.Apply(source.Width, source.Height);
        int left = (int)Math.Round(Math.Min(x0, x1)), right = (int)Math.Round(Math.Max(x0, x1));
        int top = (int)Math.Round(Math.Min(y0, y1)), bottom = (int)Math.Round(Math.Max(y0, y1));

        // Background around the picture only, so that nothing flashes underneath it.
        RECT band = new() { right = _clientWidth, bottom = Math.Max(0, top) };
        PInvoke.FillRect(dc, &band, _backgroundBrush);
        band = new RECT { top = Math.Min(_clientHeight, bottom), right = _clientWidth, bottom = _clientHeight };
        PInvoke.FillRect(dc, &band, _backgroundBrush);
        band = new RECT { top = top, right = Math.Max(0, left), bottom = bottom };
        PInvoke.FillRect(dc, &band, _backgroundBrush);
        band = new RECT { left = Math.Min(_clientWidth, right), top = top, right = _clientWidth, bottom = bottom };
        PInvoke.FillRect(dc, &band, _backgroundBrush);

        // COLORONCOLOR drops pixels instead of averaging them. HALFTONE would look as good as the
        // GPU's filtering but took 45 ms for this one frame, which Direct3D replaces within ~150 ms.
        if (image.Orientation == Orientation.Normal)
        {
            PInvoke.SetStretchBltMode(dc, STRETCH_BLT_MODE.COLORONCOLOR);
            PInvoke.StretchDIBits(dc, left, top, right - left, bottom - top, 0, 0, source.Width, source.Height,
                source.Pixels, &bitmap, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
        }
        else
        {
            // Rotated or mirrored: GDI's world transform does it, without smoothing.
            PInvoke.SetGraphicsMode(dc, GRAPHICS_MODE.GM_ADVANCED);
            XFORM transform = new() { eM11 = (float)m.A, eM12 = (float)m.B, eM21 = (float)m.C, eM22 = (float)m.D, eDx = (float)m.Tx, eDy = (float)m.Ty };
            PInvoke.SetWorldTransform(dc, &transform);
            PInvoke.SetStretchBltMode(dc, STRETCH_BLT_MODE.COLORONCOLOR);
            PInvoke.StretchDIBits(dc, 0, 0, source.Width, source.Height, 0, 0, source.Width, source.Height,
                source.Pixels, &bitmap, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
            PInvoke.ModifyWorldTransform(dc, null, MODIFY_WORLD_TRANSFORM_MODE.MWT_IDENTITY);
        }
        PInvoke.EndPaint(_hwnd, &paint);
        Perf.StampOnce(ref _stampedGdi, "first pixels (GDI)");
        StartFolderIndexIfDue();
    }

    // ---------------------------------------------------------------- input

    void OnSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return; // minimised
        _clientWidth = width;
        _clientHeight = height;
        UseWholeClientAsView();
        _gpu?.Resize(width, height);
        FitToView();
        Invalidate();
    }

    void OnMouseWheel(int delta, int screenX, int screenY, bool shift, bool control)
    {
        var point = new System.Drawing.Point(screenX, screenY);
        PInvoke.ScreenToClient(_hwnd, &point);
        double factor = Math.Pow(WheelZoomStep, delta / (double)PInvoke.WHEEL_DELTA);
        if (Comparing && AlignWheel(delta, control, shift)) { } // while aligning, the wheel with Ctrl or Shift turns or scales the image
        else if (Comparing) CompareWheel(factor, point.X, point.Y);
        else if (_shown is { HasSize: true }) _view.ZoomAt(_view.Zoom * factor, point.X - ViewX, point.Y - ViewY, ViewWidth, ViewHeight);
        else return;
        Invalidate();
    }

    void OnMouseMove(int x, int y)
    {
        if (_panning)
        {
            if (Comparing) CompareDrag(x, y, x - _mouseX, y - _mouseY);
            else if (_shown is { HasSize: true } image) _view.Pan(x - _mouseX, y - _mouseY, image.UprightWidth, image.UprightHeight);
        }
        _mouseX = x;
        _mouseY = y;
        Invalidate(); // the readout under the cursor changed
    }

    /// <summary>
    /// The left button went down over the image: a drag pans it. When comparing it may instead
    /// move the divider, or, with Shift held or after R, draw the region the numbers are taken over.
    /// </summary>
    void OnLeftDown(int x, int y, bool shift)
    {
        _panning = true;
        _mouseX = x;
        _mouseY = y;
        PInvoke.SetCapture(_hwnd);
        if (!Comparing) return;
        CompareMouseDown(x, y, shift);
        Invalidate();
    }

    void OnLeftUp()
    {
        _panning = false;
        _dragDivider = false;
        _dragAlign = false;
        EndRegion();
        _dragItem = -1;
        PInvoke.ReleaseCapture();
    }

    void OnKeyUp(VIRTUAL_KEY key)
    {
        if (!Comparing) return;
        CompareKeyUp(key);
        Invalidate();
    }

    /// <summary>Zooms at the centre of the view (of the pane, when comparing), to a factor of the image's size or by a factor of the current zoom.</summary>
    void ZoomAtCentre(double value, bool absolute)
    {
        if (Comparing) CompareZoom(value, absolute);
        else if (_shown is { HasSize: true }) _view.ZoomAt(absolute ? value : _view.Zoom * value, ViewWidth / 2.0, ViewHeight / 2.0, ViewWidth, ViewHeight);
    }

    /// <summary>Shows the whole image, or all of every compared image, and keeps it so when the window changes size.</summary>
    void FitView()
    {
        if (Comparing)
        {
            _view.Fit = true;
            foreach (CompareItem item in _compare) item.View.Fit = true; // laid out, and thereby fitted, before the next frame
        }
        else if (_shown is { HasSize: true } image)
        {
            _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
        }
    }

    void OnKeyDown(VIRTUAL_KEY key, bool repeat, bool shift, bool control)
    {
        if (_dialog != Dialog.None)
        {
            // A dialog is open: Esc closes it, Enter confirms the one without a text field.
            if (key == VIRTUAL_KEY.VK_ESCAPE) _dialog = Dialog.None;
            else if (key == VIRTUAL_KEY.VK_RETURN && _dialog == Dialog.Delete) CommitDelete();
            Invalidate();
            return;
        }
        if (Comparing && CompareKey(key, shift, control, repeat))
        {
            Invalidate();
            return;
        }
        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                if (_showKeys) _showKeys = false;
                else if (_showSettings) _showSettings = false;
                else if (_showFileTypes) _showFileTypes = false;
                else if (_fullscreen) ToggleFullscreen();
                else if (Comparing) StopCompare();
                else PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
                break;
            case VIRTUAL_KEY.VK_F1:
                _showKeys = !_showKeys;
                break;
            case VIRTUAL_KEY.VK_F2:
                BeginRename();
                break;
            case VIRTUAL_KEY.VK_DELETE:
                BeginDelete();
                break;
            case VIRTUAL_KEY.VK_O when control:
                OpenWithDialog(folder: shift);
                break;
            case VIRTUAL_KEY.VK_C when control:
                if (shift) CopyPath();
                else if (Comparing) CopyView(); // a comparison is what its panes show; one image of it is copied through the menu
                else CopyImage();
                break;
            case VIRTUAL_KEY.VK_S when control:
                if (shift) SaveMetrics();
                else SaveView();
                break;
            case VIRTUAL_KEY.VK_D when control:
                SaveDifferenceValues();
                break;
            case VIRTUAL_KEY.VK_E when control:
                SaveAligned();
                break;
            case VIRTUAL_KEY.VK_V:
                _pixelValues = !_pixelValues;
                Notice(_pixelValues ? "Pixel values are written into the pixels once they are large enough: zoom in." : "Pixel values off.");
                break;
            case VIRTUAL_KEY.VK_C:
                ToggleCompare();
                break;
            case VIRTUAL_KEY.VK_H:
            {
                // Colour, red, green, blue, alpha, luma in turn; with Shift the other way round.
                const int channels = (int)ChannelView.Luma + 1;
                _display.Channel = (ChannelView)(((int)_display.Channel + (shift ? channels - 1 : 1)) % channels);
                break;
            }
            case VIRTUAL_KEY.VK_F11:
                ToggleFullscreen();
                break;
            case VIRTUAL_KEY.VK_F12:
                _showPerf = !_showPerf;
                break;
            case VIRTUAL_KEY.VK_D:
                _showDisplayPanel = !_showDisplayPanel;
                break;
            case VIRTUAL_KEY.VK_F or VIRTUAL_KEY.VK_0 or VIRTUAL_KEY.VK_NUMPAD0:
                FitView();
                break;
            case VIRTUAL_KEY.VK_1 or VIRTUAL_KEY.VK_NUMPAD1 or VIRTUAL_KEY.VK_Z: // while comparing, 1 to 9 are the images, and Z is 100 %
                ZoomAtCentre(1, absolute: true);
                break;
            case VIRTUAL_KEY.VK_OEM_PLUS or VIRTUAL_KEY.VK_ADD:
                ZoomAtCentre(WheelZoomStep, absolute: false);
                break;
            case VIRTUAL_KEY.VK_OEM_MINUS or VIRTUAL_KEY.VK_SUBTRACT:
                ZoomAtCentre(1 / WheelZoomStep, absolute: false);
                break;
            case VIRTUAL_KEY.VK_I:
                if (control) SelectAll(invert: true);
                else _display.Sharp = !_display.Sharp;
                break;
            case VIRTUAL_KEY.VK_L:
                _keepView = !_keepView;
                break;
            case VIRTUAL_KEY.VK_S:
                // S: name, date, size, type in turn. Shift+S: reverse the current order.
                if (shift) SetSort(_sort, !_sortDescending);
                else SetSort((FolderSort)(((int)_sort + 1) % 4), _sortDescending);
                break;
            case VIRTUAL_KEY.VK_B:
                _showBrowser = !_showBrowser;
                _scrollBrowserToCurrent = true;
                break;
            case VIRTUAL_KEY.VK_T:
                _showFilmstrip = !_showFilmstrip;
                _scrollStripToCurrent = true;
                break;
            case VIRTUAL_KEY.VK_TAB:
            {
                // Both panels away, or back as they were.
                if (_showBrowser || _showFilmstrip)
                {
                    _panelsBeforeHide = (_showBrowser, _showFilmstrip);
                    _showBrowser = _showFilmstrip = false;
                }
                else
                {
                    (_showBrowser, _showFilmstrip) = _panelsBeforeHide is (false, false) ? (true, false) : _panelsBeforeHide;
                    _scrollBrowserToCurrent = _scrollStripToCurrent = true;
                }
                break;
            }
            case VIRTUAL_KEY.VK_UP when _showBrowser && _browserMode == BrowserMode.Grid:
                Navigate(_fileIndex - _gridColumns);
                break;
            case VIRTUAL_KEY.VK_DOWN when _showBrowser && _browserMode == BrowserMode.Grid:
                Navigate(_fileIndex + _gridColumns);
                break;
            case VIRTUAL_KEY.VK_A when control:
                SelectAll(invert: false);
                break;

            case VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_NEXT or VIRTUAL_KEY.VK_SPACE:
                Navigate(_fileIndex + 1);
                break;
            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_BACK:
                Navigate(_fileIndex - 1);
                break;
            case VIRTUAL_KEY.VK_HOME:
                Navigate(0);
                break;
            case VIRTUAL_KEY.VK_END:
                Navigate(FileCount - 1);
                break;
            default:
                return;
        }
        Invalidate();
    }

    void OnDropFiles(HDROP drop)
    {
        uint count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        char* buffer = stackalloc char[1024];
        for (uint i = 0; i < count; i++)
        {
            uint length = PInvoke.DragQueryFile(drop, i, buffer, 1024);
            if (length > 0) files.Add(new string(buffer, 0, (int)length));
        }
        PInvoke.DragFinish(drop);
        // With Ctrl held, what is dropped joins the comparison. The key's state is asked for
        // directly: during a drag from another program this window receives no keyboard input.
        Open(files, addToComparison: PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) < 0);
    }

    void BringToFront()
    {
        if (PInvoke.IsIconic(_hwnd)) PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        EnsureVisible();
        if (!_options.NoActivate) PInvoke.SetForegroundWindow(_hwnd);
    }

    // ---------------------------------------------------------------- messages

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        MainWindow? window = s_instance;
        if (window == null || window._hwnd.IsNull) return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        try
        {
            return window.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception e)
        {
            // An exception must not unwind into the window manager.
            CrashLog.Write(e);
            PInvoke.PostQuitMessage(1);
            return default;
        }
    }

    static int LowWord(nint value) => (short)(value & 0xFFFF);
    static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);

    static bool IsMouseMessage(uint message) => message is >= PInvoke.WM_MOUSEFIRST and <= PInvoke.WM_MOUSELAST;
    static bool IsKeyMessage(uint message) => message is >= PInvoke.WM_KEYFIRST and <= PInvoke.WM_KEYLAST;

    LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (_ui != null)
        {
            if (_ui.HandleMessage(hwnd, message, wParam, lParam)) return new LRESULT(1);
            bool mouse = IsMouseMessage(message), key = IsKeyMessage(message);
            if (mouse || key || message == PInvoke.WM_MOUSELEAVE) Invalidate();
            // Input over a panel, or typed into one, belongs to the panel. A drag that started on
            // the image keeps going wherever the mouse is.
            if (mouse && _ui.WantsMouse && !_panning)
            {
                _mouseX = _mouseY = -1; // the cursor is not over the image: no readout, no linked cursor
                return default;
            }
            if (key && _ui.WantsKeyboard) return default;
        }

        switch (message)
        {
            case PInvoke.WM_MOUSELEAVE:
                _mouseX = _mouseY = -1;
                return default;

            case WM_APP_PIPELINE:
                OnPipeline();
                return default;
            case WM_APP_DEVICE:
                OnDeviceReady();
                return default;
            case WM_APP_UI:
                OnUiReady();
                return default;
            case WM_APP_FOLDER:
                OnFolderIndexed();
                return default;
            case WM_APP_FOLDER_CHANGED:
                OnFolderChanged();
                return default;
            case WM_APP_THUMBS:
                Invalidate(); // the frame takes the finished thumbnails in
                return default;
            case WM_APP_PICKED:
                OnPicked();
                return default;
            case WM_APP_RUN:
                OnUiQueue();
                return default;
            case WM_APP_TEST_OPEN_DIALOG:
                OpenWithDialog(folder: wParam.Value != 0);
                return default;
            case WM_APP_TEST_MOUSE:
            {
                // Routed as the real messages are: to a panel if the mouse is over one, else to the image.
                int x = LowWord(lParam.Value), y = HighWord(lParam.Value), button = (int)wParam.Value & 3;
                if (button == 1) (_testShift, _testControl) = (((int)wParam.Value & 4) != 0, ((int)wParam.Value & 8) != 0);
                else if (button == 2) _testShift = _testControl = false;
                _ui?.TestMouse(x, y, button);
                if (_ui is { WantsMouse: true } && !_panning) _mouseX = _mouseY = -1;
                else if (button == 1) OnLeftDown(x, y, shift: ((int)wParam.Value & 4) != 0);
                else if (button == 2) OnLeftUp();
                else OnMouseMove(x, y);
                Invalidate();
                return default;
            }
            case WM_APP_RESET_STATISTICS:
                _frames = _intervals = _thumbsUploaded = 0;
                _frameCpuMs = _frameMaxCpuMs = _frameMaxMs = _intervalMs = _intervalMaxMs = 0;
                _lastPresented = 0;
                return default;

            case PInvoke.WM_PAINT:
                OnPaint();
                return default;
            case PInvoke.WM_ERASEBKGND:
                return new LRESULT(1);
            case PInvoke.WM_SIZE:
                OnSize(LowWord(lParam.Value) & 0xFFFF, HighWord(lParam.Value) & 0xFFFF);
                return default;
            case PInvoke.WM_DPICHANGED:
            {
                _dpiScale = (wParam.Value & 0xFFFF) / 96.0;
                RECT* suggested = (RECT*)lParam.Value;
                PInvoke.SetWindowPos(hwnd, default, suggested->left, suggested->top, suggested->right - suggested->left,
                    suggested->bottom - suggested->top, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                return default;
            }
            case PInvoke.WM_TIMER:
                switch (wParam.Value)
                {
                    case ShowTimerId:
                        EnsureVisible();
                        OnPipeline();
                        OnDeviceReady();
                        OnUiReady();
                        break;
                    case SettleTimerId:
                        // The user stopped running through the folder: full resolution may be worked on again.
                        PInvoke.KillTimer(hwnd, SettleTimerId);
                        if (_fast)
                        {
                            _fast = false;
                            UpdateWanted();
                        }
                        break;
                    case FolderTimerId:
                        RefreshFolder();
                        break;
                    case NoticeTimerId:
                        PInvoke.KillTimer(hwnd, NoticeTimerId);
                        Invalidate(); // the notice has run out
                        break;
                    case BlinkTimerId:
                        OnBlinkTimer();
                        break;
                }
                return default;

            case PInvoke.WM_MOUSEWHEEL:
                OnMouseWheel(HighWord((nint)wParam.Value), LowWord(lParam.Value), HighWord(lParam.Value),
                    shift: (wParam.Value & 0x0004) != 0, control: (wParam.Value & 0x0008) != 0); // MK_SHIFT, MK_CONTROL
                return default;
            case PInvoke.WM_MOUSEMOVE:
                OnMouseMove(LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_LBUTTONDOWN:
                OnLeftDown(LowWord(lParam.Value), HighWord(lParam.Value), shift: (wParam.Value & 0x0004) != 0); // MK_SHIFT
                return default;
            case PInvoke.WM_LBUTTONUP:
                OnLeftUp();
                return default;
            case PInvoke.WM_LBUTTONDBLCLK:
                if (Comparing)
                {
                    CompareDoubleClick(LowWord(lParam.Value), HighWord(lParam.Value));
                    Invalidate();
                }
                else if (_shown is { HasSize: true } image)
                {
                    if (_view.Fit || _view.Zoom != 1) _view.ZoomAt(1, LowWord(lParam.Value) - ViewX, HighWord(lParam.Value) - ViewY, ViewWidth, ViewHeight);
                    else _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
                    Invalidate();
                }
                return default;
            case PInvoke.WM_RBUTTONUP:
                if (Comparing) CompareRightClick(LowWord(lParam.Value), HighWord(lParam.Value));
                _viewMenuRequested = true; // a right click on the image
                Invalidate();
                return default;
            case PInvoke.WM_KEYDOWN:
                OnKeyDown((VIRTUAL_KEY)(ushort)wParam.Value, repeat: (lParam.Value & (1 << 30)) != 0,
                    shift: PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0, control: PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) < 0);
                return default;
            case WM_APP_TEST_KEY:
                if ((lParam.Value & 4) != 0) OnKeyUp((VIRTUAL_KEY)(ushort)wParam.Value);
                else OnKeyDown((VIRTUAL_KEY)(ushort)wParam.Value, repeat: false, shift: (lParam.Value & 1) != 0, control: (lParam.Value & 2) != 0);
                return default;
            case PInvoke.WM_KEYUP:
                OnKeyUp((VIRTUAL_KEY)(ushort)wParam.Value);
                return default;
            case PInvoke.WM_KILLFOCUS:
                _pick.ForgetPress(); // the release of a held number key goes to whoever has the keyboard now
                break;
            case PInvoke.WM_ACTIVATE:
                // Back from the Windows settings, perhaps: which program opens what may have changed there.
                if (_showFileTypes && (wParam.Value & 0xFFFF) != 0) LoadFileTypes(apply: null, tick: false);
                break;

            case PInvoke.WM_DROPFILES:
                OnDropFiles(new HDROP((void*)wParam.Value));
                return default;
            case PInvoke.WM_COPYDATA:
                if (SingleInstance.ReadForwarded(lParam) is { } forwarded)
                {
                    OpenForwarded(forwarded);
                    BringToFront();
                    return new LRESULT(1);
                }
                return default;

            case PInvoke.WM_DESTROY:
                WriteStatistics();
                SaveSettings();
                _metricsCancel?.Cancel();
                _alignCancel?.Cancel();
                StopWatching();
                _thumbs?.Dispose();
                _pendingPack?.Dispose();
                PInvoke.PostQuitMessage(0);
                return default;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}

internal static class CrashLog
{
    public static void Write(Exception e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastImageViewer2");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:O} {e}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
