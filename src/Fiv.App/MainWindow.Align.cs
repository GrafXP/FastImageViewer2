using System.Diagnostics;
using System.Numerics;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Hexa.NET.ImGui;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Fiv.App;

// Comparing, third part: aligning. Every image of a set has a pose: how far it is moved, turned
// and scaled from where its placement puts it on the canvas. The pose is part of the image's
// transform to the canvas, so everything that goes through the canvas follows it: the panes,
// the modes that combine two images, the linked cursor, the numbers and the saved files.
//
// What is aligned is the second image of the pair on show, to the first. By hand: in align
// mode a drag moves the image instead of the view, and the arrow keys and their neighbours
// nudge, turn and scale it. Automatically: Fiv.Comparing.Registration works out the pose on a
// worker, from the full-resolution pixels, and it is taken over if it does not make the two
// images less alike.
internal sealed unsafe partial class MainWindow
{
    /// <summary>What changed a pose last. Changes of one kind in a row are undone together.</summary>
    enum AlignAction { None, Nudge, Edit, Drag, Automatic, Reset }

    bool _alignMode;
    AlignModel _alignModel = AlignModel.ShiftRotationScale;

    bool _dragAlign;
    bool _dragAlignTurns;            // the drag turns the image (Shift was held when it began) instead of moving it
    AlignPose _alignDragStart;
    double _alignDragX, _alignDragY; // how far the mouse has gone since the drag began, in screen pixels
    double _alignDragTurn;           // how far it has gone round the image's centre since then, in degrees

    CompareItem? _alignUndoItem;
    AlignPose _alignUndoPose;
    AlignAction _alignLastAction;

    int _alignJob;
    bool _alignRunning;
    CancellationTokenSource? _alignCancel;
    string? _alignReport; // what the last automatic alignment did, for the panel

    /// <summary>
    /// The canvas rectangle the automatic alignment goes by, and "how alike" is taken over;
    /// null: everything the two images show. Its own, not the region of the numbers: one may
    /// well align by one part of a picture and measure another.
    /// </summary>
    CanvasRect? _alignRegion;
    bool _alignRegionArmed; // the next drag on the image draws it

    /// <summary>What a score is of: the two images' pixels, where they lie on the canvas, and the region.</summary>
    readonly record struct ScoreKey(DecodedImage First, DecodedImage Second, Affine2 FirstToCanvas, Affine2 SecondToCanvas, CanvasRect? Region);

    double _alignScore = double.NaN;
    ScoreKey _alignScoreKey;
    bool _alignScoreKnown, _alignScoreRunning;

    // The brightness of the compared images at reduced sizes, kept while they are in the set:
    // made once per image, some 10 ms and a third of the image's size in memory.
    readonly Dictionary<DecodedImage, LumaPyramid> _lumas = [];

    /// <summary>The image that is aligned and the one it is aligned to: the second and the first of the pair on show.</summary>
    bool TryGetAlignPair(out int reference, out int moving)
    {
        (reference, moving) = Comparing ? _pick.Pair : (-1, -1);
        return reference != moving && reference >= 0 && moving >= 0 && reference < _compare.Count && moving < _compare.Count &&
               _compare[reference].Image is { HasSize: true } && _compare[moving].Image is { HasSize: true } && _canvasWidth > 0;
    }

    void SetAlignMode(bool on)
    {
        if (on == _alignMode || (on && !Comparing)) return;
        _alignMode = on;
        _dragAlign = false;
        if (!on) _alignRegionArmed = false; // the region itself stays: Shift+A goes by it with the panel closed too, and it is drawn
        if (on && TryGetAlignPair(out int reference, out int moving))
            Notice($"Aligning image {moving + 1} to image {reference + 1}: drag it (with Shift: turn it), nudge it with the arrow keys, or press Shift+A.");
    }

    /// <summary>Forgets everything about aligning: the comparison ended or another began. The poses go with the set.</summary>
    void ResetAlign()
    {
        _alignCancel?.Cancel();
        _alignCancel = null;
        _alignJob++;
        _alignRunning = false;
        _alignMode = _dragAlign = false;
        _alignReport = null;
        _alignScore = double.NaN;
        _alignScoreKnown = false;
        _alignUndoItem = null;
        _alignLastAction = AlignAction.None;
        _alignRegion = null;
        _alignRegionArmed = false;
        foreach (LumaPyramid luma in _lumas.Values) luma.Dispose();
        _lumas.Clear();
    }

    // ---------------------------------------------------------------- the pose

    /// <summary>A pose as it is kept: to a thousandth of a pixel, a ten-thousandth of a degree and a millionth in size, the turn within half a turn either way.</summary>
    static AlignPose Tidy(AlignPose pose)
    {
        double rotation = Math.IEEERemainder(pose.Rotation, 360);
        if (rotation <= -180) rotation += 360;
        double scale = Math.Clamp(double.IsFinite(pose.Scale) ? pose.Scale : 1, 0.05, 20);
        return new AlignPose(Whole(pose.Dx, 1000), Whole(pose.Dy, 1000), Whole(rotation, 10000), Whole(scale, 1000000));

        static double Whole(double value, double per) => double.IsFinite(value) ? Math.Round(value * per) / per + 0.0 : 0;
    }

    /// <summary>Gives an image of the set a pose, and remembers the one it had for "undo" unless this change continues the last one.</summary>
    void SetPose(int item, AlignPose pose, AlignAction action)
    {
        CompareItem entry = _compare[item];
        pose = Tidy(pose);
        if (pose == entry.Pose) return;
        if (action != _alignLastAction || _alignUndoItem != entry)
        {
            _alignUndoItem = entry;
            _alignUndoPose = entry.Pose;
        }
        _alignLastAction = action;
        entry.Pose = pose;
        if (action != AlignAction.Automatic) _alignReport = null; // what the last automatic alignment said is no longer about this pose
        Invalidate();
    }

    void Nudge(double dx, double dy)
    {
        if (!TryGetAlignPair(out _, out int moving)) return;
        AlignPose pose = _compare[moving].Pose;
        SetPose(moving, pose.With(dx: pose.Dx + dx, dy: pose.Dy + dy), AlignAction.Nudge);
    }

    void Turn(double degrees)
    {
        if (!TryGetAlignPair(out _, out int moving)) return;
        AlignPose pose = _compare[moving].Pose;
        SetPose(moving, pose.With(rotation: pose.Rotation + degrees), AlignAction.Nudge);
    }

    /// <summary>Makes the image larger or smaller by a fraction of its original size.</summary>
    void Rescale(double by)
    {
        if (!TryGetAlignPair(out _, out int moving)) return;
        AlignPose pose = _compare[moving].Pose;
        SetPose(moving, pose.With(scale: pose.Scale + by), AlignAction.Nudge);
    }

    void ResetPose()
    {
        if (!TryGetAlignPair(out _, out int moving)) return;
        _alignLastAction = AlignAction.None;
        SetPose(moving, AlignPose.Identity, AlignAction.Reset);
    }

    /// <summary>Back to the pose before the last change, and, pressed again, forward to it.</summary>
    void UndoPose()
    {
        if (_alignUndoItem is not { } entry || !_compare.Contains(entry)) return;
        (entry.Pose, _alignUndoPose) = (_alignUndoPose, entry.Pose);
        _alignLastAction = AlignAction.None;
        Invalidate();
    }

    /// <summary>Writes "x 23.6  y -14.2  0.75°  100.4 %" (turn and size only if there are any) into <paramref name="to"/>; returns the bytes written.</summary>
    static int WritePose(AlignPose pose, Span<byte> to)
    {
        if (!Utf8.TryWrite(to, $"x {pose.Dx:0.###}  y {pose.Dy:0.###}", out int n)) return 0;
        if (pose.Rotation != 0 && Utf8.TryWrite(to[n..], $"  {pose.Rotation:0.####}°", out int more)) n += more;
        if (pose.Scale != 1 && Utf8.TryWrite(to[n..], $"  {pose.Scale * 100:0.####} %", out more)) n += more;
        return n;
    }

    // ---------------------------------------------------------------- input

    /// <summary>The keys of aligning. True if the key was one of them.</summary>
    bool AlignKey(VIRTUAL_KEY key, bool shift)
    {
        switch (key)
        {
            case VIRTUAL_KEY.VK_A:
                if (shift) AutoAlign(fine: false);
                else SetAlignMode(!_alignMode);
                return true;
            case VIRTUAL_KEY.VK_Q:
                AutoAlign(fine: true);
                return true;
        }
        if (!_alignMode) return false;
        // A pixel, a tenth of a degree, a tenth of a per cent; with Shift a tenth of that.
        double fine = shift ? 0.1 : 1;
        switch (key)
        {
            case VIRTUAL_KEY.VK_LEFT:
                Nudge(-fine, 0);
                return true;
            case VIRTUAL_KEY.VK_RIGHT:
                Nudge(fine, 0);
                return true;
            case VIRTUAL_KEY.VK_UP:
                Nudge(0, -fine);
                return true;
            case VIRTUAL_KEY.VK_DOWN:
                Nudge(0, fine);
                return true;
            case VIRTUAL_KEY.VK_PRIOR:
                Turn(-0.1 * fine);
                return true;
            case VIRTUAL_KEY.VK_NEXT:
                Turn(0.1 * fine);
                return true;
            case VIRTUAL_KEY.VK_HOME:
                Rescale(-0.001 * fine);
                return true;
            case VIRTUAL_KEY.VK_END:
                Rescale(0.001 * fine);
                return true;
            case VIRTUAL_KEY.VK_BACK:
                ResetPose();
                return true;
            case VIRTUAL_KEY.VK_ESCAPE:
                SetAlignMode(false);
                return true;
        }
        return false;
    }

    /// <summary>
    /// The left button went down over a pane while aligning: the drag moves the image, or with
    /// <paramref name="turn"/> (Shift held) turns it about its centre. False if there is nothing to move.
    /// </summary>
    bool BeginAlignDrag(bool turn)
    {
        if (!TryGetAlignPair(out _, out int moving)) return false;
        _dragAlign = true;
        _dragAlignTurns = turn;
        _alignDragStart = _compare[moving].Pose;
        _alignDragX = _alignDragY = _alignDragTurn = 0;
        _alignLastAction = AlignAction.None; // each drag can be undone on its own
        return true;
    }

    /// <summary>The mouse, at (<paramref name="x"/>, <paramref name="y"/>) now, went this far in a drag that moves or turns the image.</summary>
    void AlignDrag(int x, int y, int dx, int dy)
    {
        if (!TryGetAlignPair(out _, out int moving)) return;
        if (_dragAlignTurns)
        {
            // The image goes round its centre by the angle the mouse goes round it: what was
            // grabbed stays under the mouse, and the further out it is grabbed, the finer the turn.
            if (!TryGetAlignCentreOnScreen(moving, out double centreX, out double centreY)) return;
            double fromX = x - dx - centreX, fromY = y - dy - centreY, toX = x - centreX, toY = y - centreY;
            if (fromX * fromX + fromY * fromY < 64 || toX * toX + toY * toY < 64) return; // at the centre itself there is no angle to speak of
            // Screen coordinates have y pointing down, so this angle is positive clockwise, as the pose's turn is.
            _alignDragTurn += Math.Atan2(fromX * toY - fromY * toX, fromX * toX + fromY * toY) * (180 / Math.PI);
            SetPose(moving, _alignDragStart.With(rotation: _alignDragStart.Rotation + _alignDragTurn), AlignAction.Drag);
            return;
        }
        _alignDragX += dx;
        _alignDragY += dy;
        double zoom = ViewOf(_dragItem).Zoom, moveX = _alignDragX / zoom, moveY = _alignDragY / zoom;
        // Zoomed out, a screen pixel is several image pixels: the image moves by whole pixels,
        // and what it was moved by a fraction before stays. Fractions are for when one can see them.
        if (zoom <= 1)
        {
            moveX = Math.Round(moveX);
            moveY = Math.Round(moveY);
        }
        SetPose(moving, _alignDragStart.With(dx: _alignDragStart.Dx + moveX, dy: _alignDragStart.Dy + moveY), AlignAction.Drag);
    }

    /// <summary>
    /// Where the centre of the image being aligned is in the window, in the pane the drag is in:
    /// the point it turns about. The canvas is the same in every pane, so any pane will do.
    /// </summary>
    bool TryGetAlignCentreOnScreen(int moving, out double x, out double y)
    {
        x = y = 0;
        int pane = -1;
        for (int p = 0; p < _paneCount && pane < 0; p++)
            if (_panes[p].First == _dragItem) pane = p;
        if (pane < 0 || _compare[moving].Image is not { HasSize: true } image) return false;
        (double centreX, double centreY) = PoseCentre(image);
        AlignPose pose = _compare[moving].Pose;
        (x, y) = CanvasToScreen(_dragItem, _panes[pane].Rect).Apply(centreX + pose.Dx, centreY + pose.Dy);
        return true;
    }

    /// <summary>
    /// The wheel while aligning: with Shift it turns the image, as a drag with Shift does (a
    /// tenth of a degree a notch, with Ctrl as well a hundredth); with Ctrl alone it scales it.
    /// False if it is the view's.
    /// </summary>
    bool AlignWheel(int delta, bool control, bool shift)
    {
        if (!_alignMode || !(control || shift)) return false;
        double notches = delta / 120.0;
        if (shift) Turn(notches * (control ? 0.01 : 0.1));
        else Rescale(notches * 0.001);
        return true;
    }

    // ---------------------------------------------------------------- the worker's share

    /// <summary>What a worker needs of the pair, held so that nothing of it goes away under it.</summary>
    sealed class AlignWork
    {
        public required DecodedImage First, Second;
        public Affine2 FirstToCanvas, SecondToCanvas, SecondPlaced;
        public LumaPyramid? FirstLuma, SecondLuma;
        public bool BuiltFirst, BuiltSecond;

        /// <summary>On the worker: makes what the cache did not have.</summary>
        public void Prepare()
        {
            if (FirstLuma == null) (FirstLuma, BuiltFirst) = (new LumaPyramid(First), true);
            if (SecondLuma == null) (SecondLuma, BuiltSecond) = (new LumaPyramid(Second), true);
        }
    }

    /// <summary>Takes hold of the pair's pixels and of what is cached for them. False if they are not both loaded in full.</summary>
    bool TryBeginAlignWork(out AlignWork work, out int moving)
    {
        work = null!;
        if (!TryGetAlignPair(out int reference, out moving)) return false;
        LoadedImage first = _compare[reference].Image!, second = _compare[moving].Image!;
        if (first.Full is not { } a || second.Full is not { } b) return false;
        a.AddReference();
        b.AddReference();
        work = new AlignWork
        {
            First = a,
            Second = b,
            FirstToCanvas = ImageToCanvas(reference),
            SecondToCanvas = ImageToCanvas(moving),
            SecondPlaced = PlacedOnCanvas(second),
        };
        if (_lumas.TryGetValue(a, out LumaPyramid? lumaA))
        {
            lumaA.AddReference();
            work.FirstLuma = lumaA;
        }
        if (_lumas.TryGetValue(b, out LumaPyramid? lumaB))
        {
            lumaB.AddReference();
            work.SecondLuma = lumaB;
        }
        return true;
    }

    /// <summary>Back on the UI thread: what the worker made goes into the cache, if its image is still compared; the rest is let go of.</summary>
    void EndAlignWork(AlignWork work)
    {
        Keep(work.First, work.FirstLuma, work.BuiltFirst);
        Keep(work.Second, work.SecondLuma, work.BuiltSecond);
        work.First.Dispose();
        work.Second.Dispose();

        void Keep(DecodedImage image, LumaPyramid? luma, bool built)
        {
            if (luma == null) return;
            if (built && !_lumas.ContainsKey(image) && _compare.Exists(item => item.Image?.Full == image)) _lumas[image] = luma;
            else luma.Dispose();
        }
    }

    /// <summary>Lets go of the cached brightness of images that are no longer compared.</summary>
    void PruneLumas()
    {
        List<DecodedImage>? gone = null;
        foreach (DecodedImage image in _lumas.Keys)
            if (!_compare.Exists(item => item.Image?.Full == image)) (gone ??= []).Add(image);
        if (gone == null) return;
        foreach (DecodedImage image in gone)
        {
            _lumas[image].Dispose();
            _lumas.Remove(image);
        }
    }

    // ---------------------------------------------------------------- how alike, as the pose is now

    /// <summary>Starts the score for the pair as it lies now, if the panel is open and it is neither there nor on its way. Cheap; called every frame.</summary>
    void UpdateAlignScore()
    {
        if (!_alignMode || _alignScoreRunning || _alignRunning) return;
        if (!TryGetAlignPair(out int reference, out int moving)) return;
        if (_compare[reference].Image!.Full is not { } a || _compare[moving].Image!.Full is not { } b) return;
        if (_dragRegion && _dragRegionForAlign) return; // the region is being drawn: the score follows when it is there
        var key = new ScoreKey(a, b, ImageToCanvas(reference), ImageToCanvas(moving), _alignRegion);
        if (_alignScoreKnown && key == _alignScoreKey) return;
        PruneLumas();
        if (!TryBeginAlignWork(out AlignWork work, out _)) return;
        _alignScoreRunning = true;
        Task.Run(() =>
        {
            double score = double.NaN;
            try
            {
                work.Prepare();
                score = Registration.Score(new AlignInput(work.FirstLuma!, work.FirstToCanvas), new AlignInput(work.SecondLuma!, work.SecondToCanvas), key.Region);
            }
            catch (Exception e)
            {
                CrashLog.Write(e); // out of memory, most likely: the viewer goes on without the score
            }
            RunOnUi(() =>
            {
                EndAlignWork(work);
                _alignScoreRunning = false;
                _alignScore = score;
                _alignScoreKey = key;
                _alignScoreKnown = true;
            });
        });
    }

    // ---------------------------------------------------------------- automatic alignment

    /// <summary>
    /// Works out the pose of the second image of the pair: from scratch, or with
    /// <paramref name="fine"/> from the pose it has. The result arrives in <see cref="OnAligned"/>.
    /// </summary>
    void AutoAlign(bool fine)
    {
        if (!TryGetAlignPair(out _, out _))
        {
            Notice("Aligning takes two images on show.");
            return;
        }
        PruneLumas();
        if (!TryBeginAlignWork(out AlignWork work, out int moving))
        {
            Notice("Both images have to be loaded in full first.");
            return;
        }
        _alignCancel?.Cancel();
        var cancel = _alignCancel = new CancellationTokenSource();
        int job = ++_alignJob;
        _alignRunning = true;
        _alignReport = fine ? "Refining..." : "Aligning...";
        CompareItem entry = _compare[moving];
        AlignPose start = entry.Pose;
        AlignModel model = _alignModel;
        CanvasRect? region = _alignRegion;
        Task.Run(() =>
        {
            AlignResult? result = null;
            string? error = null;
            try
            {
                work.Prepare();
                result = Registration.Align(new AlignInput(work.FirstLuma!, work.FirstToCanvas), new AlignInput(work.SecondLuma!, work.SecondPlaced), start, model, fine, cancel.Token, region);
            }
            catch (Exception e)
            {
                CrashLog.Write(e);
                error = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
            }
            RunOnUi(() => OnAligned(job, work, entry, start, result, error, fine, model, region));
        });
    }

    /// <summary>
    /// A worker has a pose. It is taken over unless it makes the two images less alike than they
    /// were: in the region the alignment went by, if there was one. UI thread.
    /// </summary>
    void OnAligned(int job, AlignWork work, CompareItem entry, AlignPose start, AlignResult? result, string? error, bool fine, AlignModel model, CanvasRect? region)
    {
        EndAlignWork(work);
        if (job != _alignJob) return; // of a comparison that has ended, or overtaken by another run
        _alignRunning = false;
        int item = _compare.IndexOf(entry);
        if (error != null) _alignReport = "The alignment failed: " + error;
        else if (result == null || item < 0) _alignReport = null; // cancelled, or the image has left the set
        else if (entry.Pose != start) _alignReport = "The image was moved while its alignment was worked out: left as it is.";
        else
        {
            bool better = result.Found && !double.IsNaN(result.ScoreAfter) && !(result.ScoreAfter < result.ScoreBefore - 0.0005);
            // With a region, the scores are of the region: the report says so.
            string where = region != null ? " in the region" : "", by = region != null ? " by the region" : "";
            if (!better)
            {
                _alignReport = double.IsNaN(result.ScoreAfter) || !result.Found
                    ? region != null ? "No alignment found: the region is too small, or the two images have too little in common there."
                                     : "No alignment found: the two images have too little in common."
                    : $"No better alignment found: alike{where} {result.ScoreBefore:0.0000} as it is, {result.ScoreAfter:0.0000} at best.";
            }
            else if (Tidy(result.Pose) == start)
            {
                _alignReport = $"Nothing to improve: alike{where} {result.ScoreAfter:0.0000} ({result.Milliseconds:0} ms).";
            }
            else
            {
                _alignLastAction = AlignAction.None;
                SetPose(item, result.Pose, AlignAction.Automatic);
                _alignReport = double.IsNaN(result.ScoreBefore)
                    ? $"Aligned{by} in {result.Milliseconds:0} ms: alike{where} {result.ScoreAfter:0.0000}."
                    : $"Aligned{by} in {result.Milliseconds:0} ms: alike{where} {result.ScoreBefore:0.0000} before, {result.ScoreAfter:0.0000} after.";
            }
        }
        if (_alignReport != null) Notice(_alignReport);
        if (result != null && _options.PerfLog != null)
        {
            try
            {
                AlignPose pose = item >= 0 ? entry.Pose : result.Pose;
                File.AppendAllText(_options.PerfLog,
                    $"align fine={(fine ? 1 : 0)} model={model} ms={result.Milliseconds:F1} found={(result.Found ? 1 : 0)} patches={result.Patches} " +
                    $"before={result.ScoreBefore:F5} after={result.ScoreAfter:F5} dx={pose.Dx:F3} dy={pose.Dy:F3} rotation={pose.Rotation:F4} scale={pose.Scale:F6} " +
                    $"region={(region is { } r ? $"{r.X},{r.Y},{r.Width},{r.Height}" : "none")} at={Stopwatch.GetTimestamp()}\n");
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- the panel

    void DrawAlignPanel()
    {
        float scale = (float)_dpiScale;
        // Top right, under the second image's name: it is that image the panel moves.
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth - 10 * scale, ViewY + 64 * scale), ImGuiCond.FirstUseEver, new Vector2(1, 0));
        bool open = true;
        if (!ImGui.Begin("Align"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            if (!open) SetAlignMode(false);
            return;
        }
        if (!open) _deferred = () => SetAlignMode(false);
        if (!TryGetAlignPair(out int reference, out int moving))
        {
            ImGui.TextDisabled("Aligning takes two images on show."u8);
            ImGui.End();
            return;
        }
        if (Utf8.TryWrite(_utf8, $"{moving + 1}  {Path.GetFileName(_compare[moving].Path.AsSpan())}   is laid on   {reference + 1}  {Path.GetFileName(_compare[reference].Path.AsSpan())}", out int n)) Put(n);
        ImGui.TextDisabled("Drag the image to move it, drag with Shift to turn it about its centre."u8);
        ImGui.TextDisabled("Arrows 1 pixel, PgUp / PgDn 0.1 degrees, Home / End 0.1 %; with Shift a tenth."u8);
        ImGui.TextDisabled("Shift+wheel turns, Ctrl+wheel scales."u8);

        AlignPose pose = _compare[moving].Pose;
        double x = pose.Dx, y = pose.Dy, turn = pose.Rotation, percent = pose.Scale * 100;
        ImGui.PushItemWidth(150 * scale);
        bool edited = ImGui.InputDouble("x"u8, ref x, 1, 10, "%.3f"u8);
        ImGui.SameLine();
        edited |= ImGui.InputDouble("y, pixels"u8, ref y, 1, 10, "%.3f"u8);
        edited |= ImGui.InputDouble("turn, degrees"u8, ref turn, 0.1, 1, "%.4f"u8);
        ImGui.SameLine();
        edited |= ImGui.InputDouble("size, %"u8, ref percent, 0.1, 1, "%.4f"u8);
        ImGui.PopItemWidth();
        if (edited) SetPose(moving, new AlignPose(x, y, turn, percent / 100), AlignAction.Edit);

        ImGui.BeginDisabled(pose.IsIdentity);
        if (ImGui.Button("Reset (Backspace)"u8)) ResetPose();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_alignUndoItem == null || !_compare.Contains(_alignUndoItem));
        if (ImGui.Button("Undo the last change"u8)) UndoPose();
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Automatically, the image may be"u8);
        ImGui.SameLine();
        if (ImGui.RadioButton("moved"u8, _alignModel == AlignModel.Shift)) _alignModel = AlignModel.Shift;
        ImGui.SameLine();
        if (ImGui.RadioButton("and turned"u8, _alignModel == AlignModel.ShiftRotation)) _alignModel = AlignModel.ShiftRotation;
        ImGui.SameLine();
        if (ImGui.RadioButton("and scaled"u8, _alignModel == AlignModel.ShiftRotationScale)) _alignModel = AlignModel.ShiftRotationScale;

        // What it goes by: everything the two images show, or a rectangle dragged on the image.
        if (_alignRegion is { } region)
        {
            if (Utf8.TryWrite(_utf8, $"It goes by the region at x {region.X}, y {region.Y}: {region.Width} x {region.Height} pixels", out n)) Put(n);
        }
        else
        {
            ImGui.TextUnformatted("It goes by the whole of both images."u8);
        }
        if (ImGui.Button(_alignRegionArmed ? "Drag a rectangle on the image..."u8 : "Region..."u8)) _alignRegionArmed = !_alignRegionArmed;
        if (_alignRegion != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Whole image"u8))
            {
                _alignRegion = null;
                _alignReport = null; // what the last alignment said was about the region
            }
        }
        ImGui.BeginDisabled(_alignRunning);
        if (ImGui.Button("Align (Shift+A)"u8)) _deferred = () => AutoAlign(fine: false);
        ImGui.SameLine();
        if (ImGui.Button("Fine align from here (Q)"u8)) _deferred = () => AutoAlign(fine: true);
        ImGui.EndDisabled();
        if (_alignReport != null)
        {
            Utf8Of(_alignReport);
            fixed (byte* text = _utf8) TextDim(text);
        }

        ImGui.Separator();
        // How alike the two are as they lie now: it follows every change, which is what tells a better pose from a worse one.
        bool current = _alignScoreKnown && _compare[reference].Image!.Full is { } a && _compare[moving].Image!.Full is { } b &&
                       _alignScoreKey == new ScoreKey(a, b, ImageToCanvas(reference), ImageToCanvas(moving), _alignRegion);
        if (current && !double.IsNaN(_alignScore))
        {
            if (_alignRegion != null ? Utf8.TryWrite(_utf8, $"Alike in the region: {_alignScore:0.0000}", out n) : Utf8.TryWrite(_utf8, $"Alike: {_alignScore:0.0000}", out n)) Put(n);
        }
        else
        {
            ImGui.TextUnformatted(current ? "Alike: -   (the two do not overlap there, or one is flat)"u8 : "Alike: ..."u8);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("correlation of brightness at reduced size; 1: the same picture"u8);
        if (ImGui.Button("Save the aligned image... (Ctrl+E)"u8)) _deferred = SaveAligned;
        ImGui.End();
    }

    // ---------------------------------------------------------------- saving the aligned image

    /// <summary>
    /// Saves the second image of the pair as it lies on the first: on the first one's pixels
    /// where that image lies pixel for pixel on the canvas, else on the canvas's. The file has
    /// the image's own depth: PNG, or TIFF for float values.
    /// </summary>
    void SaveAligned()
    {
        if (!TryMetricsKey(out MetricsKey key))
        {
            Notice(Comparing ? "Both images have to be loaded in full first." : "There is no comparison to save an aligned image of.");
            return;
        }
        (_, int moving) = _pick.Pair;
        string name = Path.GetFileNameWithoutExtension(_compare[moving].Path) + "_aligned";
        var canvas = new CanvasRect(0, 0, (int)_canvasWidth, (int)_canvasHeight);
        bool isFloat = key.Second.Layout == PixelLayout.GrayFloat;
        key.First.AddReference();
        key.Second.AddReference();
        void Release()
        {
            key.First.Dispose();
            key.Second.Dispose();
        }
        if (_options.ExportDir != null) Notice("Saving...");
        SaveTo(name + (isFloat ? ".tif" : ".png"), isFloat ? "aligned.tif" : "aligned.png",
            isFloat ? [("TIFF image, 32-bit float", "tif")] : [("PNG image", "png")], path => Export(Path.GetFileName(path), () =>
        {
            try
            {
                var first = new CompareSource(key.First, key.FirstToCanvas);
                CanvasRect area = first.PixelAligned ? first.Bounds : canvas;
                using DecodedImage image = CompareExport.Resampled(new CompareSource(key.Second, key.SecondToCanvas), area, out bool complete);
                if (isFloat)
                {
                    if ((long)image.Width * image.Height * sizeof(float) > uint.MaxValue - 4096) throw new IOException("The image is too large for a TIFF file (4 GB).");
                    ImageWriter.WriteTiffFloat(path, image);
                }
                else
                {
                    // Transparent where the moved image has no pixel, and where it was transparent before.
                    ImageWriter.WritePng(path, image, alpha: !complete || HasTransparency(image));
                }
            }
            finally
            {
                Release();
            }
        }), Release);
    }

    /// <summary>True if a colour image has a pixel that is not opaque.</summary>
    static bool HasTransparency(DecodedImage image)
    {
        if (image.Layout is not (PixelLayout.Bgra8 or PixelLayout.Rgba16)) return false;
        for (int y = 0; y < image.Height; y++)
        {
            byte* row = image.Pixels + (long)y * image.Stride;
            if (image.Layout == PixelLayout.Bgra8)
            {
                for (int x = 0; x < image.Width; x++)
                    if (row[x * 4 + 3] != 255) return true;
            }
            else
            {
                for (int x = 0; x < image.Width; x++)
                    if (((ushort*)row)[x * 4 + 3] != 65535) return true;
            }
        }
        return false;
    }
}
