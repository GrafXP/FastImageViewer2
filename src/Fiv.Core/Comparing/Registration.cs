using System.Buffers;
using System.Diagnostics;
using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>
/// One image as the registration sees it: its brightness, and where its stored pixels lie on
/// the canvas. For the image that is moved, the transform is the one of its placement alone.
/// </summary>
public readonly record struct AlignInput(LumaPyramid Luma, Affine2 ToCanvas);

/// <summary>What <see cref="Registration.Align"/> found.</summary>
public sealed class AlignResult
{
    /// <summary>Where the moved image belongs, about its centre on the canvas.</summary>
    public AlignPose Pose { get; init; }

    /// <summary>
    /// How alike the two images are where they overlap, at the pose given and at the pose found:
    /// the correlation of their brightness, 1 for the same picture (see <see cref="Registration.Score"/>).
    /// Not a number if they do not overlap.
    /// </summary>
    public double ScoreBefore { get; init; }
    public double ScoreAfter { get; init; }

    /// <summary>The parts of the image the last step measured a shift in, and how clear those shifts were on average (1: nothing but a shift).</summary>
    public int Patches { get; init; }
    public double Confidence { get; init; }

    /// <summary>False if no step found anything to go by: the pose is then no better than a guess.</summary>
    public bool Found { get; init; }

    public double Milliseconds { get; init; }
}

/// <summary>
/// Finds where one image lies on another: moved, turned and scaled.
///
/// A first estimate comes from the two pictures as a whole, reduced to 512 samples across. How
/// much one is turned and scaled against the other is read from their Fourier spectra, whose
/// magnitudes do not depend on where the pictures lie: laid out by angle and by the logarithm
/// of the radius, a turn and a scaling are two shifts, found by phase correlation. With those
/// undone, a second phase correlation gives the shift.
///
/// The estimate is then refined from coarse to fine, on the image at a sixteenth, an eighth
/// and so on up to its full size: at each size the shift that is left is measured by phase
/// correlation in a grid of patches, and the move, turn and scaling that explains those shifts
/// best is taken off. The patches of the full-size step are 256 pixels across and each good
/// to a few hundredths of a pixel; spread over the image they fix the turn to a thousandth of
/// a degree.
/// </summary>
public static unsafe class Registration
{
    const int CoarseSize = 512, ScoreSize = 640;
    const int LargestPatch = 256, SmallestPatch = 16, MostPatchesAcross = 6;

    /// <summary>What counts as nothing, in canvas pixels at the corners of the image: a turn or a scaling that moves them by less is taken to be none.</summary>
    const double Negligible = 0.03;

    /// <summary>
    /// A pose that puts the corners less than this far from where a move by whole pixels, with
    /// no turn and no scaling, would put them is taken to be that move. It is the one pose under
    /// which two images are compared pixel for pixel, and the tolerance stays inside the tenth
    /// of a pixel the alignment is good for.
    /// </summary>
    const double NearlyWhole = 0.08;

    readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public bool IsEmpty => !(Width > 0 && Height > 0);
        public Box Intersect(Box other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        public Box Union(Box other) => new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>The upright rectangle of the canvas an image lies in.</summary>
    static Box BoxOf(LumaPyramid luma, in Affine2 toCanvas)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (int corner = 0; corner < 4; corner++)
        {
            (double x, double y) = toCanvas.Apply((corner & 1) * luma.Width, (corner >> 1) * luma.Height);
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return new Box(left, top, right, bottom);
    }

    /// <summary>The point poses turn and scale about: the centre of the moved image where its placement puts it.</summary>
    public static (double X, double Y) Pivot(in AlignInput moving) => moving.ToCanvas.Apply(moving.Luma.Width / 2.0, moving.Luma.Height / 2.0);

    /// <summary>
    /// Aligns <paramref name="moving"/> to <paramref name="reference"/>. With <paramref name="fine"/>
    /// the pose <paramref name="start"/> is refined, which works from a pose that is off by up
    /// to about a twentieth of the image; without, the search starts from the placement alone
    /// and <paramref name="start"/> only says what "before" is. <paramref name="model"/> says
    /// what may be changed: a turn or scaling that is not, stays as the starting pose has it.
    /// Null if cancelled.
    ///
    /// With <paramref name="region"/>, a rectangle of the canvas, only what the two images show
    /// inside it is gone by: the rest of them may differ as it likes. The pose found is still
    /// that of the whole image. A region can make up for a pose that is off by up to about a
    /// quarter of its own size; the smaller it is, the less it says about turn and scaling.
    /// The scores are then of the region too.
    /// </summary>
    public static AlignResult? Align(AlignInput reference, AlignInput moving, AlignPose start, AlignModel model, bool fine,
        CancellationToken cancel = default, CanvasRect? region = null)
    {
        long started = Stopwatch.GetTimestamp();
        (double pivotX, double pivotY) = Pivot(moving);
        Box movingBox = BoxOf(moving.Luma, moving.ToCanvas);
        double halfDiagonal = 0.5 * Math.Sqrt(movingBox.Width * movingBox.Width + movingBox.Height * movingBox.Height);
        Box? within = region is { } r ? new Box(r.X, r.Y, r.Right, r.Bottom) : null;

        AlignPose basis = fine ? start : AlignPose.Identity;
        Affine2 current = basis.About(pivotX, pivotY);
        double before = Score(reference, moving with { ToCanvas = start.About(pivotX, pivotY).After(moving.ToCanvas) }, region);
        if (cancel.IsCancellationRequested) return null;

        bool found = false;
        if (!fine && Coarse(reference, moving, model, within, cancel) is { } estimate)
        {
            current = estimate;
            found = true;
        }
        if (cancel.IsCancellationRequested) return null;
        current = Refine(reference, moving, current, model, within, out double confidence, out int patches, cancel);
        if (cancel.IsCancellationRequested) return null;
        found |= patches > 0;

        AlignPose pose = AlignPose.FromSimilarity(current, pivotX, pivotY);
        // What the model does not let change is exactly as it was, not as rounding left it.
        pose = model switch
        {
            AlignModel.Shift => new AlignPose(pose.Dx, pose.Dy, basis.Rotation, basis.Scale),
            AlignModel.ShiftRotation => new AlignPose(pose.Dx, pose.Dy, pose.Rotation, basis.Scale),
            _ => pose,
        };
        pose = Snap(pose, halfDiagonal);
        double after = Score(reference, moving with { ToCanvas = pose.About(pivotX, pivotY).After(moving.ToCanvas) }, region);
        return new AlignResult
        {
            Pose = pose,
            ScoreBefore = before,
            ScoreAfter = after,
            Patches = patches,
            Confidence = confidence,
            Found = found,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    /// <summary>
    /// A pose within a hair of whole pixels, of no turn or of no scaling is taken to be that:
    /// two images that differ by a shift of three pixels are then compared pixel for pixel,
    /// not through an interpolation by a few hundredths of a pixel.
    /// </summary>
    static AlignPose Snap(AlignPose pose, double halfDiagonal)
    {
        double rotation = pose.Rotation, scale = pose.Scale, dx = pose.Dx, dy = pose.Dy;
        // How far the turn and the scaling move the corners, and how far the move is from whole pixels.
        double angle = rotation * (Math.PI / 180), c = scale * Math.Cos(angle) - 1, s = scale * Math.Sin(angle);
        double turned = halfDiagonal * Math.Sqrt(c * c + s * s);
        double fractionX = dx - Math.Round(dx), fractionY = dy - Math.Round(dy);
        if (turned + Math.Sqrt(fractionX * fractionX + fractionY * fractionY) < NearlyWhole)
            return new AlignPose(Math.Round(dx) + 0.0, Math.Round(dy) + 0.0); // plus zero: no "minus nothing"
        if (halfDiagonal * Math.Abs(angle) < Negligible) rotation = 0;
        if (halfDiagonal * Math.Abs(scale - 1) < Negligible) scale = 1;
        return new AlignPose(dx, dy, rotation, scale);
    }

    // ---------------------------------------------------------------- how alike

    /// <summary>
    /// How alike two images are where both have pixels, as they lie on the canvas: the
    /// correlation of their brightness (each less its mean, over the product of their spreads),
    /// taken at a reduced size of about 640 samples across. 1 for the same picture, also if one
    /// is brighter or has more contrast; near 0 for unrelated ones. Not a number where they do
    /// not overlap or one is flat. A millisecond or two. With <paramref name="region"/>, a
    /// rectangle of the canvas, only what is inside it counts.
    /// </summary>
    public static double Score(AlignInput reference, AlignInput moving, CanvasRect? region = null)
    {
        Box overlap = BoxOf(reference.Luma, reference.ToCanvas).Intersect(BoxOf(moving.Luma, moving.ToCanvas));
        if (region is { } r) overlap = overlap.Intersect(new Box(r.X, r.Y, r.Right, r.Bottom));
        if (overlap.IsEmpty) return double.NaN;
        double step = Math.Max(1, Math.Max(overlap.Width, overlap.Height) / ScoreSize);
        int columns = Math.Max(1, (int)(overlap.Width / step)), rows = Math.Max(1, (int)(overlap.Height / step));
        const int band = 32;
        Affine2 referenceToStored = reference.ToCanvas.Inverse(), movingToStored = moving.ToCanvas.Inverse();
        var total = new double[6];
        Parallel.For(0, (rows + band - 1) / band, () => (A: new float[columns * band], B: new float[columns * band], Sums: new double[6]), (index, _, local) =>
        {
            int first = index * band, count = Math.Min(band, rows - first);
            Affine2 gridToCanvas = Affine2.ScaleTranslate(step, step, overlap.Left, overlap.Top + first * step);
            fixed (float* a = local.A, b = local.B)
            {
                reference.Luma.Sample(a, columns, count, referenceToStored.After(gridToCanvas));
                moving.Luma.Sample(b, columns, count, movingToStored.After(gridToCanvas));
                double n = 0, sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int i = 0; i < columns * count; i++)
                {
                    double va = a[i], vb = b[i];
                    if (double.IsNaN(va) || double.IsNaN(vb)) continue;
                    n++;
                    sa += va;
                    sb += vb;
                    saa += va * va;
                    sbb += vb * vb;
                    sab += va * vb;
                }
                double[] sums = local.Sums;
                sums[0] += n;
                sums[1] += sa;
                sums[2] += sb;
                sums[3] += saa;
                sums[4] += sbb;
                sums[5] += sab;
            }
            return local;
        }, local =>
        {
            lock (total)
                for (int i = 0; i < total.Length; i++) total[i] += local.Sums[i];
        });
        double samples = total[0];
        if (samples < 16) return double.NaN;
        double varianceA = total[3] - total[1] * total[1] / samples, varianceB = total[4] - total[2] * total[2] / samples;
        double covariance = total[5] - total[1] * total[2] / samples;
        // A flat image has no variance to speak of: what is left of it is rounding.
        if (!(varianceA > 1e-9 * Math.Abs(total[3])) || !(varianceB > 1e-9 * Math.Abs(total[4]))) return double.NaN;
        return Math.Clamp(covariance / Math.Sqrt(varianceA * varianceB), -1, 1);
    }

    // ---------------------------------------------------------------- the first estimate

    static readonly float[] Window = MakeWindow();

    /// <summary>A Hann window over 0 .. 1 in 1024 steps: 0 at both ends, 1 in the middle.</summary>
    static float[] MakeWindow()
    {
        var window = new float[1024];
        for (int i = 0; i < window.Length; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / window.Length));
        return window;
    }

    /// <summary>
    /// The whole of both pictures on one grid, or with <paramref name="region"/> what both show
    /// in that rectangle of the canvas: how the moved one has to be turned and scaled (if the
    /// model allows), then how it has to be shifted. Returns the transform of the canvas that
    /// does it, or null if there is nothing to go by.
    /// </summary>
    static Affine2? Coarse(AlignInput reference, AlignInput moving, AlignModel model, Box? region, CancellationToken cancel)
    {
        Box area = region ?? BoxOf(reference.Luma, reference.ToCanvas).Union(BoxOf(moving.Luma, moving.ToCanvas));
        if (area.IsEmpty) return null;
        double extent = Math.Max(area.Width, area.Height);
        if (!(extent >= 2)) return null;
        int n = Math.Clamp(Fft2D.PowerOfTwoBelow((int)extent), 64, CoarseSize);
        double step = extent / n, centreX = (area.Left + area.Right) / 2, centreY = (area.Top + area.Bottom) / 2;
        Affine2 gridToCanvas = Affine2.ScaleTranslate(step, step, centreX - n * step / 2, centreY - n * step / 2);

        float[] samples = ArrayPool<float>.Shared.Rent(n * n), weights = ArrayPool<float>.Shared.Rent(n * n);
        try
        {
            fixed (float* s = samples, w = weights)
            {
                if (!SampleWindowed(reference.Luma, reference.ToCanvas, gridToCanvas, n, s, w, region)) return null;
                using PhaseCorrelation.Spectrum first = PhaseCorrelation.Transform(s, n, n, parallel: true);

                // The moved image as it is, and, if it may be turned, turned by what the spectra
                // say or by that and half a turn: the spectra cannot tell those two apart.
                Span<Affine2> candidates = stackalloc Affine2[3];
                int count = 0;
                candidates[count++] = Affine2.Identity;
                if (model != AlignModel.Shift && SampleWindowed(moving.Luma, moving.ToCanvas, gridToCanvas, n, s, w, region))
                {
                    using PhaseCorrelation.Spectrum second = PhaseCorrelation.Transform(s, n, n, parallel: true);
                    if (TurnAndScale(first, second, out double degrees, out double scale))
                    {
                        if (model == AlignModel.ShiftRotation) scale = 1;
                        candidates[count++] = AlignPose.TurnAbout(centreX, centreY, -degrees, 1 / scale);
                        candidates[count++] = AlignPose.TurnAbout(centreX, centreY, 180 - degrees, 1 / scale);
                    }
                }

                Affine2? best = null;
                double bestHeight = 0;
                for (int c = 0; c < count; c++)
                {
                    if (cancel.IsCancellationRequested) return null;
                    if (!SampleWindowed(moving.Luma, candidates[c].After(moving.ToCanvas), gridToCanvas, n, s, w, region)) continue;
                    using PhaseCorrelation.Spectrum second = PhaseCorrelation.Transform(s, n, n, parallel: true);
                    PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(first, second, parallel: true);
                    double height = c == 2 ? peak.Height / 1.25 : peak.Height; // half a turn has to be clearly the better one
                    if (best != null && height <= bestHeight) continue;
                    bestHeight = height;
                    // The moved picture is the other one shifted by the peak: it goes back by as much.
                    best = Affine2.ScaleTranslate(1, 1, -peak.Dx * step, -peak.Dy * step).After(candidates[c]);
                }
                return bestHeight > 0 ? best : null;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
            ArrayPool<float>.Shared.Return(weights);
        }
    }

    /// <summary>
    /// An image on an n x n grid, ready for the Fourier transform: less its mean, in units of
    /// its spread, and faded out towards its own edges, so that it is the image's content and
    /// not its outline that shows in the spectrum. The fade belongs to the image and turns with
    /// it. With <paramref name="region"/> the fade is over that rectangle of the canvas instead,
    /// the same for both images, and nothing outside it is left. False if hardly any of the
    /// image is on the grid, or it is flat.
    /// </summary>
    static bool SampleWindowed(LumaPyramid luma, in Affine2 toCanvas, in Affine2 gridToCanvas, int n, float* samples, float* weights, Box? region)
    {
        Affine2 gridToStored = toCanvas.Inverse().After(gridToCanvas);
        luma.Sample(samples, n, n, gridToStored);
        float[] window = Window;
        // Where on the fade a sample lies, 0 .. 1024 either way: across the image, or across the region.
        double length = window.Length;
        Affine2 gridToFade = region is { } r
            ? Affine2.ScaleTranslate(length / r.Width, length / r.Height, -r.Left * length / r.Width, -r.Top * length / r.Height).After(gridToCanvas)
            : Affine2.ScaleTranslate(length / luma.Width, length / luma.Height, 0, 0).After(gridToStored);
        double sum = 0, sumWeights = 0;
        for (int j = 0; j < n; j++)
        {
            (double startX, double startY) = gridToFade.Apply(0.5, j + 0.5);
            float* row = samples + (long)j * n, weightRow = weights + (long)j * n;
            for (int i = 0; i < n; i++)
            {
                float value = row[i];
                double x = startX + i * gridToFade.A, y = startY + i * gridToFade.B;
                if (float.IsNaN(value) || !(x >= 0 && x < length && y >= 0 && y < length))
                {
                    row[i] = weightRow[i] = 0;
                    continue;
                }
                float weight = window[(int)x] * window[(int)y];
                weightRow[i] = weight;
                sum += weight * value;
                sumWeights += weight;
            }
        }
        int count = n * n;
        if (!(sumWeights > count * 0.001)) return false;
        double mean = sum / sumWeights, variance = 0;
        for (int i = 0; i < count; i++)
        {
            double d = samples[i] - mean;
            variance += weights[i] * d * d;
        }
        variance /= sumWeights;
        if (!(variance > 0) || !double.IsFinite(variance)) return false;
        float scale = (float)(1 / Math.Sqrt(variance)), offset = (float)mean;
        for (int i = 0; i < count; i++) samples[i] = weights[i] * (samples[i] - offset) * scale;
        return true;
    }

    /// <summary>
    /// How much the second picture is turned (degrees, clockwise on screen) and scaled against
    /// the first, from their spectra. A turn is known only up to half a turn. False if the
    /// answer makes no sense.
    /// </summary>
    static bool TurnAndScale(PhaseCorrelation.Spectrum first, PhaseCorrelation.Spectrum second, out double degrees, out double scale)
    {
        degrees = 0;
        scale = 1;
        int n = first.Width, angles = n, radii = n / 2;
        // From a few cycles across the picture, below which the spectrum is the fade's, to
        // short of the finest detail the grid holds, where it is noise.
        double lowest = Math.Max(2, n / 64.0), highest = 0.45 * n, growth = Math.Log(highest / lowest) / radii;
        float[] a = ArrayPool<float>.Shared.Rent(angles * radii), b = ArrayPool<float>.Shared.Rent(angles * radii), magnitude = ArrayPool<float>.Shared.Rent(n * n);
        try
        {
            fixed (float* pa = a, pb = b, scratch = magnitude)
            {
                LogPolar(first, scratch, pa, angles, radii, lowest, growth);
                LogPolar(second, scratch, pb, angles, radii, lowest, growth);
                PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(pa, pb, radii, angles, parallel: true);
                if (!(peak.Height > 0)) return false;
                // The second map is the first one shifted by the turn along the angle, and by
                // minus the logarithm of the scaling along the radius.
                degrees = peak.Dy * 180.0 / angles;
                scale = Math.Exp(-peak.Dx * growth);
                return scale is > 0.2 and < 5;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
            ArrayPool<float>.Shared.Return(magnitude);
        }
    }

    /// <summary>
    /// The size of a spectrum, as logarithm, laid out by angle (rows: half a turn, which is all
    /// there is, the other half being its mirror image) and by the logarithm of the radius
    /// (columns). What is the same at every angle is taken out, and the map fades out towards
    /// the smallest and the largest radius.
    /// </summary>
    static void LogPolar(PhaseCorrelation.Spectrum spectrum, float* magnitude, float* map, int angles, int radii, double lowest, double growth)
    {
        int n = spectrum.Width, mask = n - 1;
        fixed (float* re = spectrum.Re, im = spectrum.Im)
            for (int i = 0; i < n * n; i++) magnitude[i] = MathF.Log(1 + MathF.Sqrt(re[i] * re[i] + im[i] * im[i]));

        Span<double> radius = radii <= 512 ? stackalloc double[radii] : new double[radii];
        for (int m = 0; m < radii; m++) radius[m] = lowest * Math.Exp(m * growth);
        for (int k = 0; k < angles; k++)
        {
            double angle = Math.PI * k / angles, c = Math.Cos(angle), s = Math.Sin(angle);
            float* row = map + (long)k * radii;
            for (int m = 0; m < radii; m++)
            {
                double u = radius[m] * c, v = radius[m] * s;
                int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
                float tx = (float)(u - x0), ty = (float)(v - y0);
                // Frequencies below zero are at the far end of the transform's rows and columns.
                float* upper = magnitude + (long)(y0 & mask) * n, lower = magnitude + (long)((y0 + 1) & mask) * n;
                int xa = x0 & mask, xb = (x0 + 1) & mask;
                float top = upper[xa] + (upper[xb] - upper[xa]) * tx, bottom = lower[xa] + (lower[xb] - lower[xa]) * tx;
                row[m] = top + (bottom - top) * ty;
            }
        }
        for (int m = 0; m < radii; m++)
        {
            double sum = 0;
            for (int k = 0; k < angles; k++) sum += map[(long)k * radii + m];
            float mean = (float)(sum / angles), fade = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (m + 0.5) / radii));
            for (int k = 0; k < angles; k++) map[(long)k * radii + m] = (map[(long)k * radii + m] - mean) * fade;
        }
    }

    // ---------------------------------------------------------------- refining, from coarse to fine

    struct PatchShift
    {
        /// <summary>The patch's centre on the canvas.</summary>
        public double X, Y;

        /// <summary>What is there in the reference is found this far away in the moved image, in canvas pixels.</summary>
        public double Dx, Dy;

        public double Height;
    }

    /// <summary>
    /// Takes off, size by size, what is left between the two images. <paramref name="current"/>
    /// is the transform of the canvas the moved image is under so far; the better one is returned.
    /// With <paramref name="region"/> the patches lie inside that rectangle of the canvas.
    /// </summary>
    static Affine2 Refine(AlignInput reference, AlignInput moving, Affine2 current, AlignModel model, Box? region, out double confidence, out int used, CancellationToken cancel)
    {
        confidence = 0;
        used = 0;
        Box referenceBox = BoxOf(reference.Luma, reference.ToCanvas);
        // The coarsest size is the one at which what is looked at is some 512 samples across.
        Box union = region ?? referenceBox.Union(BoxOf(moving.Luma, current.After(moving.ToCanvas)));
        if (region is { } inside) referenceBox = referenceBox.Intersect(inside);
        if (referenceBox.IsEmpty) return current;
        int reduction = 1;
        while (reduction * CoarseSize < Math.Max(union.Width, union.Height)) reduction *= 2;
        Affine2 referenceToStored = reference.ToCanvas.Inverse();

        for (; reduction >= 1; reduction /= 2)
        {
            double step = reduction;
            for (int round = 0; round < 3; round++)
            {
                if (cancel.IsCancellationRequested) return current;
                Affine2 movingToCanvas = current.After(moving.ToCanvas);
                Box overlap = referenceBox.Intersect(BoxOf(moving.Luma, movingToCanvas));
                if (overlap.IsEmpty) return current;

                // Patches as large as fit twice into the overlap, half a patch apart.
                double across = overlap.Width / step, down = overlap.Height / step;
                int n = Math.Min(LargestPatch, Fft2D.PowerOfTwoBelow((int)(Math.Min(across, down) / 2)));
                if (n < 2 * SmallestPatch) n = Math.Min(LargestPatch, Fft2D.PowerOfTwoBelow((int)Math.Min(across, down)));
                if (n < SmallestPatch) break; // too small at this size: on to the next
                int columns = Math.Clamp((int)Math.Ceiling((across - n) / (n / 2.0)) + 1, 1, MostPatchesAcross);
                int rows = Math.Clamp((int)Math.Ceiling((down - n) / (n / 2.0)) + 1, 1, MostPatchesAcross);
                double size = n * step, spareX = overlap.Width - size, spareY = overlap.Height - size;

                var shifts = new PatchShift[columns * rows];
                Affine2 movingToStored = movingToCanvas.Inverse();
                Parallel.For(0, shifts.Length, index =>
                {
                    int column = index % columns, row = index / columns;
                    double x = overlap.Left + (columns > 1 ? column * spareX / (columns - 1) : spareX / 2);
                    double y = overlap.Top + (rows > 1 ? row * spareY / (rows - 1) : spareY / 2);
                    // The patch's samples on whole canvas pixels (whole steps, at a reduced size): a reference
                    // that lies pixel for pixel on the canvas is then read as it is, not interpolated,
                    // and interpolating shifts fine detail by a few hundredths of a pixel.
                    x = Math.Round(x / step) * step + size / 2;
                    y = Math.Round(y / step) * step + size / 2;
                    shifts[index] = Measure(reference.Luma, referenceToStored, moving.Luma, movingToStored, x, y, n, step);
                });

                // A peak has to stand clear of what two unrelated patches would give: the fewer samples, the higher that is.
                if (!Fit(shifts, model, step, Math.Min(0.5, 16.0 / n), out Affine2 map, out double clear, out int count)) break;
                confidence = clear;
                used = count;
                // The map takes a place in the reference to where the same thing is in the moved image: the moved image goes back by it.
                current = map.Inverse().After(current);

                double moved = 0;
                foreach (PatchShift shift in shifts)
                {
                    (double x, double y) = map.Apply(shift.X, shift.Y);
                    moved = Math.Max(moved, Math.Max(Math.Abs(x - shift.X), Math.Abs(y - shift.Y)));
                }
                if (moved < (reduction == 1 ? 0.01 : 0.1) * step) break; // nothing left at this size
            }
        }
        return current;
    }

    /// <summary>The shift between the two images in one patch of n x n samples, <paramref name="step"/> canvas pixels apart, centred on a canvas point.</summary>
    static PatchShift Measure(LumaPyramid reference, in Affine2 referenceToStored, LumaPyramid moving, in Affine2 movingToStored, double x, double y, int n, double step)
    {
        var result = new PatchShift { X = x, Y = y };
        int count = n * n;
        float[] a = ArrayPool<float>.Shared.Rent(count), b = ArrayPool<float>.Shared.Rent(count);
        try
        {
            fixed (float* pa = a, pb = b)
            {
                Affine2 gridToCanvas = Affine2.ScaleTranslate(step, step, x - n * step / 2, y - n * step / 2);
                reference.Sample(pa, n, n, referenceToStored.After(gridToCanvas));
                moving.Sample(pb, n, n, movingToStored.After(gridToCanvas));

                // Where both have pixels: each less its mean there, faded out towards the patch's edges.
                double sumA = 0, sumB = 0;
                int valid = 0;
                for (int i = 0; i < count; i++)
                {
                    if (float.IsNaN(pa[i]) || float.IsNaN(pb[i])) continue;
                    sumA += pa[i];
                    sumB += pb[i];
                    valid++;
                }
                if (valid < count * 0.6) return result;
                float meanA = (float)(sumA / valid), meanB = (float)(sumB / valid);
                float[] window = Window;
                double per = window.Length / (double)n;
                for (int j = 0; j < n; j++)
                {
                    float fadeY = window[(int)((j + 0.5) * per)];
                    for (int i = 0; i < n; i++)
                    {
                        int k = j * n + i;
                        if (float.IsNaN(pa[k]) || float.IsNaN(pb[k]))
                        {
                            pa[k] = pb[k] = 0;
                            continue;
                        }
                        float fade = fadeY * window[(int)((i + 0.5) * per)];
                        pa[k] = (pa[k] - meanA) * fade;
                        pb[k] = (pb[k] - meanB) * fade;
                    }
                }
                PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(pa, pb, n, n);
                result.Dx = peak.Dx * step;
                result.Dy = peak.Dy * step;
                result.Height = peak.Height;
                return result;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
        }
    }

    /// <summary>
    /// The move, turn and scaling (as far as the model allows) that takes each patch's centre to
    /// where its shift says, as nearly as one transform can for all of them: least squares,
    /// clearer peaks counting for more, and patches that disagree with the rest left out.
    /// False if no patch has a peak of at least <paramref name="leastHeight"/>.
    /// </summary>
    static bool Fit(PatchShift[] shifts, AlignModel model, double step, double leastHeight, out Affine2 map, out double confidence, out int used)
    {
        map = Affine2.Identity;
        confidence = 0;
        used = 0;
        Span<bool> use = shifts.Length <= 64 ? stackalloc bool[shifts.Length] : new bool[shifts.Length];
        Span<double> residuals = shifts.Length <= 64 ? stackalloc double[shifts.Length] : new double[shifts.Length];
        double highest = 0;
        foreach (PatchShift shift in shifts) highest = Math.Max(highest, shift.Height);
        for (int i = 0; i < shifts.Length; i++) use[i] = shifts[i].Height >= leastHeight && shifts[i].Height >= 0.25 * highest;

        for (int pass = 0; pass < 3; pass++)
        {
            if (!Solve(shifts, use, model, step, out map)) return false;
            int count = 0;
            for (int i = 0; i < shifts.Length; i++)
            {
                if (!use[i]) continue;
                (double x, double y) = map.Apply(shifts[i].X, shifts[i].Y);
                double ex = x - (shifts[i].X + shifts[i].Dx), ey = y - (shifts[i].Y + shifts[i].Dy);
                residuals[count++] = Math.Sqrt(ex * ex + ey * ey);
            }
            Span<double> sorted = residuals[..count];
            sorted.Sort();
            double limit = Math.Max(3 * sorted[count / 2], 0.3 * step);
            bool dropped = false;
            for (int i = 0; i < shifts.Length; i++)
            {
                if (!use[i]) continue;
                (double x, double y) = map.Apply(shifts[i].X, shifts[i].Y);
                double ex = x - (shifts[i].X + shifts[i].Dx), ey = y - (shifts[i].Y + shifts[i].Dy);
                if (Math.Sqrt(ex * ex + ey * ey) <= limit) continue;
                use[i] = false;
                dropped = true;
            }
            if (!dropped) break;
        }
        if (!Solve(shifts, use, model, step, out map)) return false;
        for (int i = 0; i < shifts.Length; i++)
        {
            if (!use[i]) continue;
            used++;
            confidence += shifts[i].Height;
        }
        confidence /= used;
        return true;
    }

    static bool Solve(PatchShift[] shifts, ReadOnlySpan<bool> use, AlignModel model, double step, out Affine2 map)
    {
        map = Affine2.Identity;
        double weights = 0, px = 0, py = 0, qx = 0, qy = 0;
        int count = 0;
        for (int i = 0; i < shifts.Length; i++)
        {
            if (!use[i]) continue;
            double w = shifts[i].Height * shifts[i].Height;
            weights += w;
            px += w * shifts[i].X;
            py += w * shifts[i].Y;
            qx += w * (shifts[i].X + shifts[i].Dx);
            qy += w * (shifts[i].Y + shifts[i].Dy);
            count++;
        }
        if (count == 0 || !(weights > 0)) return false;
        px /= weights;
        py /= weights;
        qx /= weights;
        qy /= weights;

        double spread = 0, dot = 0, cross = 0;
        for (int i = 0; i < shifts.Length; i++)
        {
            if (!use[i]) continue;
            double w = shifts[i].Height * shifts[i].Height;
            double ax = shifts[i].X - px, ay = shifts[i].Y - py, bx = shifts[i].X + shifts[i].Dx - qx, by = shifts[i].Y + shifts[i].Dy - qy;
            spread += w * (ax * ax + ay * ay);
            dot += w * (ax * bx + ay * by);
            cross += w * (ax * by - ay * bx);
        }
        // One patch, or patches all in one place, say how far to move and nothing else.
        double a = 1, b = 0;
        if (model != AlignModel.Shift && count >= 2 && spread > weights * (8 * step) * (8 * step))
        {
            if (model == AlignModel.ShiftRotation)
            {
                double length = Math.Sqrt(dot * dot + cross * cross);
                if (length > 0) (a, b) = (dot / length, cross / length);
            }
            else
            {
                (a, b) = (dot / spread, cross / spread);
            }
        }
        map = new Affine2(a, b, -b, a, qx - (a * px - b * py), qy - (b * px + a * py));
        return true;
    }
}
