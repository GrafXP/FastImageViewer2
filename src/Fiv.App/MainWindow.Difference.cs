using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Render;
using Hexa.NET.ImGui;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Fiv.App;

// Comparing, second part: what two images differ by. The difference view and the similarity
// heat map are two more of the modes that show a pair in one pane; the metrics panel, the
// values written into the pixels at high zoom, the region the numbers are taken over, and
// saving all of it to files are here too.
//
// What is on screen is made by the GPU from the textures, at whatever zoom, for looking at.
// The numbers and the saved files are computed on the CPU from the full-resolution pixels as
// the files have them (Fiv.Comparing), on a worker, and arrive when they are ready.
internal sealed unsafe partial class MainWindow
{
    /// <summary>Posted by workers: there is something in <see cref="_uiQueue"/> to run on the UI thread.</summary>
    const uint WM_APP_RUN = PInvoke.WM_APP + 9;

    const float MaxGain = 4096, MaxSimilarityGain = 64;

    DifferenceKind _differenceKind;
    Colormap _differenceMap = Colormap.Gray, _signedMap = Colormap.Coolwarm;
    float _differenceGain = 1, _similarityGain = 2;

    /// <summary>From which difference on a pixel counts as changed, in the units the GPU samples: a fraction of the range for integer images.</summary>
    float _threshold = 2f / 255;

    bool _mask, _showMetrics, _pixelValues;
    bool _autoGain;     // set the gain from the largest difference as soon as that is known
    bool _captureView;  // copy what the panes show in the next frame

    /// <summary>The canvas rectangle the numbers are taken over; null: everywhere both images have pixels.</summary>
    CanvasRect? _region;
    bool _regionArmed, _dragRegion;
    bool _dragRegionForAlign; // the rectangle being dragged is the alignment's (see MainWindow.Align), not the numbers'
    double _regionStartX, _regionStartY;

    readonly ConcurrentQueue<Action> _uiQueue = new();

    /// <summary>Runs something on the UI thread, from any thread.</summary>
    void RunOnUi(Action action)
    {
        _uiQueue.Enqueue(action);
        Post(WM_APP_RUN);
    }

    void OnUiQueue()
    {
        while (_uiQueue.TryDequeue(out Action? action)) action();
        Invalidate();
    }

    /// <summary>The colours in use: the signed difference has its own choice, a map with two directions.</summary>
    Colormap CurrentMap => _compareMode == CompareMode.Difference && _differenceKind == DifferenceKind.Signed ? _signedMap : _differenceMap;

    void SetCurrentMap(Colormap map)
    {
        if (_compareMode == CompareMode.Difference && _differenceKind == DifferenceKind.Signed) _signedMap = map;
        else _differenceMap = map;
    }

    /// <summary>The value range differences are scaled by: the span between the display's black and white.</summary>
    float FullScale => MathF.Max(MathF.Abs(_display.WindowHigh - _display.WindowLow), 1e-12f);

    DifferenceView CurrentDifferenceView() => new()
    {
        Kind = _differenceKind,
        Map = CurrentMap,
        Gain = _differenceGain,
        FullScale = FullScale,
        Channel = (int)_display.Channel,
        Mask = _mask,
        Threshold = _threshold,
    };

    ToneSettings CurrentTone() => new()
    {
        Low = _display.WindowLow,
        High = _display.WindowHigh,
        Gamma = _display.Gamma,
        Exposure = _display.Exposure,
        Channel = (int)_display.Channel,
    };

    static ReadOnlySpan<byte> KindLabel(DifferenceKind kind) => kind switch
    {
        DifferenceKind.Signed => "signed"u8,
        DifferenceKind.Squared => "squared"u8,
        DifferenceKind.Relative => "relative"u8,
        _ => "absolute"u8,
    };

    // ---------------------------------------------------------------- the pair and its units

    /// <summary>The two images the two-image modes show, if both are loaded far enough to know their size.</summary>
    bool TryGetPair(out LoadedImage first, out LoadedImage second)
    {
        first = second = null!;
        if (!Comparing) return false;
        (int a, int b) = _pick.Pair;
        if (a == b || a < 0 || b < 0 || a >= _compare.Count || b >= _compare.Count) return false;
        if (_compare[a].Image is not { HasSize: true } imageA || _compare[b].Image is not { HasSize: true } imageB) return false;
        first = imageA;
        second = imageB;
        return true;
    }

    /// <summary>The units the pair's differences are in, and one step of them in the units the GPU samples.</summary>
    ValueUnit PairUnit(out float step)
    {
        ValueUnit unit = ValueUnit.Bits8;
        if (TryGetPair(out LoadedImage first, out LoadedImage second) && first.Layout is { } a && second.Layout is { } b) unit = CompareMetrics.Units(a, b).Unit;
        step = CompareMetrics.UnitStep(unit);
        return unit;
    }

    /// <summary>The entry of the metrics that goes with the channel the display shows.</summary>
    ChannelMetrics? ShownChannel(MetricsResult result) => (_display.Channel switch
    {
        ChannelView.Red => result.Find(ChannelMetrics.Red),
        ChannelView.Green => result.Find(ChannelMetrics.Green),
        ChannelView.Blue => result.Find(ChannelMetrics.Blue),
        ChannelView.Alpha => result.Find(ChannelMetrics.Alpha),
        ChannelView.Luma => result.Find(ChannelMetrics.Luma),
        _ => null,
    }) ?? result.Main;

    // ---------------------------------------------------------------- metrics

    /// <summary>What a set of numbers is of: the two images' pixels, where they lie on the canvas, and the region.</summary>
    readonly record struct MetricsKey(DecodedImage First, DecodedImage Second, Affine2 FirstToCanvas, Affine2 SecondToCanvas, CanvasRect? Region)
    {
        public MetricsKey WholeImage => this with { Region = null };
    }

    MetricsResult? _metrics;
    MetricsKey _metricsKey;
    bool _metricsFinal, _metricsWithMap;
    int _metricsJob;
    bool _metricsRunning, _metricsRunningMap;
    MetricsKey _metricsRunningKey, _metricsFailedKey;
    CancellationTokenSource? _metricsCancel;
    string? _metricsError;

    // The similarity per pixel, for the heat map: on the CPU for readouts and saving, on the GPU for drawing.
    DecodedImage? _similarityMap;
    GpuImage? _similarityGpu;
    CanvasRect _similarityArea;
    MetricsKey _similarityKey;

    bool TryMetricsKey(out MetricsKey key)
    {
        key = default;
        if (_canvasWidth <= 0 || !TryGetPair(out LoadedImage first, out LoadedImage second)) return false;
        if (first.Full is not { } a || second.Full is not { } b) return false; // numbers are of the full images only
        (int indexA, int indexB) = _pick.Pair;
        key = new MetricsKey(a, b, ImageToCanvas(indexA), ImageToCanvas(indexB), _region);
        return true;
    }

    /// <summary>The numbers of the pair on show, if they have been computed for it as it is now.</summary>
    MetricsResult? CurrentMetrics => _metrics != null && TryMetricsKey(out MetricsKey key) && key == _metricsKey ? _metrics : null;

    bool SimilarityReady => _similarityGpu != null && TryMetricsKey(out MetricsKey key) && key.WholeImage == _similarityKey;

    /// <summary>Starts the numbers for the pair on show if someone wants them and they are neither there nor on their way. Cheap; called every frame.</summary>
    void UpdateMetrics()
    {
        bool map = _compareMode == CompareMode.Ssim;
        if (!Comparing || !(_showMetrics || map || _autoGain)) return;
        if (_dragAlign) return; // an image is being dragged into place: the numbers follow when it is let go
        if (!TryMetricsKey(out MetricsKey key)) return;
        if (_similarityMap != null && key.WholeImage != _similarityKey) DropSimilarity(); // of another pair: its memory is wanted back
        if (_metrics != null && _metricsFinal && _metricsKey == key && (!map || _metricsWithMap)) return;
        if (_metricsRunning && _metricsRunningKey == key && (!map || _metricsRunningMap)) return;
        if (_metricsError != null && _metricsFailedKey == key) return; // it failed for exactly this: do not try for ever
        StartMetrics(key, map);
    }

    void StartMetrics(MetricsKey key, bool map)
    {
        _metricsCancel?.Cancel();
        var cancel = _metricsCancel = new CancellationTokenSource();
        int job = ++_metricsJob;
        _metricsRunning = true;
        _metricsRunningKey = key;
        _metricsRunningMap = map;
        _metricsError = null;
        // The worker keeps the pixels alive, whatever the cache does with the images meanwhile.
        key.First.AddReference();
        key.Second.AddReference();
        GraphicsDevice? gpu = _gpu;
        long started = Stopwatch.GetTimestamp();
        Task.Run(() =>
        {
            MetricsResult? result = null;
            GpuImage? texture = null;
            string? error = null;
            try
            {
                var first = new CompareSource(key.First, key.FirstToCanvas);
                var second = new CompareSource(key.Second, key.SecondToCanvas);
                result = CompareMetrics.Compute(first, second, key.Region, ssim: true, map, cancel.Token,
                    early => RunOnUi(() => OnMetrics(job, key, early, null, final: false, null, 0)));
                if (result?.SsimMap is { } similarity && gpu != null)
                {
                    using var mips = new MipChain(similarity);
                    texture = GpuImage.Create(gpu.Device, similarity, mips);
                }
            }
            catch (Exception e)
            {
                // Out of memory for a map of a very large pair, most likely. The viewer goes on without the numbers.
                CrashLog.Write(e);
                error = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
                result?.SsimMap?.Dispose();
                result = null;
            }
            finally
            {
                key.First.Dispose();
                key.Second.Dispose();
            }
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            RunOnUi(() => OnMetrics(job, key, result, texture, final: true, error, ms));
        });
    }

    /// <summary>A worker has numbers: the early ones without similarity, or the final ones. UI thread.</summary>
    void OnMetrics(int job, MetricsKey key, MetricsResult? result, GpuImage? texture, bool final, string? error, double ms)
    {
        if (job != _metricsJob)
        {
            // Of a pair, a region or a placement the user has since left.
            result?.SsimMap?.Dispose();
            texture?.Dispose();
            return;
        }
        if (final)
        {
            _metricsRunning = false;
            _metricsError = error;
            if (error != null) _metricsFailedKey = key;
        }
        if (result == null) return; // cancelled or failed
        _metrics = result;
        _metricsKey = key;
        _metricsFinal = final;
        _metricsWithMap = final && _metricsRunningMap;
        if (final && result.SsimMap != null)
        {
            DropSimilarity();
            _similarityMap = result.SsimMap;
            _similarityGpu = texture;
            _similarityArea = result.SsimArea;
            _similarityKey = key.WholeImage;
        }
        if (_autoGain) ApplyAutoGain();
        if (final && _options.PerfLog != null)
        {
            try
            {
                File.AppendAllText(_options.PerfLog,
                    $"metrics pixels={result.Pixels} basic_ms={result.BasicMs:F1} ssim_ms={result.SsimMs:F1} total_ms={ms:F1} map={(result.SsimMap != null ? 1 : 0)} at={Stopwatch.GetTimestamp()}\n");
            }
            catch (IOException)
            {
            }
        }
    }

    void DropSimilarity()
    {
        _similarityGpu?.Dispose();
        _similarityMap?.Dispose();
        _similarityGpu = null;
        _similarityMap = null;
    }

    /// <summary>Forgets the numbers and stops whatever is computing them: the comparison ended.</summary>
    void ResetMetrics()
    {
        _metricsCancel?.Cancel();
        _metricsCancel = null;
        _metricsJob++;
        _metricsRunning = false;
        _metrics = null;
        _metricsError = null;
        _metricsKey = _metricsRunningKey = _metricsFailedKey = default;
        _autoGain = false;
        _region = null;
        _regionArmed = _dragRegion = false;
        DropSimilarity();
    }

    /// <summary>Chooses the gain that puts the largest difference of the shown channel at the end of the colour map.</summary>
    void AutoGain()
    {
        _autoGain = true;
        ApplyAutoGain();
    }

    void ApplyAutoGain()
    {
        if (CurrentMetrics is not { } metrics || ShownChannel(metrics) is not { } channel) return; // comes when the numbers do
        _autoGain = false;
        PairUnit(out float step);
        double largest = channel.MaxError * step; // in the units the GPU samples
        _differenceGain = largest > 0 ? (float)Math.Clamp(FullScale / largest, 1, MaxGain) : 1;
    }

    // ---------------------------------------------------------------- drawing the two new modes

    /// <summary>The settings of the combine pass for the difference view.</summary>
    MixSettings DifferenceMix() => new() { Kind = MixKind.Difference, Amount = _differenceGain, Difference = CurrentDifferenceView(), Colors = CurrentMap };

    /// <summary>
    /// Draws the similarity heat map into a pane, or, until it has been computed, the first image
    /// of the pair as it is.
    /// </summary>
    void RenderSimilarity(in Pane pane, in PaneRect r)
    {
        if (SimilarityReady)
        {
            Affine2 mapToPane = CanvasToPane(pane.First, r).After(Affine2.ScaleTranslate(1, 1, _similarityArea.X, _similarityArea.Y));
            // The map is a rectangle. Of a second image that is turned, only where it has pixels is the map shown.
            GpuImage? second = pane.Second >= 0 && !_compare[pane.Second].Pose.IsIdentity ? Drawable(pane.Second) : null;
            var mix = new MixSettings { Kind = MixKind.Heat, Amount = _similarityGain, Colors = _differenceMap, WithinSecond = second != null };
            Affine2 secondToPane = second != null ? CanvasToPane(pane.Second, r).After(ImageToCanvas(pane.Second)) : Affine2.Identity;
            _compareRenderer ??= new CompareRenderer(_gpu!, _renderer!);
            _compareRenderer.Draw(_similarityGpu, mapToPane, ViewOf(pane.First).Zoom, second, secondToPane, second != null ? ZoomOf(pane.Second, _compare[pane.Second].Image!) : 1,
                _display, mix, r.X, r.Y, r.Width, r.Height, Backdrop);
        }
        else if (Drawable(pane.First) is { } texture)
        {
            LoadedImage image = _compare[pane.First].Image!;
            _renderer!.Draw(texture, CanvasToScreen(pane.First, r).After(ImageToCanvas(pane.First)), ZoomOf(pane.First, image), _display, r.X, r.Y, r.Width, r.Height);
        }
    }

    /// <summary>Copies what the panes show, without labels and panels, to the clipboard. Called in a frame, after the panes are drawn.</summary>
    void CaptureView()
    {
        _captureView = false;
        using DecodedImage? shot = _gpu!.Capture(ViewX, ViewY, ViewWidth, ViewHeight);
        if (shot == null) return;
        if (_options.ExportDir is { } folder)
        {
            // Tests look at a file, and leave the clipboard of whoever is working on the machine alone.
            string path = Path.Combine(folder, "copied-view.png");
            DecodedImage held = shot;
            held.AddReference();
            Task.Run(() =>
            {
                using (held) Export("copied-view.png", () => ImageWriter.WritePng(path, held));
            });
            return;
        }
        Notice(FileActions.CopyToClipboard(_hwnd, shot) ? "View copied." : "The view could not be copied.");
    }

    // ---------------------------------------------------------------- overlays: colour bar, readout, region

    const uint ColorRegion = 0xFF40E0FF, ColorAlignRegion = 0xFF70F080, ColorGrid = 0x58808080, ColorValue = 0xFFFFFFFF, ColorDelta = 0xFF70E8FF,
        ColorRed = 0xFF8C8CFF, ColorGreen = 0xFF8CFF8C, ColorBlue = 0xFFFFB48C;

    // The same inks for writing on bright pixels.
    const uint InkValue = 0xFF000000, InkDelta = 0xFF003C78, InkRed = 0xFF0000A8, InkGreen = 0xFF005800, InkBlue = 0xFFA83000;

    /// <summary>Writes a number with about four significant digits, without exponents for the sizes pixel values have, into <paramref name="to"/>; returns the bytes written.</summary>
    static int FormatNumber(double value, Span<byte> to, bool sign = false)
    {
        // 255 that came out of float arithmetic as 255.00001 is 255.
        if (Math.Abs(value - Math.Round(value)) < 1e-4 * Math.Max(1, Math.Abs(value))) value = Math.Round(value);
        double size = Math.Abs(value);
        int n;
        bool ok;
        if (double.IsNaN(value)) ok = Utf8.TryWrite(to, $"-", out n);
        else if (double.IsInfinity(value)) ok = Utf8.TryWrite(to, $"{(value > 0 ? "inf" : "-inf")}", out n);
        else if (size == 0) ok = Utf8.TryWrite(to, $"0", out n);
        else if (size >= 1000 || size == Math.Floor(size)) ok = sign ? Utf8.TryWrite(to, $"{value:+0;-0}", out n) : Utf8.TryWrite(to, $"{value:0}", out n);
        else if (size >= 100) ok = sign ? Utf8.TryWrite(to, $"{value:+0.0;-0.0}", out n) : Utf8.TryWrite(to, $"{value:0.0}", out n);
        else if (size >= 10) ok = sign ? Utf8.TryWrite(to, $"{value:+0.00;-0.00}", out n) : Utf8.TryWrite(to, $"{value:0.00}", out n);
        else if (size >= 1) ok = sign ? Utf8.TryWrite(to, $"{value:+0.000;-0.000}", out n) : Utf8.TryWrite(to, $"{value:0.000}", out n);
        else if (size >= 0.001) ok = sign ? Utf8.TryWrite(to, $"{value:+0.0000;-0.0000}", out n) : Utf8.TryWrite(to, $"{value:0.0000}", out n);
        else ok = sign ? Utf8.TryWrite(to, $"{value:+0.00E+0;-0.00E+0}", out n) : Utf8.TryWrite(to, $"{value:0.00E+0}", out n);
        return ok ? n : 0;
    }

    /// <summary>The colour bar of the difference view and of the similarity view, at the right edge of the pane, with its values in the units of the files.</summary>
    void DrawColorBar(ImDrawListPtr draw, in PaneRect r, float scale)
    {
        bool similarity = _compareMode == CompareMode.Ssim;
        if (!similarity && _mask) return; // the mask has two colours and no scale
        PairUnit(out float step);
        double top, middle, bottom;
        ReadOnlySpan<byte> caption;
        bool sign = false, percent = false;
        if (similarity)
        {
            (bottom, middle, top) = (1, 1 - 0.5 / _similarityGain, 1 - 1.0 / _similarityGain);
            caption = "SSIM"u8;
        }
        else
        {
            double end = FullScale / _differenceGain / step; // the difference at the end of the map, in the units of the files
            switch (_differenceKind)
            {
                case DifferenceKind.Signed:
                    (bottom, middle, top, sign) = (-end, 0, end, true);
                    caption = "2nd - 1st"u8;
                    break;
                case DifferenceKind.Squared:
                    (bottom, middle, top) = (0, end * end / 2, end * end);
                    caption = "difference²"u8;
                    break;
                case DifferenceKind.Relative:
                    (bottom, middle, top, percent) = (0, 50.0 / _differenceGain, 100.0 / _differenceGain, true);
                    caption = "% of 1st"u8;
                    break;
                default:
                    (bottom, middle, top) = (0, end / 2, end);
                    caption = "difference"u8;
                    break;
            }
        }

        // Labels first, to know how wide the plate behind bar and labels has to be.
        Span<byte> labels = stackalloc byte[3 * 32];
        Span<int> lengths = stackalloc int[3];
        float labelWidth = 0, lineHeight = ImGui.GetTextLineHeight();
        for (int i = 0; i < 3; i++)
        {
            Span<byte> label = labels.Slice(i * 32, 32);
            int n = FormatNumber(i == 0 ? top : i == 1 ? middle : bottom, label[..28], sign);
            if (percent) label[n++] = (byte)'%';
            label[n] = 0;
            lengths[i] = n;
            fixed (byte* text = label) labelWidth = MathF.Max(labelWidth, ImGui.CalcTextSize(text, text + n).X);
        }
        float captionWidth;
        fixed (byte* text = caption) captionWidth = ImGui.CalcTextSize(text, text + caption.Length).X;

        float pad = MathF.Round(6 * scale), barWidth = MathF.Round(14 * scale), gap = MathF.Round(6 * scale);
        float barHeight = MathF.Round(MathF.Min(260 * scale, r.Height * 0.45f));
        if (barHeight < 3 * lineHeight) return;
        float inner = MathF.Max(barWidth + gap + labelWidth, captionWidth);
        float plateRight = r.Right - pad, plateLeft = plateRight - inner - 2 * pad;
        float barTop = MathF.Round(r.Y + (r.Height - barHeight) / 2), barLeft = plateLeft + pad;
        draw.AddRectFilled(new Vector2(plateLeft, barTop - pad - lineHeight - gap / 2), new Vector2(plateRight, barTop + barHeight + pad), ColorPlate, 3);
        fixed (byte* text = caption) draw.AddText(new Vector2(plateLeft + pad, barTop - lineHeight - gap / 2 - pad / 2), ColorTextDim, text, text + caption.Length);

        // The map from its end at the top to its start at the bottom, in 64 steps of blended colour.
        byte[] table = Colormaps.Table(CurrentMap);
        const int steps = 64;
        for (int i = 0; i < steps; i++)
        {
            uint upper = TableColor(table, 1 - i / (float)steps), lower = TableColor(table, 1 - (i + 1) / (float)steps);
            float y0 = barTop + barHeight * i / steps, y1 = barTop + barHeight * (i + 1) / steps;
            draw.AddRectFilledMultiColor(new Vector2(barLeft, y0), new Vector2(barLeft + barWidth, y1), upper, upper, lower, lower);
        }
        draw.AddRect(new Vector2(barLeft, barTop), new Vector2(barLeft + barWidth, barTop + barHeight), ColorOutline);
        for (int i = 0; i < 3; i++)
        {
            float y = i == 0 ? barTop : i == 1 ? barTop + (barHeight - lineHeight) / 2 : barTop + barHeight - lineHeight;
            fixed (byte* text = labels.Slice(i * 32, 32)) draw.AddText(new Vector2(barLeft + barWidth + gap, y), ColorText, text, text + lengths[i]);
        }

        static uint TableColor(byte[] table, float t)
        {
            (float red, float green, float blue) = Colormaps.Lookup(table, t);
            return 0xFF000000 | ((uint)(blue * 255 + 0.5f) << 16) | ((uint)(green * 255 + 0.5f) << 8) | (uint)(red * 255 + 0.5f);
        }
    }

    /// <summary>
    /// The pixels of the pair under the cursor as R, G, B, A in the units their difference is
    /// taken in (see <see cref="CompareMetrics.Units"/>). False if either has no pixel there.
    /// </summary>
    bool TryGetPairPixels(out Vector4 first, out Vector4 second, out bool colour)
    {
        first = second = default;
        colour = false;
        (int a, int b) = _pick.Pair;
        if (!TryGetComparePixel(a, out _, out _, out PixelValue valueA) || !TryGetComparePixel(b, out _, out _, out PixelValue valueB)) return false;
        return PairValues(valueA, valueB, out first, out second, out colour);
    }

    static bool PairValues(in PixelValue valueA, in PixelValue valueB, out Vector4 first, out Vector4 second, out bool colour)
    {
        (_, float scaleA, float scaleB) = CompareMetrics.Units(valueA.Layout, valueB.Layout);
        first = Spread(valueA) * scaleA;
        second = Spread(valueB) * scaleB;
        colour = IsColour(valueA.Layout) || IsColour(valueB.Layout);
        return true;

        static bool IsColour(PixelLayout layout) => layout is PixelLayout.Bgra8 or PixelLayout.Rgba16;
        // A grey value counts for red, green and blue, as the shaders have it; and a grey image is opaque.
        static Vector4 Spread(in PixelValue value) => IsColour(value.Layout)
            ? new Vector4(value.R, value.G, value.B, value.A)
            : new Vector4(value.R, value.R, value.R, value.Layout == PixelLayout.Gray8 ? 255 : value.Layout == PixelLayout.Gray16 ? 65535 : 1);
    }

    /// <summary>Writes "diff  R +3  G 0  B -5" (or "diff +12", or "SSIM 0.9731") for the point under the cursor into the shared buffer. False if there is nothing to say.</summary>
    bool WriteDifferenceReadout()
    {
        if (_compareMode == CompareMode.Ssim)
        {
            if (!SimilarityReady || _similarityMap is not { } map || !TryGetCursorCanvas(out double x, out double y)) return false;
            int mx = (int)Math.Floor(x) - _similarityArea.X, my = (int)Math.Floor(y) - _similarityArea.Y;
            if (mx < 0 || my < 0 || mx >= map.Width || my >= map.Height) return false;
            float value = ((float*)(map.Pixels + (long)my * map.Stride))[mx];
            return Utf8.TryWrite(_utf8, $"SSIM {value:0.0000}\0", out _);
        }
        if (!TryGetPairPixels(out Vector4 a, out Vector4 b, out bool colour)) return false;
        Vector4 d = b - a;
        if (!Utf8.TryWrite(_utf8, $"diff ", out int n)) return false;
        if (colour)
        {
            ReadOnlySpan<byte> names = "RGB"u8;
            for (int c = 0; c < 3; c++)
            {
                _utf8[n++] = (byte)' ';
                _utf8[n++] = names[c];
                _utf8[n++] = (byte)' ';
                n += FormatNumber(c == 0 ? d.X : c == 1 ? d.Y : d.Z, _utf8.AsSpan(n, 24), sign: true);
                _utf8[n++] = (byte)' ';
            }
        }
        else
        {
            n += FormatNumber(d.X, _utf8.AsSpan(n, 24), sign: true);
        }
        _utf8[n] = 0;
        return true;
    }

    /// <summary>
    /// The outlines of the regions in a pane, also while they are being dragged: the one the
    /// numbers are taken over, and, in its own colour and named, the one the alignment goes by.
    /// </summary>
    void DrawRegion(ImDrawListPtr draw, int item, in PaneRect r, float scale)
    {
        if (item < 0) return;
        if (_region is { } region) Outline(region, ColorRegion, default, r);
        if (_alignRegion is { } alignRegion) Outline(alignRegion, ColorAlignRegion, "align by this"u8, r);

        void Outline(CanvasRect rect, uint color, ReadOnlySpan<byte> name, in PaneRect pane)
        {
            Affine2 toScreen = CanvasToScreen(item, pane);
            (double x0, double y0) = toScreen.Apply(rect.X, rect.Y);
            (double x1, double y1) = toScreen.Apply(rect.Right, rect.Bottom);
            Vector2 p0 = new(MathF.Round((float)x0), MathF.Round((float)y0)), p1 = new(MathF.Round((float)x1), MathF.Round((float)y1));
            draw.AddRect(p0 - Vector2.One, p1 + Vector2.One, ColorOutline, 0, 0, 3 * scale);
            draw.AddRect(p0, p1, color, 0, 0, scale);
            if (name.IsEmpty) return;
            // Its name inside the top left corner, on the dark outline's colour so that it reads on any picture.
            fixed (byte* text = name)
            {
                var at = new Vector2(p0.X + 4 * scale, p0.Y + 2 * scale);
                draw.AddText(at + Vector2.One, ColorOutline, text, text + name.Length);
                draw.AddText(at, color, text, text + name.Length);
            }
        }
    }

    // ---------------------------------------------------------------- pixel values in the cells

    /// <summary>
    /// Writes the stored values into the pixels of a pane once they are large enough on screen to
    /// hold them, and draws the grid between them. <paramref name="gridToScreen"/> places the
    /// pixel grid (the canvas, or the upright image) in the window; <paramref name="gridToFirst"/>
    /// and <paramref name="gridToSecond"/> lead from it to the stored pixels of the image, or of
    /// the two images whose values and difference are shown.
    /// </summary>
    void DrawPixelValues(ImDrawListPtr draw, in PaneRect r, in Affine2 gridToScreen, double gridWidth, double gridHeight,
        LoadedImage first, in Affine2 gridToFirst, LoadedImage? second, in Affine2 gridToSecond)
    {
        float cell = (float)gridToScreen.A;
        if (first.Layout is not { } layoutA) return;
        PixelLayout layoutB = second?.Layout ?? layoutA;
        bool pair = second != null, colour = layoutA is PixelLayout.Bgra8 or PixelLayout.Rgba16 || layoutB is PixelLayout.Bgra8 or PixelLayout.Rgba16;
        bool isFloat = layoutA == PixelLayout.GrayFloat || layoutB == PixelLayout.GrayFloat || (pair && CompareSource.DepthOf(layoutA) != CompareSource.DepthOf(layoutB));
        int digits = isFloat ? 7 : layoutA is PixelLayout.Gray16 or PixelLayout.Rgba16 || layoutB is PixelLayout.Gray16 or PixelLayout.Rgba16 ? 5 : 3;

        // What fits, in the text size of the rest of the UI: a pair shows first, second and
        // difference in rows, with a column per colour channel; one image shows its channels in
        // rows. Where that does not fit yet, a pair shows the difference alone, its channels in rows.
        // One text size only: it stays crisp, and the UI library has no glyphs of new sizes to
        // prepare while the user zooms.
        float digitWidth = ImGui.CalcTextSize("0"u8).X, lineHeight = ImGui.GetTextLineHeight();
        float columnNeeds = (digits + 1.6f) * digitWidth;
        bool Fits(int columnCount, int rowCount) => columnCount * columnNeeds <= cell * 0.96f && rowCount * lineHeight <= cell * 0.96f;
        int columns = pair && colour ? 3 : 1, rows = pair || colour ? 3 : 1;
        bool differenceOnly = false;
        if (!Fits(columns, rows))
        {
            if (!pair) return;
            differenceOnly = true;
            columns = 1;
            rows = colour ? 3 : 1;
            if (!Fits(columns, rows)) return;
        }

        // The pixels of the grid that are in the pane.
        Affine2 toGrid = gridToScreen.Inverse();
        (double gx0, double gy0) = toGrid.Apply(r.X, r.Y);
        (double gx1, double gy1) = toGrid.Apply(r.Right, r.Bottom);
        int left = (int)Math.Max(0, Math.Floor(gx0)), top = (int)Math.Max(0, Math.Floor(gy0));
        int right = (int)Math.Min(gridWidth, Math.Ceiling(gx1)), bottom = (int)Math.Min(gridHeight, Math.Ceiling(gy1));
        if (right <= left || bottom <= top || (long)(right - left) * (bottom - top) > 2500) return;

        for (int x = left; x <= right; x++)
        {
            float sx = MathF.Round((float)gridToScreen.Apply(x, 0).X);
            draw.AddLine(new Vector2(sx, (float)gridToScreen.Apply(0, top).Y), new Vector2(sx, (float)gridToScreen.Apply(0, bottom).Y), ColorGrid);
        }
        for (int y = top; y <= bottom; y++)
        {
            float sy = MathF.Round((float)gridToScreen.Apply(0, y).Y);
            draw.AddLine(new Vector2((float)gridToScreen.Apply(left, 0).X, sy), new Vector2((float)gridToScreen.Apply(right, 0).X, sy), ColorGrid);
        }

        float rowHeight = lineHeight, columnWidth = cell / columns, signWidth = ImGui.CalcTextSize("+"u8).X;
        // Light writing on dark pixels, dark on bright ones. What a difference or a similarity
        // looks like under the numbers depends on the colour map; there the writing stays light.
        ToneSettings tone = CurrentTone();
        bool mapped = pair && _compareMode is CompareMode.Difference or CompareMode.Ssim;
        float fullA = layoutA is PixelLayout.Gray16 or PixelLayout.Rgba16 ? 65535 : layoutA == PixelLayout.GrayFloat ? 1 : 255;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                (double ax, double ay) = gridToFirst.Apply(x + 0.5, y + 0.5);
                if (!first.TryReadPixel((int)Math.Floor(ax), (int)Math.Floor(ay), out PixelValue valueA)) continue;
                (double cx, double cy) = gridToScreen.Apply(x, y);
                float cellLeft = (float)cx, cellTop = (float)cy + (cell - rows * rowHeight) / 2;
                float shownLuma = layoutA is PixelLayout.Bgra8 or PixelLayout.Rgba16
                    ? (valueA.R * DifferenceMath.LumaWeights.X + valueA.G * DifferenceMath.LumaWeights.Y + valueA.B * DifferenceMath.LumaWeights.Z) / fullA
                    : valueA.R / fullA;
                bool onBright = !mapped && CompareExport.Tone(shownLuma, tone) > 0.55f;
                uint shadow = onBright ? 0xB0FFFFFF : 0xE0000000;
                uint inkValue = onBright ? InkValue : ColorValue, inkDelta = onBright ? InkDelta : ColorDelta;
                uint inkRed = onBright ? InkRed : ColorRed, inkGreen = onBright ? InkGreen : ColorGreen, inkBlue = onBright ? InkBlue : ColorBlue;

                if (!pair)
                {
                    if (colour)
                    {
                        Number(valueA.R, 0, 0, inkRed, false);
                        Number(valueA.G, 0, 1, inkGreen, false);
                        Number(valueA.B, 0, 2, inkBlue, false);
                    }
                    else
                    {
                        Number(valueA.R, 0, 0, inkValue, false);
                    }
                    continue;
                }

                (double bx, double by) = gridToSecond.Apply(x + 0.5, y + 0.5);
                if (!second!.TryReadPixel((int)Math.Floor(bx), (int)Math.Floor(by), out PixelValue valueB)) continue;
                PairValues(valueA, valueB, out Vector4 a, out Vector4 b, out _);
                for (int c = 0; c < (colour ? 3 : 1); c++)
                {
                    float va = c == 0 ? a.X : c == 1 ? a.Y : a.Z, vb = c == 0 ? b.X : c == 1 ? b.Y : b.Z;
                    uint color = !colour ? inkValue : c == 0 ? inkRed : c == 1 ? inkGreen : inkBlue;
                    if (differenceOnly)
                    {
                        Number(vb - va, 0, c, colour ? color : inkDelta, true);
                        continue;
                    }
                    Number(va, c, 0, color, false);
                    Number(vb, c, 1, color, false);
                    Number(vb - va, c, 2, colour ? color : inkDelta, true);
                }

                // One number, centred in its column, in its row, over a shadow that keeps it readable on any pixel.
                void Number(float value, int column, int row, uint color, bool sign)
                {
                    int n = isFloat
                        ? FormatNumber(value, _utf8.AsSpan(0, 31), sign)
                        : sign ? (Utf8.TryWrite(_utf8, $"{value:+0;-0;0}", out int a1) ? a1 : 0) : (Utf8.TryWrite(_utf8, $"{value:0}", out int a2) ? a2 : 0);
                    if (n == 0) return;
                    fixed (byte* p = _utf8)
                    {
                        // Digits are all as wide as each other: the width is counted, not measured, a thousand times a frame.
                        bool signed = p[0] is (byte)'+' or (byte)'-';
                        float width = (signed ? n - 1 : n) * digitWidth + (signed ? signWidth : 0);
                        var at = new Vector2(MathF.Round(cellLeft + column * columnWidth + (columnWidth - width) / 2), MathF.Round(cellTop + row * rowHeight));
                        draw.AddText(at + Vector2.One, shadow, p, p + n);
                        draw.AddText(at, color, p, p + n);
                    }
                }
            }
        }
    }

    /// <summary>The values in the pixels of one image shown on its own.</summary>
    void DrawSinglePixelValues()
    {
        if (!_pixelValues || Comparing || _shown is not { HasSize: true } image) return;
        var r = new PaneRect(ViewX, ViewY, ViewWidth, ViewHeight);
        Affine2 uprightToScreen = Affine2.ScaleTranslate(1, 1, ViewX, ViewY).After(_view.ToScreen(ViewWidth, ViewHeight));
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Bottom), true);
        DrawPixelValues(draw, r, uprightToScreen, image.UprightWidth, image.UprightHeight,
            image, image.Orientation.ToUpright(image.Width, image.Height).Inverse(), null, Affine2.Identity);
        draw.PopClipRect();
    }

    // ---------------------------------------------------------------- the metrics panel

    static ReadOnlySpan<byte> ChannelLabel(string name) => name switch
    {
        ChannelMetrics.Red => "Red"u8,
        ChannelMetrics.Green => "Green"u8,
        ChannelMetrics.Blue => "Blue"u8,
        ChannelMetrics.Alpha => "Alpha"u8,
        ChannelMetrics.Luma => "Luma"u8,
        ChannelMetrics.All => "All"u8,
        _ => "Value"u8,
    };

    void MetricCell(int column, double value, bool pending = false)
    {
        ImGui.TableSetColumnIndex(column);
        if (pending)
        {
            ImGui.TextDisabled("..."u8);
            return;
        }
        int n = FormatNumber(value, _utf8);
        Put(n);
    }

    void DrawMetricsPanel()
    {
        float scale = (float)_dpiScale;
        // Top left, under the first image's name and readout: the right edge is the colour bar's.
        ImGui.SetNextWindowPos(new Vector2(ViewX + 10 * scale, ViewY + 64 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Metrics"u8, ref _showMetrics, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }
        int n;
        (int a, int b) = _pick.Pair;
        if (a >= 0 && b >= 0 && a < _compare.Count && b < _compare.Count &&
            Utf8.TryWrite(_utf8, $"{b + 1}  {Path.GetFileName(_compare[b].Path.AsSpan())}   less   {a + 1}  {Path.GetFileName(_compare[a].Path.AsSpan())}", out n)) Put(n);

        MetricsResult? metrics = CurrentMetrics;
        ValueUnit unit = PairUnit(out float step);
        if (metrics != null)
        {
            CanvasRect area = metrics.Area;
            // Of a turned image not all of the rectangle is covered: then the number of pixels says how much is.
            bool all = metrics.Pixels + metrics.Ignored == area.Area;
            if (_region != null
                    ? all ? Utf8.TryWrite(_utf8, $"Region at x {area.X}, y {area.Y}: {area.Width} x {area.Height} pixels, {MetricsReport.UnitName(metrics.Unit)} values", out n)
                          : Utf8.TryWrite(_utf8, $"Region at x {area.X}, y {area.Y}: {metrics.Pixels} pixels of {area.Width} x {area.Height}, {MetricsReport.UnitName(metrics.Unit)} values", out n)
                    : all ? Utf8.TryWrite(_utf8, $"Everywhere both have pixels: {area.Width} x {area.Height}, {MetricsReport.UnitName(metrics.Unit)} values", out n)
                          : Utf8.TryWrite(_utf8, $"Everywhere both have pixels: {metrics.Pixels} pixels within {area.Width} x {area.Height}, {MetricsReport.UnitName(metrics.Unit)} values", out n))
            {
                fixed (byte* text = _utf8)
                {
                    _utf8[n] = 0;
                    TextDim(text);
                }
            }
            if (!metrics.PixelAligned) ImGui.TextDisabled("One image is scaled, turned or moved by part of a pixel to lie over the other: its pixels are blended."u8);
            if (metrics.Ignored > 0 && Utf8.TryWrite(_utf8, $"{metrics.Ignored} pixels left out: not a number\0", out _))
            {
                fixed (byte* text = _utf8) TextDim(text);
            }
        }
        if (ImGui.Button(_regionArmed ? "Drag a rectangle on the image..."u8 : "Region (R)"u8)) _regionArmed = !_regionArmed;
        if (_region != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Whole image"u8)) _region = null;
        }

        double threshold = _threshold / step; // in the units of the files
        if (metrics is { Channels.Count: > 0 })
        {
            if (ImGui.BeginTable("metrics"u8, 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn(""u8);
                ImGui.TableSetupColumn("MAE"u8);
                ImGui.TableSetupColumn("MSE"u8);
                ImGui.TableSetupColumn("PSNR, dB"u8);
                ImGui.TableSetupColumn("Max"u8);
                ImGui.TableSetupColumn("Over T, %"u8);
                ImGui.TableSetupColumn("SSIM"u8);
                ImGui.TableHeadersRow();
                ChannelMetrics? shown = ShownChannel(metrics);
                foreach (ChannelMetrics channel in metrics.Channels)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (channel == shown) ImGui.TextUnformatted(ChannelLabel(channel.Name));
                    else ImGui.TextDisabled(ChannelLabel(channel.Name));
                    MetricCell(1, channel.Mae);
                    MetricCell(2, channel.Mse);
                    MetricCell(3, channel.Psnr);
                    MetricCell(4, channel.MaxError);
                    MetricCell(5, 100 * channel.Histogram.FractionBeyond(threshold));
                    MetricCell(6, channel.Ssim, pending: !_metricsFinal && channel.Name != ChannelMetrics.Alpha);
                }
                ImGui.EndTable();
            }
            DrawHistogram(ShownChannel(metrics)!, threshold, scale);
        }
        else if (metrics != null)
        {
            ImGui.TextDisabled("The two images, or the region and they, have no pixels in common."u8);
        }
        else if (_metricsError != null)
        {
            Utf8Of(_metricsError);
            fixed (byte* text = _utf8) TextIn(new Vector4(1, 0.5f, 0.4f, 1), text);
        }
        else
        {
            ImGui.TextDisabled(TryMetricsKey(out _) ? "Computing..."u8 : "Waiting for both images to be loaded in full..."u8);
        }

        // The threshold, in the units of the files: what "over T" counts, and what the mask of the difference view marks.
        float edited = (float)threshold, most = unit is ValueUnit.Bits8 ? 64 : unit is ValueUnit.Bits16 ? 16384 : FullScale / 4;
        ImGui.SetNextItemWidth(240 * scale);
        if (ImGui.SliderFloat("threshold T"u8, ref edited, 0, most, unit is ValueUnit.Bits8 or ValueUnit.Bits16 ? "%.0f"u8 : "%.4g"u8))
            _threshold = Math.Clamp(unit is ValueUnit.Bits8 or ValueUnit.Bits16 ? MathF.Round(edited) * step : edited, 0, 1e30f);
        ImGui.SameLine();
        ImGui.Checkbox("Mark what is over it (W)"u8, ref _mask);

        if (ImGui.Button("Save the numbers... (Ctrl+Shift+S)"u8)) _deferred = SaveMetrics;
        if (metrics != null && _metricsFinal && Utf8.TryWrite(_utf8, $"errors {metrics.BasicMs:0} ms, similarity {metrics.SsimMs:0} ms\0", out _))
        {
            ImGui.SameLine();
            fixed (byte* text = _utf8) TextDim(text);
        }
        ImGui.End();
    }

    /// <summary>
    /// How often each difference of a channel occurs: bars on a logarithmic scale (the bar at no
    /// difference would dwarf all others), from the largest negative to the largest positive
    /// difference, with the threshold marked on both sides.
    /// </summary>
    void DrawHistogram(ChannelMetrics channel, double threshold, float scale)
    {
        DifferenceHistogram histogram = channel.Histogram;
        var size = new Vector2(MathF.Max(ImGui.GetContentRegionAvail().X, 420 * scale), 90 * scale);
        Vector2 origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xFF181818);

        // The range shown: as far as the largest difference, and at least a few steps.
        double reach = Math.Max(channel.MaxError, threshold * 1.25);
        if (histogram.Exact) reach = Math.Max(reach, 4); // whole steps: at least a few of them
        if (reach <= 0) reach = 1;
        int bars = Math.Max(16, (int)(size.X / MathF.Max(2, 2 * scale)));
        Span<double> sums = bars <= 512 ? stackalloc double[bars] : new double[bars];
        sums.Clear();
        double most = 0;
        for (int i = 0; i < histogram.Counts.Length; i++)
        {
            long count = histogram.Counts[i];
            if (count == 0) continue;
            int bar = (int)((histogram.ValueAt(i) + reach) / (2 * reach) * bars);
            bar = Math.Clamp(bar, 0, bars - 1);
            sums[bar] += count;
            most = Math.Max(most, sums[bar]);
        }
        if (most > 0)
        {
            double logMost = Math.Log(1 + most);
            float barWidth = size.X / bars;
            for (int i = 0; i < bars; i++)
            {
                if (sums[i] <= 0) continue;
                float height = (float)(Math.Log(1 + sums[i]) / logMost) * (size.Y - 2);
                draw.AddRectFilled(new Vector2(origin.X + i * barWidth, origin.Y + size.Y - MathF.Max(1, height)),
                    new Vector2(origin.X + MathF.Max((i + 1) * barWidth - 1, i * barWidth + 1), origin.Y + size.Y), 0xFFC8A064);
            }
        }
        float Across(double value) => origin.X + (float)((value + reach) / (2 * reach)) * size.X;
        draw.AddLine(new Vector2(Across(0), origin.Y), new Vector2(Across(0), origin.Y + size.Y), 0x60FFFFFF);
        if (threshold > 0 && threshold < reach)
        {
            draw.AddLine(new Vector2(Across(-threshold), origin.Y), new Vector2(Across(-threshold), origin.Y + size.Y), ColorRegion);
            draw.AddLine(new Vector2(Across(threshold), origin.Y), new Vector2(Across(threshold), origin.Y + size.Y), ColorRegion);
        }

        // Under it: the ends of the range, and what the picture is of.
        int n = FormatNumber(-reach, _utf8);
        Put(n);
        n = FormatNumber(reach, _utf8, sign: true);
        float width;
        fixed (byte* text = _utf8) width = ImGui.CalcTextSize(text, text + n).X;
        ImGui.SameLine(size.X - width);
        Put(n);
        ImGui.SameLine(size.X / 2 - 110 * scale);
        ImGui.TextDisabled("differences, 2nd less 1st (log scale)"u8);
    }

    // ---------------------------------------------------------------- the region

    /// <summary>
    /// The left button went down over a pane while a region is to be drawn: the rectangle starts
    /// here. With <paramref name="forAlign"/> it is the region the alignment goes by, otherwise
    /// the one the numbers are taken over. False if not over the canvas.
    /// </summary>
    bool BeginRegion(int x, int y, bool forAlign = false)
    {
        int p = PaneAt(x, y);
        if (p < 0 || _panes[p].First < 0 || _canvasWidth <= 0) return false;
        (_regionStartX, _regionStartY) = CanvasToScreen(_panes[p].First, _panes[p].Rect).Inverse().Apply(x, y);
        _dragRegion = true;
        _dragRegionForAlign = forAlign;
        _dragItem = _panes[p].First;
        if (forAlign)
        {
            _alignRegionArmed = false;
            _alignRegion = null;
            _alignReport = null; // what the last alignment said was about what it went by then
        }
        else
        {
            _regionArmed = false;
            _region = null;
        }
        return true;
    }

    void DragRegion(int x, int y)
    {
        int p = PaneAt(x, y);
        // The pane the drag began in decides, also when the mouse has left it.
        for (int i = 0; i < _paneCount && (p < 0 || _panes[p].First != _dragItem); i++)
            if (_panes[i].First == _dragItem) p = i;
        if (p < 0) return;
        (double cx, double cy) = CanvasToScreen(_panes[p].First, _panes[p].Rect).Inverse().Apply(x, y);
        int left = (int)Math.Floor(Math.Clamp(Math.Min(_regionStartX, cx), 0, _canvasWidth)), top = (int)Math.Floor(Math.Clamp(Math.Min(_regionStartY, cy), 0, _canvasHeight));
        int right = (int)Math.Ceiling(Math.Clamp(Math.Max(_regionStartX, cx), 0, _canvasWidth)), bottom = (int)Math.Ceiling(Math.Clamp(Math.Max(_regionStartY, cy), 0, _canvasHeight));
        CanvasRect dragged = CanvasRect.FromEdges(left, top, right, bottom);
        if (_dragRegionForAlign) _alignRegion = dragged;
        else _region = dragged;
    }

    /// <summary>The drag ended. A rectangle of less than two pixels either way was a click: the whole image again.</summary>
    void EndRegion()
    {
        if (!_dragRegion) return;
        _dragRegion = false;
        if (_dragRegionForAlign)
        {
            if (_alignRegion is { } region && (region.Width < 2 || region.Height < 2)) _alignRegion = null;
        }
        else if (_region is { } region && (region.Width < 2 || region.Height < 2))
        {
            _region = null;
        }
    }

    // ---------------------------------------------------------------- keys

    /// <summary>The keys of the difference view and the metrics. True if the key was one of them.</summary>
    bool DifferenceKey(VIRTUAL_KEY key, bool shift)
    {
        const int kinds = (int)DifferenceKind.Relative + 1;
        switch (key)
        {
            case VIRTUAL_KEY.VK_E:
                // To the difference view; in it, through the kinds of difference.
                if (_compareMode != CompareMode.Difference) SetCompareMode(CompareMode.Difference);
                else _differenceKind = (DifferenceKind)(((int)_differenceKind + (shift ? kinds - 1 : 1)) % kinds);
                return true;
            case VIRTUAL_KEY.VK_G when _compareMode is CompareMode.Difference or CompareMode.Ssim:
                SetCurrentMap((Colormap)(((int)CurrentMap + (shift ? Colormaps.Count - 1 : 1)) % Colormaps.Count));
                Notice("Colours: " + CurrentMap.DisplayName());
                return true;
            case VIRTUAL_KEY.VK_W:
                _mask = !_mask;
                if (_mask && _compareMode != CompareMode.Difference) SetCompareMode(CompareMode.Difference);
                return true;
            case VIRTUAL_KEY.VK_OEM_COMMA or VIRTUAL_KEY.VK_OEM_PERIOD:
                StepThreshold(key == VIRTUAL_KEY.VK_OEM_PERIOD ? 1 : -1);
                return true;
            case VIRTUAL_KEY.VK_N:
                _showMetrics = !_showMetrics;
                return true;
            case VIRTUAL_KEY.VK_R:
                if (shift)
                {
                    _region = null;
                    _regionArmed = false;
                    Notice("The numbers are of the whole image again.");
                }
                else
                {
                    _regionArmed = !_regionArmed;
                    if (_regionArmed) Notice("Drag a rectangle on the image: the numbers are taken there.");
                }
                return true;
        }
        return false;
    }

    /// <summary>One step of the threshold: a level at a time while it is small, a quarter more or less from 16 levels on.</summary>
    void StepThreshold(int direction)
    {
        ValueUnit unit = PairUnit(out float step);
        float smallest = unit is ValueUnit.Bits8 or ValueUnit.Bits16 ? step : FullScale / 1024;
        float levels = _threshold / smallest;
        levels = levels < 16 || (levels == 16 && direction < 0) ? levels + direction : direction > 0 ? levels * 1.25f : levels / 1.25f;
        if (unit is ValueUnit.Bits8 or ValueUnit.Bits16) levels = MathF.Round(levels);
        _threshold = MathF.Max(0, levels) * smallest;
        Span<byte> text = stackalloc byte[32];
        int n = FormatNumber(_threshold / step, text);
        Notice("Threshold " + System.Text.Encoding.UTF8.GetString(text[..n]));
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Runs a file export on the calling (worker) thread and reports on the UI thread what
    /// became of it. Nothing an export throws may bring the viewer down.
    /// </summary>
    void Export(string name, Action write)
    {
        long started = Stopwatch.GetTimestamp();
        string message;
        try
        {
            write();
            message = $"Saved {name} ({Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s).";
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            message = $"{name} could not be saved: {(e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message)}";
        }
        RunOnUi(() => Notice(message));
    }

    /// <summary>
    /// Asks where to save and then calls <paramref name="save"/> with the path on a worker
    /// thread. In test runs (--export-dir) there is no dialog: the file goes into that folder
    /// under <paramref name="testName"/>. If the dialog is cancelled, <paramref name="abandon"/>
    /// is called instead, to let go of what was held for the save.
    /// </summary>
    void SaveTo(string suggestedName, string testName, (string Name, string Extension)[] types, Action<string> save, Action abandon)
    {
        if (_options.ExportDir is { } folder)
        {
            string path = Path.Combine(folder, testName);
            Task.Run(() => save(path));
            return;
        }
        FileActions.SaveAsync(_hwnd, suggestedName, types, path =>
        {
            if (path != null) save(path); // on the dialog's thread, which has nothing else to do
            else abandon();
        });
    }

    /// <summary>"first_vs_second_what" from the two files' names.</summary>
    string PairName(string what)
    {
        (int a, int b) = _pick.Pair;
        return $"{Path.GetFileNameWithoutExtension(_compare[a].Path)}_vs_{Path.GetFileNameWithoutExtension(_compare[b].Path)}_{what}";
    }

    /// <summary>
    /// Saves what the two-image view shows, computed again from the full images at the full
    /// resolution of the canvas: the difference in its colours, the similarity map, or the
    /// split, blend or checkerboard as set.
    /// </summary>
    void SaveView()
    {
        if (!Comparing || !_compareMode.IsOverlay())
        {
            Notice("Saving works in the views that combine two images: split, blend, checkerboard, difference, SSIM.");
            return;
        }
        if (!TryMetricsKey(out MetricsKey key))
        {
            Notice("Both images have to be loaded in full first.");
            return;
        }
        LayoutCompare();
        if (_paneCount == 0) return;
        Pane pane = _panes[0];
        int width = (int)_canvasWidth, height = (int)_canvasHeight;
        CompareMode mode = _compareMode;
        ToneSettings tone = CurrentTone();
        DifferenceView view = CurrentDifferenceView();
        Colormap colors = CurrentMap;
        float similarityGain = _similarityGain, opacity = _opacity;

        // The divider and the cells are where the screen has them, in canvas coordinates.
        Affine2 paneToCanvas = CanvasToPane(pane.First, pane.Rect).Inverse();
        (double dividerX, double dividerY) = paneToCanvas.Apply(DividerAt(pane.Rect), DividerAt(pane.Rect));
        (double originX, double originY) = paneToCanvas.Apply(pane.Rect.Width / 2.0, pane.Rect.Height / 2.0);
        float cellSize = (float)(_checkerSize / ViewOf(pane.First).Zoom);
        bool splitRows = _splitRows;

        DecodedImage? similarity = null;
        CanvasRect similarityArea = _similarityArea;
        if (mode == CompareMode.Ssim)
        {
            if (!SimilarityReady || _similarityMap == null)
            {
                Notice("The similarity is still being computed.");
                return;
            }
            similarity = _similarityMap;
            similarity.AddReference();
        }
        key.First.AddReference();
        key.Second.AddReference();
        string what = mode switch
        {
            CompareMode.Difference => "difference",
            CompareMode.Ssim => "ssim",
            CompareMode.Blend => "blend",
            CompareMode.Checker => "checkerboard",
            _ => "split",
        };
        void Release()
        {
            key.First.Dispose();
            key.Second.Dispose();
            similarity?.Dispose();
        }
        if (_options.ExportDir != null) Notice("Saving...");
        SaveTo(PairName(what) + ".png", "view.png", [("PNG image", "png")], path => Export(Path.GetFileName(path), () =>
        {
            try
            {
                var first = new CompareSource(key.First, key.FirstToCanvas);
                var second = new CompareSource(key.Second, key.SecondToCanvas);
                using DecodedImage image = mode switch
                {
                    CompareMode.Difference => CompareExport.Difference(first, second, width, height, view, tone),
                    CompareMode.Ssim => CompareExport.Heat(similarity!, similarityArea, width, height, colors, similarityGain),
                    CompareMode.Blend => CompareExport.Mix(first, second, width, height, MixExport.Blend, opacity, 0, 0, tone),
                    CompareMode.Checker => CompareExport.Mix(first, second, width, height, MixExport.Checker, cellSize, (float)originX, (float)originY, tone),
                    _ => CompareExport.Mix(first, second, width, height, splitRows ? MixExport.SplitRows : MixExport.SplitColumns,
                        (float)(splitRows ? dividerY : dividerX), 0, 0, tone),
                };
                ImageWriter.WritePng(path, image, alpha: true);
            }
            finally
            {
                Release();
            }
        }), Release);
    }

    /// <summary>Saves the difference itself, second less first, in the units of the files, as 32-bit float TIFF: for other programs to work on.</summary>
    void SaveDifferenceValues()
    {
        if (!TryMetricsKey(out MetricsKey key))
        {
            Notice(Comparing ? "Both images have to be loaded in full first." : "There is no comparison to save the difference of.");
            return;
        }
        int width = (int)_canvasWidth, height = (int)_canvasHeight, channel = (int)_display.Channel;
        if ((long)width * height * sizeof(float) > uint.MaxValue - 4096)
        {
            Notice("The difference is too large for a TIFF file (4 GB).");
            return;
        }
        key.First.AddReference();
        key.Second.AddReference();
        void Release()
        {
            key.First.Dispose();
            key.Second.Dispose();
        }
        SaveTo(PairName("difference") + ".tif", "difference.tif", [("TIFF image, 32-bit float", "tif")], path => Export(Path.GetFileName(path), () =>
        {
            try
            {
                using DecodedImage values = CompareExport.DifferenceValues(new CompareSource(key.First, key.FirstToCanvas), new CompareSource(key.Second, key.SecondToCanvas), width, height, channel);
                ImageWriter.WriteTiffFloat(path, values);
            }
            finally
            {
                Release();
            }
        }), Release);
    }

    /// <summary>Saves the numbers as CSV or JSON, whichever the file is named as. They are computed first if they are not there yet.</summary>
    void SaveMetrics()
    {
        if (!Comparing)
        {
            Notice("There is no comparison to save the numbers of.");
            return;
        }
        if (CurrentMetrics is not { } metrics || !_metricsFinal)
        {
            _showMetrics = true; // which starts them
            Notice("The numbers are still being computed; save again when the panel shows them.");
            return;
        }
        (int a, int b) = _pick.Pair;
        string first = _compare[a].Path, second = _compare[b].Path;
        PairUnit(out float step);
        double threshold = _threshold / step;
        if (_options.ExportDir is { } folder)
        {
            // Tests get both forms.
            Task.Run(() => Export("metrics.csv and metrics.json", () =>
            {
                File.WriteAllText(Path.Combine(folder, "metrics.csv"), MetricsReport.Csv(metrics, first, second, threshold));
                File.WriteAllText(Path.Combine(folder, "metrics.json"), MetricsReport.Json(metrics, first, second, threshold));
            }));
            return;
        }
        FileActions.SaveAsync(_hwnd, PairName("metrics") + ".csv", [("CSV table", "csv"), ("JSON", "json")], path =>
        {
            if (path == null) return;
            bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            Export(Path.GetFileName(path), () => File.WriteAllText(path, json ? MetricsReport.Json(metrics, first, second, threshold) : MetricsReport.Csv(metrics, first, second, threshold)));
        });
    }
}
