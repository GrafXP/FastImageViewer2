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
// the modes that combine images, the linked cursor, the numbers and the saved files.
//
// The images are aligned to the reference (see CompareSelection), and what is moved are the
// images ticked to be moved, all but the reference unless some are unticked. By hand: in align
// mode a drag moves them instead of the view, and the arrow keys and their neighbours nudge,
// turn and scale them, all alike; the numbers in the panel set one image's pose. Automatically:
// Fiv.Comparing.Registration works out each one's pose to the reference on a worker, one image
// after the other, from the full-resolution pixels, and it is taken over if it does not make
// the two images less alike.
internal sealed unsafe partial class MainWindow
{
    /// <summary>What changed the poses last. Changes of one kind to the same images in a row are undone together.</summary>
    enum AlignAction { None, Nudge, Edit, Drag, Automatic, Reset }

    bool _alignMode;
    AlignModel _alignModel = AlignModel.ShiftRotationScale;

    bool _dragAlign;
    bool _dragAlignTurns;            // the drag turns the images (Shift was held when it began) instead of moving them
    readonly AlignPose[] _alignDragStarts = new AlignPose[MaxCompare]; // each image's pose when the drag began
    int _alignDragLead;              // the image whose centre the turn of a drag is measured about
    double _alignDragX, _alignDragY; // how far the mouse has gone since the drag began, in screen pixels
    double _alignDragTurn;           // how far it has gone round the lead image's centre since then, in degrees

    readonly List<(CompareItem Item, AlignPose Pose)> _alignUndo = []; // the poses before the last change
    AlignAction _alignLastAction;

    // The automatic alignment: the images still to go, one at a time, and how it went so far.
    readonly List<CompareItem> _alignQueue = [];
    int _alignJob, _alignTotal, _alignImproved;
    bool _alignRunning, _alignFine;
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

    bool _alignScoreRunning;

    // The brightness of the compared images at reduced sizes, kept while they are in the set:
    // made once per image, some 10 ms and a third of the image's size in memory.
    readonly Dictionary<DecodedImage, LumaPyramid> _lumas = [];

    /// <summary>True if there is something to align to: the reference, with its size, on a canvas.</summary>
    bool AlignReady() =>
        Comparing && _canvasWidth > 0 && _pick.Reference < _compare.Count && _compare[_pick.Reference].Image is { HasSize: true };

    bool HasSize(int item) => item >= 0 && item < _compare.Count && _compare[item].Image is { HasSize: true };

    /// <summary>The images aligning moves now: those ticked, but the reference, whose size is known. Into <paramref name="items"/>; returns how many.</summary>
    int MovingImages(Span<int> items)
    {
        if (!AlignReady()) return 0;
        int n = 0;
        for (int i = 0; i < _compare.Count; i++)
            if (_pick.Moves(i) && HasSize(i)) items[n++] = i;
        return n;
    }

    /// <summary>
    /// The image whose pose the panel's numbers show and set, and which is saved aligned: the
    /// active one unless it is the reference, otherwise the first one that moves, or any; -1 if none.
    /// </summary>
    int AlignTarget()
    {
        if (!AlignReady()) return -1;
        int active = _pick.Active, reference = _pick.Reference;
        if (active != reference && HasSize(active)) return active;
        Span<int> items = stackalloc int[MaxCompare];
        if (MovingImages(items) > 0) return items[0];
        for (int i = 0; i < _compare.Count; i++)
            if (i != reference && HasSize(i)) return i;
        return -1;
    }

    void SetAlignMode(bool on)
    {
        if (on == _alignMode || (on && !Comparing)) return;
        _alignMode = on;
        _dragAlign = false;
        if (!on) _alignRegionArmed = false; // the region itself stays: Shift+A goes by it with the panel closed too, and it is drawn
        Span<int> items = stackalloc int[MaxCompare];
        int n = on ? MovingImages(items) : 0;
        if (n == 1) Notice($"Aligning image {items[0] + 1} to image {_pick.Reference + 1}: drag it (with Shift: turn it), nudge it with the arrow keys, or press Shift+A.");
        else if (n > 1) Notice($"Aligning {n} images to image {_pick.Reference + 1}: drag them (with Shift: turn them), nudge them with the arrow keys, or press Shift+A. The panel says which move.");
    }

    /// <summary>Forgets everything about aligning: the comparison ended or another began. The poses go with the set.</summary>
    void ResetAlign()
    {
        CancelAutoAlign();
        _alignMode = _dragAlign = false;
        _alignReport = null;
        _alignUndo.Clear();
        _alignLastAction = AlignAction.None;
        _alignRegion = null;
        _alignRegionArmed = false;
        foreach (LumaPyramid luma in _lumas.Values) luma.Dispose();
        _lumas.Clear();
    }

    /// <summary>Stops the automatic alignment and what is still to go of it; what the worker finds is not taken over.</summary>
    void CancelAutoAlign()
    {
        _alignCancel?.Cancel();
        _alignCancel = null;
        _alignJob++;
        _alignRunning = false;
        _alignQueue.Clear();
    }

    /// <summary>The same while the comparison goes on: the images that were waiting or under way say nothing any more, and the panel says why.</summary>
    void StopAutoAlign(string report)
    {
        CancelAutoAlign();
        foreach (CompareItem item in _compare)
            if (item.AlignReport?.EndsWith("...") == true) item.AlignReport = null;
        _alignReport = report;
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

    /// <summary>Remembers the poses of these images for "undo", unless this change continues the last one: of the same kind, to the same images.</summary>
    void BeginChange(ReadOnlySpan<int> items, AlignAction action)
    {
        bool same = action == _alignLastAction && _alignUndo.Count == items.Length;
        for (int k = 0; same && k < items.Length; k++) same = _alignUndo[k].Item == _compare[items[k]];
        _alignLastAction = action;
        if (same) return;
        _alignUndo.Clear();
        foreach (int item in items) _alignUndo.Add((_compare[item], _compare[item].Pose));
    }

    /// <summary>Gives images of the set new poses, as one change.</summary>
    void SetPoses(ReadOnlySpan<int> items, Func<int, AlignPose, AlignPose> change, AlignAction action)
    {
        if (items.IsEmpty) return;
        BeginChange(items, action);
        foreach (int item in items)
        {
            CompareItem entry = _compare[item];
            AlignPose pose = Tidy(change(item, entry.Pose));
            if (pose == entry.Pose) continue;
            entry.Pose = pose;
            if (action != AlignAction.Automatic) entry.AlignReport = null; // what its last automatic alignment said is no longer about this pose
        }
        Invalidate();
    }

    /// <summary>Gives one image of the set a pose.</summary>
    void SetPose(int item, AlignPose pose, AlignAction action)
    {
        if (Tidy(pose) == _compare[item].Pose) return;
        SetPoses([item], (_, _) => pose, action);
    }

    /// <summary>Changes the pose of every image aligning moves alike, as one change. Says so if there is none.</summary>
    void ChangeMoving(Func<int, AlignPose, AlignPose> change, AlignAction action)
    {
        Span<int> items = stackalloc int[MaxCompare];
        int n = MovingImages(items);
        if (n == 0)
        {
            if (AlignReady()) Notice("No image is ticked to be moved: tick one in the align panel.");
            return;
        }
        SetPoses(items[..n], change, action);
    }

    void Nudge(double dx, double dy) => ChangeMoving((_, pose) => pose.With(dx: pose.Dx + dx, dy: pose.Dy + dy), AlignAction.Nudge);

    void Turn(double degrees) => ChangeMoving((_, pose) => pose.With(rotation: pose.Rotation + degrees), AlignAction.Nudge);

    /// <summary>Makes the images larger or smaller by a fraction of their original size.</summary>
    void Rescale(double by) => ChangeMoving((_, pose) => pose.With(scale: pose.Scale + by), AlignAction.Nudge);

    /// <summary>The images that move back to where they were before any aligning.</summary>
    void ResetPose()
    {
        _alignLastAction = AlignAction.None;
        ChangeMoving((_, _) => AlignPose.Identity, AlignAction.Reset);
    }

    /// <summary>Back to the poses before the last change, and, pressed again, forward to them.</summary>
    void UndoPose()
    {
        for (int k = 0; k < _alignUndo.Count; k++)
        {
            (CompareItem entry, AlignPose pose) = _alignUndo[k];
            if (!_compare.Contains(entry)) continue;
            _alignUndo[k] = (entry, entry.Pose);
            entry.Pose = pose;
        }
        _alignLastAction = AlignAction.None;
        Invalidate();
    }

    bool CanUndoPose() => _alignUndo.Exists(undo => _compare.Contains(undo.Item));

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
    /// The left button went down over a pane while aligning: the drag moves the images that
    /// move, or with <paramref name="turn"/> (Shift held) turns each about its centre. False if there is nothing to move.
    /// </summary>
    bool BeginAlignDrag(bool turn)
    {
        Span<int> items = stackalloc int[MaxCompare];
        int n = MovingImages(items);
        if (n == 0) return false;
        _dragAlign = true;
        _dragAlignTurns = turn;
        for (int i = 0; i < _compare.Count; i++) _alignDragStarts[i] = _compare[i].Pose;
        // The turn is measured about the centre of the image the user is at, if it moves; of the first that moves otherwise.
        _alignDragLead = items[..n].Contains(_pick.Active) ? _pick.Active : items[0];
        _alignDragX = _alignDragY = _alignDragTurn = 0;
        _alignLastAction = AlignAction.None; // each drag can be undone on its own
        return true;
    }

    /// <summary>The mouse, at (<paramref name="x"/>, <paramref name="y"/>) now, went this far in a drag that moves or turns the images.</summary>
    void AlignDrag(int x, int y, int dx, int dy)
    {
        Span<int> items = stackalloc int[MaxCompare];
        int n = MovingImages(items);
        if (n == 0) return;
        if (_dragAlignTurns)
        {
            // Each image goes round its centre by the angle the mouse goes round the lead image's:
            // what was grabbed stays under the mouse, and the further out it is grabbed, the finer the turn.
            if (!TryGetAlignCentreOnScreen(_alignDragLead, out double centreX, out double centreY)) return;
            double fromX = x - dx - centreX, fromY = y - dy - centreY, toX = x - centreX, toY = y - centreY;
            if (fromX * fromX + fromY * fromY < 64 || toX * toX + toY * toY < 64) return; // at the centre itself there is no angle to speak of
            // Screen coordinates have y pointing down, so this angle is positive clockwise, as the pose's turn is.
            _alignDragTurn += Math.Atan2(fromX * toY - fromY * toX, fromX * toX + fromY * toY) * (180 / Math.PI);
            double turn = _alignDragTurn;
            SetPoses(items[..n], (item, _) => _alignDragStarts[item].With(rotation: _alignDragStarts[item].Rotation + turn), AlignAction.Drag);
            return;
        }
        _alignDragX += dx;
        _alignDragY += dy;
        double zoom = ViewOf(_dragItem).Zoom, moveX = _alignDragX / zoom, moveY = _alignDragY / zoom;
        // Zoomed out, a screen pixel is several image pixels: the images move by whole pixels,
        // and what they were moved by a fraction before stays. Fractions are for when one can see them.
        if (zoom <= 1)
        {
            moveX = Math.Round(moveX);
            moveY = Math.Round(moveY);
        }
        SetPoses(items[..n], (item, _) => _alignDragStarts[item].With(dx: _alignDragStarts[item].Dx + moveX, dy: _alignDragStarts[item].Dy + moveY), AlignAction.Drag);
    }

    /// <summary>
    /// Where the centre of an image being aligned is in the window, in the pane the drag is in:
    /// the point it turns about. The canvas is the same in every pane, so any pane will do.
    /// </summary>
    bool TryGetAlignCentreOnScreen(int moving, out double x, out double y)
    {
        x = y = 0;
        int pane = -1;
        for (int p = 0; p < _paneCount && pane < 0; p++)
            if (_panes[p].First == _dragItem) pane = p;
        if (pane < 0 || !HasSize(moving)) return false;
        (double centreX, double centreY) = PoseCentre(_compare[moving].Image!);
        AlignPose pose = _compare[moving].Pose;
        (x, y) = CanvasToScreen(_dragItem, _panes[pane].Rect).Apply(centreX + pose.Dx, centreY + pose.Dy);
        return true;
    }

    /// <summary>
    /// The wheel while aligning: with Shift it turns the images, as a drag with Shift does (a
    /// tenth of a degree a notch, with Ctrl as well a hundredth); with Ctrl alone it scales them.
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

    /// <summary>What a worker needs of a pair, held so that nothing of it goes away under it.</summary>
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

    /// <summary>Takes hold of the pixels of an image and the reference, and of what is cached for them. False if they are not both loaded in full.</summary>
    bool TryBeginAlignWork(int reference, int moving, out AlignWork work)
    {
        work = null!;
        if (!HasSize(reference) || !HasSize(moving) || reference == moving) return false;
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

    // ---------------------------------------------------------------- how alike, as the poses are now

    /// <summary>The key of an image's score against the reference as the two lie now; false if either is not loaded in full.</summary>
    bool TryScoreKey(int reference, int item, out ScoreKey key)
    {
        key = default;
        if (!HasSize(reference) || !HasSize(item) || _compare[reference].Image!.Full is not { } a || _compare[item].Image!.Full is not { } b) return false;
        key = new ScoreKey(a, b, ImageToCanvas(reference), ImageToCanvas(item), _alignRegion);
        return true;
    }

    /// <summary>
    /// Starts the score of an image against the reference as it lies now, if the panel is open
    /// and it is neither there nor on its way: the one whose numbers the panel shows first, then
    /// the others, one at a time. Cheap; called every frame.
    /// </summary>
    void UpdateAlignScore()
    {
        if (!_alignMode || _alignScoreRunning || _alignRunning || !AlignReady()) return;
        if (_dragRegion && _dragRegionForAlign) return; // the region is being drawn: the scores follow when it is there
        int reference = _pick.Reference, target = AlignTarget();
        for (int k = -1; k < _compare.Count; k++)
        {
            int item = k < 0 ? target : k;
            if (item < 0 || item == reference || (k >= 0 && item == target)) continue;
            if (!TryScoreKey(reference, item, out ScoreKey key)) continue;
            CompareItem entry = _compare[item];
            if (entry.ScoreKnown && entry.ScoreKey == key) continue;
            StartScore(reference, item, key);
            return;
        }
    }

    void StartScore(int reference, int item, ScoreKey key)
    {
        PruneLumas();
        if (!TryBeginAlignWork(reference, item, out AlignWork work)) return;
        CompareItem entry = _compare[item];
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
                entry.Score = score;
                entry.ScoreKey = key;
                entry.ScoreKnown = true;
            });
        });
    }

    /// <summary>How alike an image is to the reference as the two lie now: NaN if they do not overlap or one is flat; null if it is not known yet.</summary>
    double? CurrentScore(int reference, int item)
    {
        CompareItem entry = _compare[item];
        return entry.ScoreKnown && TryScoreKey(reference, item, out ScoreKey key) && entry.ScoreKey == key ? entry.Score : null;
    }

    // ---------------------------------------------------------------- automatic alignment

    /// <summary>
    /// Works out the pose of every image that moves, to the reference, one after the other:
    /// from scratch, or with <paramref name="fine"/> from the pose each has. The results arrive in <see cref="OnAligned"/>.
    /// </summary>
    void AutoAlign(bool fine)
    {
        Span<int> items = stackalloc int[MaxCompare];
        int n = MovingImages(items);
        if (n == 0)
        {
            Notice(!AlignReady() ? "Aligning takes two images on show." : "No image is ticked to be moved: tick the ones to align in the align panel (A).");
            return;
        }
        int reference = _pick.Reference;
        bool loaded = _compare[reference].Image!.Full != null;
        foreach (int item in items[..n]) loaded &= _compare[item].Image!.Full != null;
        if (!loaded)
        {
            Notice(n == 1 ? "Both images have to be loaded in full first." : "The images have to be loaded in full first.");
            return;
        }
        CancelAutoAlign();
        _alignCancel = new CancellationTokenSource();
        _alignRunning = true;
        _alignFine = fine;
        _alignTotal = n;
        _alignImproved = 0;
        foreach (int item in items[..n])
        {
            _alignQueue.Add(_compare[item]);
            _compare[item].AlignReport = "Waiting...";
        }
        AlignNext(_alignJob);
    }

    /// <summary>Starts the next image of the automatic alignment, or, with none left, says how it went.</summary>
    void AlignNext(int job)
    {
        int reference = _pick.Reference;
        while (_alignQueue.Count > 0)
        {
            CompareItem entry = _alignQueue[0];
            _alignQueue.RemoveAt(0);
            int moving = _compare.IndexOf(entry);
            if (moving < 0 || moving == reference) continue; // it left the set
            PruneLumas();
            if (!TryBeginAlignWork(reference, moving, out AlignWork work))
            {
                entry.AlignReport = "Not loaded in full: not aligned.";
                continue;
            }
            bool fine = _alignFine;
            CancellationToken cancel = _alignCancel!.Token;
            AlignPose start = entry.Pose;
            AlignModel model = _alignModel;
            CanvasRect? region = _alignRegion;
            entry.AlignReport = fine ? "Refining..." : "Aligning...";
            int done = _alignTotal - _alignQueue.Count - 1;
            _alignReport = _alignTotal > 1 ? $"{(fine ? "Refining" : "Aligning")} {done + 1} of {_alignTotal}..." : entry.AlignReport;
            Task.Run(() =>
            {
                AlignResult? result = null;
                string? error = null;
                try
                {
                    work.Prepare();
                    result = Registration.Align(new AlignInput(work.FirstLuma!, work.FirstToCanvas), new AlignInput(work.SecondLuma!, work.SecondPlaced), start, model, fine, cancel, region);
                }
                catch (Exception e)
                {
                    CrashLog.Write(e);
                    error = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
                }
                RunOnUi(() => OnAligned(job, work, entry, start, result, error, fine, model, region));
            });
            return;
        }
        // All done.
        _alignRunning = false;
        if (_alignTotal > 1)
        {
            _alignReport = $"{_alignImproved} of {_alignTotal} images {(_alignFine ? "refined" : "aligned")} to image {reference + 1}; how alike each is now, the table says.";
            Notice(_alignReport);
        }
    }

    /// <summary>
    /// A worker has a pose. It is taken over unless it makes the two images less alike than they
    /// were: in the region the alignment went by, if there was one. Then the next image. UI thread.
    /// </summary>
    void OnAligned(int job, AlignWork work, CompareItem entry, AlignPose start, AlignResult? result, string? error, bool fine, AlignModel model, CanvasRect? region)
    {
        EndAlignWork(work);
        if (job != _alignJob) return; // of a comparison that has ended, of another reference, or overtaken by another run
        int item = _compare.IndexOf(entry);
        string? report;
        if (error != null) report = "The alignment failed: " + error;
        else if (result == null || item < 0) report = null; // cancelled, or the image has left the set
        else if (entry.Pose != start) report = "The image was moved while its alignment was worked out: left as it is.";
        else
        {
            bool better = result.Found && !double.IsNaN(result.ScoreAfter) && !(result.ScoreAfter < result.ScoreBefore - 0.0005);
            // With a region, the scores are of the region: the report says so.
            string where = region != null ? " in the region" : "", by = region != null ? " by the region" : "";
            if (!better)
            {
                report = double.IsNaN(result.ScoreAfter) || !result.Found
                    ? region != null ? "No alignment found: the region is too small, or the two images have too little in common there."
                                     : "No alignment found: the two images have too little in common."
                    : $"No better alignment found: alike{where} {result.ScoreBefore:0.0000} as it is, {result.ScoreAfter:0.0000} at best.";
            }
            else if (Tidy(result.Pose) == start)
            {
                report = $"Nothing to improve: alike{where} {result.ScoreAfter:0.0000} ({result.Milliseconds:0} ms).";
            }
            else
            {
                _alignLastAction = AlignAction.None;
                SetPose(item, result.Pose, AlignAction.Automatic);
                _alignImproved++;
                report = double.IsNaN(result.ScoreBefore)
                    ? $"Aligned{by} in {result.Milliseconds:0} ms: alike{where} {result.ScoreAfter:0.0000}."
                    : $"Aligned{by} in {result.Milliseconds:0} ms: alike{where} {result.ScoreBefore:0.0000} before, {result.ScoreAfter:0.0000} after.";
            }
        }
        entry.AlignReport = report;
        if (_alignTotal == 1)
        {
            _alignReport = report;
            if (report != null) Notice(report);
        }
        if (result != null && _options.PerfLog != null)
        {
            try
            {
                AlignPose pose = item >= 0 ? entry.Pose : result.Pose;
                File.AppendAllText(_options.PerfLog,
                    $"align fine={(fine ? 1 : 0)} model={model} ms={result.Milliseconds:F1} found={(result.Found ? 1 : 0)} patches={result.Patches} " +
                    $"before={result.ScoreBefore:F5} after={result.ScoreAfter:F5} dx={pose.Dx:F3} dy={pose.Dy:F3} rotation={pose.Rotation:F4} scale={pose.Scale:F6} " +
                    $"region={(region is { } r ? $"{r.X},{r.Y},{r.Width},{r.Height}" : "none")} item={item + 1} at={Stopwatch.GetTimestamp()}\n");
            }
            catch (IOException)
            {
            }
        }
        AlignNext(job);
    }

    // ---------------------------------------------------------------- the panel

    void DrawAlignPanel()
    {
        float scale = (float)_dpiScale;
        // Top right, under the name of the image on the right: it is the images there the panel moves.
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth - 10 * scale, ViewY + 64 * scale), ImGuiCond.FirstUseEver, new Vector2(1, 0));
        bool open = true;
        if (!ImGui.Begin("Align"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            if (!open) SetAlignMode(false);
            return;
        }
        if (!open) _deferred = () => SetAlignMode(false);
        int reference = _pick.Reference, target = AlignTarget();
        if (target < 0)
        {
            ImGui.TextDisabled("Aligning takes two images on show."u8);
            ImGui.End();
            return;
        }
        int n;
        if (Utf8.TryWrite(_utf8, $"Laid on   {reference + 1}  {Path.GetFileName(_compare[reference].Path.AsSpan())}, the reference (X: the next image is)", out n)) Put(n);
        ImGui.TextDisabled("Drag to move the ticked images, drag with Shift to turn each about its centre."u8);
        ImGui.TextDisabled("Arrows 1 pixel, PgUp / PgDn 0.1 degrees, Home / End 0.1 %%; with Shift a tenth."u8); // a format: %% is one %
        ImGui.TextDisabled("Shift+wheel turns, Ctrl+wheel scales."u8);
        DrawAlignImages(reference, target);

        // The numbers of one image: the one clicked in the table.
        AlignPose pose = _compare[target].Pose;
        if (Utf8.TryWrite(_utf8, $"{target + 1}  {Path.GetFileName(_compare[target].Path.AsSpan())}{(_pick.Moves(target) ? "" : "   (not ticked: drags and keys leave it)")}", out n)) Put(n);
        double x = pose.Dx, y = pose.Dy, turn = pose.Rotation, percent = pose.Scale * 100;
        ImGui.PushItemWidth(150 * scale);
        bool edited = ImGui.InputDouble("x"u8, ref x, 1, 10, "%.3f"u8);
        ImGui.SameLine();
        edited |= ImGui.InputDouble("y, pixels"u8, ref y, 1, 10, "%.3f"u8);
        edited |= ImGui.InputDouble("turn, degrees"u8, ref turn, 0.1, 1, "%.4f"u8);
        ImGui.SameLine();
        edited |= ImGui.InputDouble("size, %"u8, ref percent, 0.1, 1, "%.4f"u8);
        ImGui.PopItemWidth();
        if (edited) SetPose(target, new AlignPose(x, y, turn, percent / 100), AlignAction.Edit);

        Span<int> items = stackalloc int[MaxCompare];
        bool anyMoved = false;
        int moving = MovingImages(items);
        foreach (int item in items[..moving]) anyMoved |= !_compare[item].Pose.IsIdentity;
        ImGui.BeginDisabled(!anyMoved);
        if (ImGui.Button(moving > 1 ? "Reset the ticked images (Backspace)"u8 : "Reset (Backspace)"u8)) ResetPose();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!CanUndoPose());
        if (ImGui.Button("Undo the last change"u8)) UndoPose();
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled("Automatically, the ticked images may be"u8);
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
        ImGui.BeginDisabled(_alignRunning || moving == 0);
        if (ImGui.Button("Align (Shift+A)"u8)) _deferred = () => AutoAlign(fine: false);
        ImGui.SameLine();
        if (ImGui.Button("Fine align from here (Q)"u8)) _deferred = () => AutoAlign(fine: true);
        ImGui.EndDisabled();
        if (_alignRunning)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop"u8)) StopAutoAlign("Stopped.");
        }
        if (_alignReport != null)
        {
            Utf8Of(_alignReport);
            fixed (byte* text = _utf8) TextDim(text);
        }

        ImGui.Separator();
        if (Utf8.TryWrite(_utf8, $"Save image {target + 1} aligned... (Ctrl+E)", out n))
        {
            _utf8[n] = 0;
            fixed (byte* label = _utf8)
                if (ImGui.Button(label)) _deferred = SaveAligned;
        }
        ImGui.End();
    }

    /// <summary>
    /// Every image but the reference, a row each: whether aligning moves it, its pose, how alike it
    /// is to the reference as it lies, and what its last automatic alignment said. A click on a
    /// name makes it the image whose numbers are shown and set below.
    /// </summary>
    void DrawAlignImages(int reference, int target)
    {
        if (!ImGui.BeginTable("aligned"u8, 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) return;
        ImGui.TableSetupColumn("Moves"u8);
        ImGui.TableSetupColumn("Image"u8);
        ImGui.TableSetupColumn("Moved by"u8);
        ImGui.TableSetupColumn("Alike"u8);
        ImGui.TableHeadersRow();
        for (int i = 0; i < _compare.Count; i++)
        {
            if (i == reference) continue;
            CompareItem entry = _compare[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            bool moves = _pick.Moves(i);
            if (ImGui.Checkbox("##moves"u8, ref moves))
            {
                _pick.SetMoves(i, moves);
                _alignLastAction = AlignAction.None; // what is moved has changed: undo starts over
            }

            ImGui.TableSetColumnIndex(1);
            int n = Utf8.TryWrite(_utf8, $"{i + 1}  {Path.GetFileName(entry.Path.AsSpan())}", out int written) ? written : 0;
            _utf8[n] = 0;
            fixed (byte* text = _utf8)
            {
                if (ImGui.Selectable(text, i == target) && _pick.Select(i))
                {
                    StopBlink();
                    OnComparePick();
                }
            }
            if (entry.AlignReport != null && ImGui.IsItemHovered() && ImGui.BeginTooltip())
            {
                Utf8Of(entry.AlignReport);
                fixed (byte* text = _utf8) ImGui.TextUnformatted(text);
                ImGui.EndTooltip();
            }

            ImGui.TableSetColumnIndex(2);
            if (entry.Pose.IsIdentity)
            {
                ImGui.TextDisabled("not moved"u8);
            }
            else
            {
                n = WritePose(entry.Pose, _utf8.AsSpan(0, _utf8.Length - 1));
                Put(n);
            }

            ImGui.TableSetColumnIndex(3);
            switch (CurrentScore(reference, i))
            {
                case null:
                    ImGui.TextDisabled("..."u8);
                    break;
                case double score when double.IsNaN(score):
                    ImGui.TextDisabled("-"u8);
                    break;
                case double score:
                    if (Utf8.TryWrite(_utf8, $"{score:0.0000}", out n)) Put(n);
                    break;
            }
            // While the automatic alignment goes through the images, where each one is; what became of it, the name's tooltip says.
            if (entry.AlignReport is { } report && report.EndsWith("..."))
            {
                ImGui.SameLine();
                Utf8Of(report);
                fixed (byte* text = _utf8) TextDim(text);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.TextDisabled("Alike: correlation of brightness at reduced size; 1: the same picture."u8);
        if (_compare.Count > 2)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Tick all"u8))
                for (int i = 0; i < _compare.Count; i++) _pick.SetMoves(i, true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Tick none"u8))
                for (int i = 0; i < _compare.Count; i++) _pick.SetMoves(i, false);
        }
    }

    // ---------------------------------------------------------------- saving the aligned image

    /// <summary>
    /// Saves an image as it lies on the reference, the one whose numbers the align panel shows:
    /// on the reference's pixels where that image lies pixel for pixel on the canvas, else on the
    /// canvas's. The file has the image's own depth: PNG, or TIFF for float values.
    /// </summary>
    void SaveAligned()
    {
        LayoutCompare();
        int reference = _pick.Reference, moving = AlignTarget();
        if (moving < 0 || !TryMetricsKey(reference, moving, out MetricsKey key))
        {
            Notice(!Comparing ? "There is no comparison to save an aligned image of." : moving < 0 ? "Aligning takes two images on show." : "Both images have to be loaded in full first.");
            return;
        }
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
