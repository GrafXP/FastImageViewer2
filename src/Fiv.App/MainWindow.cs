using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Fiv.App;

/// <summary>
/// The viewer window. Start-up runs three things at once: this thread creates the window, one
/// thread creates the Direct3D device (the slowest step), one reads and decodes the image. The
/// first picture is painted with GDI if the device is not up yet; Direct3D takes over when it is.
/// Nothing is drawn unless something changed: every change invalidates the window, and WM_PAINT,
/// which Windows delivers once the input queue is empty, renders one frame.
/// </summary>
internal sealed unsafe partial class MainWindow
{
    const uint WM_APP_LOADED = PInvoke.WM_APP + 1;
    const uint WM_APP_DEVICE = PInvoke.WM_APP + 2;
    const uint WM_APP_UPLOADED = PInvoke.WM_APP + 3;
    const uint WM_APP_UI = PInvoke.WM_APP + 4;
    const uint WM_APP_FOLDER = PInvoke.WM_APP + 5;
    const nuint ShowTimerId = 1;
    const uint ShowTimeoutMs = 150;
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

    readonly ImageLoader _loader;
    readonly ConcurrentQueue<LoadResult> _results = new();
    readonly ConcurrentQueue<(LoadedImage Image, GpuImage? Gpu, string? Error, double Milliseconds)> _uploads = new();
    int _generation;

    // The image on display, and the one it is replacing, which stays visible until the new one has pixels.
    LoadedImage? _image;
    LoadedImage? _outgoing;
    ViewState _view, _outgoingView;
    DisplaySettings _display = DisplaySettings.Default, _outgoingDisplay;
    bool _keepView;

    // What next/previous walks through: the files given explicitly, or, after a single file
    // was opened, the index of its folder once that has been read in the background.
    readonly List<string> _files = [];
    FolderIndex? _folder;
    string? _folderFor; // the file whose folder is being, or has been, indexed
    readonly ConcurrentQueue<(string File, FolderIndex? Index)> _folders = new();
    int _fileIndex;

    // While a load has not shown anything yet, further navigation only moves this target; the
    // image being loaded is shown first. Holding an arrow key therefore always makes progress.
    int _pendingIndex = -1;
    bool _loadInFlight;

    int FileCount => _folder?.Count ?? _files.Count;
    string FileAt(int index) => _folder != null ? _folder.PathAt(index) : _files[index];
    int TargetIndex => _pendingIndex >= 0 ? _pendingIndex : _fileIndex;

    bool _panning;
    int _mouseX = -1, _mouseY = -1;

    bool _stampedRead, _stampedPreview, _stampedGdi, _stampedFirstFrame, _stampedFullDecoded, _stampedFullFrame;

    public MainWindow(Options options)
    {
        _options = options;
        _files.AddRange(options.Files);
        _loader = new ImageLoader(result =>
        {
            _results.Enqueue(result);
            Post(WM_APP_LOADED);
        });
    }

    int StatusBarHeight => (int)Math.Round(24 * _dpiScale);
    int ViewWidth => Math.Max(1, _clientWidth);
    int ViewHeight => Math.Max(1, _clientHeight - StatusBarHeight);

    public int Run()
    {
        s_instance = this;
        PlanWindow();
        StartDeviceThread();
        if (_files.Count > 0) LoadCurrent();
        CreateWindow();
        Perf.Stamp("window");

        // Whatever the workers finished before there was a window to notify.
        OnLoaded();
        OnDeviceReady();
        if (_files.Count == 0) EnsureVisible();
        else PInvoke.SetTimer(_hwnd, ShowTimerId, ShowTimeoutMs, null);

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
        System.Drawing.Point cursor;
        PInvoke.GetCursorPos(&cursor);
        HMONITOR monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO info = new() { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(monitor, &info);
        RECT work = info.rcWork;
        uint dpi = PInvoke.GetDpiForSystem();
        _dpiScale = dpi / 96.0;

        RECT frame = default;
        PInvoke.AdjustWindowRectExForDpi(&frame, WINDOW_STYLE.WS_OVERLAPPEDWINDOW, false, WINDOW_EX_STYLE.WS_EX_ACCEPTFILES, dpi);
        int frameWidth = frame.right - frame.left, frameHeight = frame.bottom - frame.top;
        int workWidth = work.right - work.left, workHeight = work.bottom - work.top;
        _windowWidth = Math.Min(workWidth, (int)(workWidth * 0.85));
        _windowHeight = Math.Min(workHeight, (int)(workHeight * 0.88));
        _windowX = work.left + (workWidth - _windowWidth) / 2;
        _windowY = work.top + (workHeight - _windowHeight) / 2;
        _clientWidth = _windowWidth - frameWidth;
        _clientHeight = _windowHeight - frameHeight;
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
        RECT client;
        PInvoke.GetClientRect(_hwnd, &client);
        _clientWidth = client.right;
        _clientHeight = client.bottom;
        _dpiScale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
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
        _extraFrames = 2;
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    /// <summary>The window stays hidden until there is a picture to show in it, or the timeout passes.</summary>
    void EnsureVisible()
    {
        if (_visible) return;
        _visible = true;
        PInvoke.KillTimer(_hwnd, ShowTimerId);
        PInvoke.ShowWindow(_hwnd, _options.NoActivate ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_SHOW);
    }

    void UpdateTitle()
    {
        string title = _image == null ? "FastImageViewer2" : $"{System.IO.Path.GetFileName(_image.Path)} - FastImageViewer2";
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

    // ---------------------------------------------------------------- files

    void Open(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count == 0) return;
        _pendingIndex = -1;
        _files.Clear();
        _files.AddRange(list);

        // Another file of the folder already indexed: keep the index.
        if (list.Count == 1 && _folder != null &&
            string.Equals(System.IO.Path.GetDirectoryName(list[0]), _folder.Directory, StringComparison.OrdinalIgnoreCase) &&
            _folder.Find(System.IO.Path.GetFileName(list[0].AsSpan())) is >= 0 and int position)
        {
            _fileIndex = position;
        }
        else
        {
            _folder = null;
            _folderFor = null;
            _fileIndex = 0;
        }
        LoadCurrent();
    }

    void Navigate(int index)
    {
        if (FileCount == 0) return;
        index = Math.Clamp(index, 0, FileCount - 1);
        if (index == TargetIndex) return;
        if (_loadInFlight)
        {
            _pendingIndex = index;
            return;
        }
        _fileIndex = index;
        LoadCurrent();
    }

    /// <summary>Reads and sorts the folder of a file that was opened on its own, so that next and previous work.</summary>
    void StartFolderIndex(string file)
    {
        _folderFor = file;
        new Thread(() =>
        {
            FolderIndex? index = null;
            try
            {
                if (System.IO.Path.GetDirectoryName(file) is { Length: > 0 } directory) index = FolderIndex.Build(directory);
            }
            catch (IOException)
            {
                // No index: the file stays the only one to show.
            }
            _folders.Enqueue((file, index));
            Post(WM_APP_FOLDER);
        }) { IsBackground = true, Name = "fiv index" }.Start();
    }

    void OnFolderIndexed()
    {
        while (_folders.TryDequeue(out var item))
        {
            if (item.Index == null || item.File != _folderFor || _files.Count != 1 || _files[0] != item.File) continue;
            int position = item.Index.Find(System.IO.Path.GetFileName(item.File.AsSpan()));
            if (position < 0) continue; // not a file type the index lists
            _folder = item.Index;
            _fileIndex = position;
            Invalidate();
        }
    }

    void LoadCurrent()
    {
        _requested = Stopwatch.GetTimestamp();
        _loadInFlight = true;
        _generation = _loader.Load(FileAt(_fileIndex), ViewWidth, ViewHeight);
    }

    long _requested; // when the latest load was asked for

    // ---------------------------------------------------------------- loading

    /// <summary>Takes what the loader delivered. UI thread.</summary>
    void OnLoaded()
    {
        bool changed = false;
        while (_results.TryDequeue(out LoadResult? result))
        {
            if (result.Generation != _generation)
            {
                result.Image?.Dispose();
                result.Mips?.Dispose();
                continue;
            }
            changed = true;
            _loadInFlight = false;
            if (result.FileRead != 0) Perf.StampOnce(ref _stampedRead, "file read", result.FileRead);
            bool isNew = _image == null || _image.Generation != result.Generation;
            if (isNew) Replace(new LoadedImage(result.Path, result.Generation));
            LoadedImage image = _image!;

            switch (result.Stage)
            {
                case LoadStage.Failed:
                    image.Error = result.Error;
                    break;

                case LoadStage.Preview:
                    if (image.Full != null)
                    {
                        result.Image!.Dispose(); // the full decode finished first
                        break;
                    }
                    image.Preview = result.Image;
                    image.PreviewInfo = result;
                    Describe(image, result);
                    if (!_stampedPreview)
                    {
                        _stampedPreview = true;
                        Perf.Stamp("preview decoded", result.Completed);
                    }
                    if (_gpu != null) image.GpuPreview = GpuImage.Create(_gpu.Device, image.Preview!, null);
                    break;

                case LoadStage.Full:
                    image.Full = result.Image;
                    image.Mips = result.Mips;
                    image.FullInfo = result;
                    image.ValueRange = result.ValueRange;
                    Describe(image, result);
                    if (!_stampedFullDecoded)
                    {
                        _stampedFullDecoded = true;
                        Perf.Stamp("full decoded", result.Completed);
                    }
                    if (image.Layout is PixelLayout.Gray16 or PixelLayout.GrayFloat)
                    {
                        _display.WindowLow = image.ValueRange.Min;
                        _display.WindowHigh = image.ValueRange.Max;
                    }
                    if (_gpu != null) StartUpload(image);
                    break;
            }

            if (isNew)
            {
                if (image.HasSize && !(_keepView && _outgoing != null)) _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
                UpdateTitle();
            }
            if (image.Error != null) DropOutgoing();
        }
        if (!changed) return;
        if (_image?.BestGpu != null) DropOutgoing();
        EnsureVisible();
        Invalidate();

        if (_files.Count == 1 && _folder == null && _folderFor == null) StartFolderIndex(_files[0]);
        if (!_loadInFlight && _pendingIndex >= 0)
        {
            // Where the user went while this image was loading.
            int next = _pendingIndex;
            _pendingIndex = -1;
            if (next != _fileIndex)
            {
                _fileIndex = next;
                LoadCurrent();
            }
        }
    }

    static void Describe(LoadedImage image, LoadResult result)
    {
        image.Width = result.Image!.SourceWidth;
        image.Height = result.Image.SourceHeight;
        image.Orientation = result.Orientation;
        image.FileSize = result.FileSize;
    }

    /// <summary>Makes <paramref name="image"/> current. The previous one stays on screen until the new one can be drawn.</summary>
    void Replace(LoadedImage image)
    {
        DropOutgoing();
        if (_image?.BestGpu != null)
        {
            _outgoing = _image;
            _outgoingView = _view;
            _outgoingDisplay = _display;
        }
        else
        {
            _image?.Dispose();
        }
        _image = image;
        _display.WindowLow = 0;
        _display.WindowHigh = 1;
    }

    void DropOutgoing()
    {
        _outgoing?.Dispose();
        _outgoing = null;
    }

    /// <summary>Creates the full-resolution textures on a worker; the 100 MB copy must not block the UI thread.</summary>
    void StartUpload(LoadedImage image)
    {
        if (image.Uploading || image.GpuFull != null || image.Full == null) return;
        image.Uploading = true;
        nint device = (nint)_gpu!.Device;
        new Thread(() =>
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                GpuImage gpuImage = GpuImage.Create((ID3D11Device*)device, image.Full!, image.Mips);
                _uploads.Enqueue((image, gpuImage, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (Exception e)
            {
                _uploads.Enqueue((image, null, e.Message, 0));
            }
            Post(WM_APP_UPLOADED);
        }) { IsBackground = true, Name = "fiv upload" }.Start();
    }

    void OnUploaded()
    {
        while (_uploads.TryDequeue(out var upload))
        {
            LoadedImage image = upload.Image;
            if (!image.UploadFinished())
            {
                upload.Gpu?.Dispose(); // the image was replaced while it uploaded
                continue;
            }
            if (upload.Gpu == null)
            {
                image.Error = "Not enough video memory for the full image: " + upload.Error;
            }
            else
            {
                image.GpuFull = upload.Gpu;
                image.UploadMs = upload.Milliseconds;
                image.GpuPreview?.Dispose();
                image.GpuPreview = null;
                image.Preview?.Dispose();
                image.Preview = null;
            }
            image.Mips?.Dispose(); // the GPU has its own copy now
            image.Mips = null;
            if (image == _image) DropOutgoing();
        }
        Invalidate();
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
                var ui = new Ui(gpu, _dpiScale);
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

        if (_image is { } image)
        {
            if (image.Preview != null && image.GpuPreview == null && image.Full == null)
                image.GpuPreview = GpuImage.Create(_gpu.Device, image.Preview, null);
            StartUpload(image);
        }
        Invalidate();
    }

    // ---------------------------------------------------------------- painting

    void OnPaint()
    {
        // Until Direct3D has something to show, a picture GDI can paint must not be replaced by an empty frame.
        bool useGpu = _renderer != null && (_presented || _image?.BestGpu != null || GdiSource(_image) == null);
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
        view.ToScreen(ViewWidth, ViewHeight).After(image.Orientation.ToUpright(image.Width, image.Height));

    void RenderFrame()
    {
        GraphicsDevice gpu = _gpu!;
        long started = Stopwatch.GetTimestamp();
        gpu.BeginFrame(0.11f, 0.11f, 0.12f);

        LoadedImage? drawn = _image?.BestGpu != null ? _image : _outgoing;
        if (drawn?.BestGpu is { } gpuImage)
        {
            bool current = drawn == _image;
            ViewState view = current ? _view : _outgoingView;
            DisplaySettings display = current ? _display : _outgoingDisplay;
            _renderer!.Draw(gpuImage, ImageToScreen(drawn, view), view.Zoom, display);
        }
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
        if (_image?.FullFrame > 0)
        {
            // Frame statistics of the steady state, for --perf-log: loading is over, this is pan and zoom.
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
                }
            }
        }
        _lastPresented = presented;
        if (drawn != null) Perf.StampOnce(ref _stampedFirstFrame, "first frame (D3D)", presented);
        if (drawn != null && drawn == _image)
        {
            if (drawn.PreviewFrame == 0)
            {
                drawn.PreviewFrame = presented;
                _imagesShown++;
            }
            if (drawn.GpuFull != null && drawn.FullFrame == 0)
            {
                drawn.FullFrame = presented;
                Perf.StampOnce(ref _stampedFullFrame, "full frame", presented);
                Loaded(drawn);
            }
        }

        if (_extraFrames > 0)
        {
            _extraFrames--;
            PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
        }
    }

    double _lastFrameMs;
    int _frames, _intervals, _imagesShown;
    double _frameCpuMs, _frameMaxCpuMs, _frameMaxMs, _intervalMs, _intervalMaxMs;
    long _lastPresented;

    void WriteFrameStatistics()
    {
        if (_options.PerfLog == null) return;
        try
        {
            File.AppendAllText(_options.PerfLog,
                $"images_shown={_imagesShown} last_index={_fileIndex + 1}/{FileCount} frames={_frames} cpu_mean_ms={_frameCpuMs / Math.Max(_frames, 1):F3} cpu_max_ms={_frameMaxCpuMs:F3} frame_max_ms={_frameMaxMs:F2} " +
                $"intervals={_intervals} interval_mean_ms={(_intervals > 0 ? _intervalMs / _intervals : 0):F2} interval_max_ms={_intervalMaxMs:F2}\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>An image is completely on screen: report and, in benchmark runs, leave.</summary>
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
                    $"qpf={Stopwatch.Frequency} requested={_requested} first_frame={image.PreviewFrame} full_frame={image.FullFrame} file={System.IO.Path.GetFileName(image.Path)}\n");
            }
        }
        catch (IOException)
        {
        }
        if (_options.ExitWhenLoaded) PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
    }

    bool _perfOutWritten;

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
        DecodedImage? source = GdiSource(_image);
        if (source == null)
        {
            PInvoke.FillRect(dc, &client, _backgroundBrush);
            PInvoke.EndPaint(_hwnd, &paint);
            return;
        }

        LoadedImage image = _image!;
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
    }

    // ---------------------------------------------------------------- input

    void OnSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return; // minimised
        _clientWidth = width;
        _clientHeight = height;
        _gpu?.Resize(width, height);
        if (_view.Fit && _image is { HasSize: true } image) _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
        Invalidate();
    }

    void OnMouseWheel(int delta, int screenX, int screenY)
    {
        if (_image is not { HasSize: true }) return;
        var point = new System.Drawing.Point(screenX, screenY);
        PInvoke.ScreenToClient(_hwnd, &point);
        double factor = Math.Pow(WheelZoomStep, delta / (double)PInvoke.WHEEL_DELTA);
        _view.ZoomAt(_view.Zoom * factor, point.X, point.Y, ViewWidth, ViewHeight);
        Invalidate();
    }

    void OnMouseMove(int x, int y)
    {
        if (_panning && _image is { HasSize: true } image)
        {
            _view.Pan(x - _mouseX, y - _mouseY, image.UprightWidth, image.UprightHeight);
        }
        _mouseX = x;
        _mouseY = y;
        Invalidate(); // the readout under the cursor changed
    }

    void OnKeyDown(VIRTUAL_KEY key)
    {

        LoadedImage? image = _image is { HasSize: true } ? _image : null;
        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                if (_fullscreen) ToggleFullscreen();
                else PInvoke.PostMessage(_hwnd, PInvoke.WM_CLOSE, default, default);
                break;
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
                if (image != null) _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
                break;
            case VIRTUAL_KEY.VK_1 or VIRTUAL_KEY.VK_NUMPAD1:
                if (image != null) _view.ZoomAt(1, ViewWidth / 2.0, ViewHeight / 2.0, ViewWidth, ViewHeight);
                break;
            case VIRTUAL_KEY.VK_OEM_PLUS or VIRTUAL_KEY.VK_ADD:
                if (image != null) _view.ZoomAt(_view.Zoom * WheelZoomStep, ViewWidth / 2.0, ViewHeight / 2.0, ViewWidth, ViewHeight);
                break;
            case VIRTUAL_KEY.VK_OEM_MINUS or VIRTUAL_KEY.VK_SUBTRACT:
                if (image != null) _view.ZoomAt(_view.Zoom / WheelZoomStep, ViewWidth / 2.0, ViewHeight / 2.0, ViewWidth, ViewHeight);
                break;
            case VIRTUAL_KEY.VK_I:
                _display.Sharp = !_display.Sharp;
                break;
            case VIRTUAL_KEY.VK_L:
                _keepView = !_keepView;
                break;
            case VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_NEXT or VIRTUAL_KEY.VK_SPACE:
                Navigate(TargetIndex + 1);
                break;
            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_BACK:
                Navigate(TargetIndex - 1);
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
        Open(files);
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
            if (mouse && _ui.WantsMouse && !_panning) return default;
            if (key && _ui.WantsKeyboard) return default;
        }

        switch (message)
        {
            case PInvoke.WM_MOUSELEAVE:
                _mouseX = _mouseY = -1;
                return default;

            case WM_APP_LOADED:
                OnLoaded();
                return default;
            case WM_APP_DEVICE:
                OnDeviceReady();
                return default;
            case WM_APP_UPLOADED:
                OnUploaded();
                return default;
            case WM_APP_UI:
                OnUiReady();
                return default;
            case WM_APP_FOLDER:
                OnFolderIndexed();
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
            case PInvoke.WM_TIMER when wParam.Value == ShowTimerId:
                EnsureVisible();
                OnLoaded();
                OnDeviceReady();
                OnUiReady();
                return default;

            case PInvoke.WM_MOUSEWHEEL:
                OnMouseWheel(HighWord((nint)wParam.Value), LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_MOUSEMOVE:
                OnMouseMove(LowWord(lParam.Value), HighWord(lParam.Value));
                return default;
            case PInvoke.WM_LBUTTONDOWN:
                _panning = true;
                _mouseX = LowWord(lParam.Value);
                _mouseY = HighWord(lParam.Value);
                PInvoke.SetCapture(hwnd);
                return default;
            case PInvoke.WM_LBUTTONUP:
                _panning = false;
                PInvoke.ReleaseCapture();
                return default;
            case PInvoke.WM_LBUTTONDBLCLK:
                if (_image is { HasSize: true } image)
                {
                    if (_view.Fit || _view.Zoom != 1) _view.ZoomAt(1, LowWord(lParam.Value), HighWord(lParam.Value), ViewWidth, ViewHeight);
                    else _view.FitTo(image.UprightWidth, image.UprightHeight, ViewWidth, ViewHeight);
                    Invalidate();
                }
                return default;
            case PInvoke.WM_KEYDOWN:
                OnKeyDown((VIRTUAL_KEY)(ushort)wParam.Value);
                return default;

            case PInvoke.WM_DROPFILES:
                OnDropFiles(new HDROP((void*)wParam.Value));
                return default;
            case PInvoke.WM_COPYDATA:
                if (SingleInstance.ReadForwarded(lParam) is { } forwarded)
                {
                    Open(forwarded);
                    BringToFront();
                    return new LRESULT(1);
                }
                return default;

            case PInvoke.WM_DESTROY:
                WriteFrameStatistics();
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
