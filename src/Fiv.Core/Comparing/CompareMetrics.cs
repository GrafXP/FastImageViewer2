using System.Diagnostics;
using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>The units the numbers of a comparison are in.</summary>
public enum ValueUnit
{
    /// <summary>Both images hold 8-bit samples: values are 0..255, as in the files.</summary>
    Bits8,

    /// <summary>Both hold 16-bit samples: 0..65535.</summary>
    Bits16,

    /// <summary>Both hold float samples: the stored values.</summary>
    Float,

    /// <summary>The two differ in depth: integer samples are taken as 0..1, float ones as stored.</summary>
    Normalised,
}

/// <summary>
/// How often each difference (second image less first) occurs. For 8- and 16-bit pairs every bin
/// is one whole difference and the counts are exact; for float and mixed pairs the range between
/// the largest negative and the largest positive difference is cut into bins.
/// </summary>
public sealed class DifferenceHistogram
{
    public long[] Counts { get; }

    /// <summary>The difference in the middle of bin 0.</summary>
    public double Lowest { get; }

    public double BinWidth { get; }
    public bool Exact { get; }
    public long Total { get; }

    public DifferenceHistogram(long[] counts, double lowest, double binWidth, bool exact)
    {
        Counts = counts;
        Lowest = lowest;
        BinWidth = binWidth;
        Exact = exact;
        long total = 0;
        foreach (long count in counts) total += count;
        Total = total;
    }

    /// <summary>The difference in the middle of a bin.</summary>
    public double ValueAt(int bin) => Lowest + bin * BinWidth;

    /// <summary>The share, 0..1, of pixels whose difference is larger in size than <paramref name="threshold"/>.</summary>
    public double FractionBeyond(double threshold)
    {
        if (Total == 0) return 0;
        long beyond = 0;
        for (int i = 0; i < Counts.Length; i++)
            if (Counts[i] != 0 && Math.Abs(ValueAt(i)) > threshold) beyond += Counts[i];
        return (double)beyond / Total;
    }
}

/// <summary>The numbers for one channel of a compared pair.</summary>
public sealed class ChannelMetrics
{
    public const string Value = "value", Red = "red", Green = "green", Blue = "blue", Alpha = "alpha", Luma = "luma", All = "all";

    public required string Name { get; init; }

    /// <summary>Mean absolute error.</summary>
    public double Mae { get; init; }

    /// <summary>Mean squared error.</summary>
    public double Mse { get; init; }

    public double Rmse => Math.Sqrt(Mse);

    /// <summary>Peak signal-to-noise ratio in decibels; infinite for identical images.</summary>
    public double Psnr { get; init; }

    public double MaxError { get; init; }

    /// <summary>The mean of second less first: which way, on the whole, the second image is off.</summary>
    public double MeanDifference { get; init; }

    /// <summary>Mean structural similarity; NaN where it was not computed or is not defined.</summary>
    public double Ssim { get; init; } = double.NaN;

    public required DifferenceHistogram Histogram { get; init; }
}

/// <summary>What <see cref="CompareMetrics.Compute"/> found.</summary>
public sealed class MetricsResult
{
    /// <summary>The canvas pixels that were compared: the region asked for, cut to where both images have pixels.</summary>
    public CanvasRect Area { get; init; }

    public long Pixels { get; init; }

    /// <summary>Pixels left out because a float sample was not a finite number.</summary>
    public long Ignored { get; init; }

    public ValueUnit Unit { get; init; }

    /// <summary>The value range PSNR and SSIM refer to: 255, 65535, 1, or for float data the span of the values met.</summary>
    public double Peak { get; init; }

    /// <summary>False if one image had to be resampled to lie over the other (different sizes, scaled to fit).</summary>
    public bool PixelAligned { get; init; }

    /// <summary>Grey pair: one entry, "value". Colour: red, green, blue, alpha if either image is not opaque, luma, and all.</summary>
    public IReadOnlyList<ChannelMetrics> Channels { get; init; } = [];

    /// <summary>The entry that stands for the whole pixel: "all" for colour, "value" for grey. Null if nothing was compared.</summary>
    public ChannelMetrics? Main => Channels.Count > 0 ? Channels[^1] : null;

    public ChannelMetrics? Find(string name)
    {
        foreach (ChannelMetrics channel in Channels)
            if (channel.Name == name) return channel;
        return null;
    }

    /// <summary>
    /// Structural similarity per pixel (of luma, or of the value), as 32-bit float covering
    /// <see cref="SsimArea"/>; null unless asked for. Owned by whoever holds the result.
    /// </summary>
    public DecodedImage? SsimMap { get; init; }

    public CanvasRect SsimArea { get; init; }

    /// <summary>How long the error numbers and the structural similarity took.</summary>
    public double BasicMs { get; init; }
    public double SsimMs { get; init; }
}

/// <summary>
/// The numbers of a comparison, computed from the full-resolution pixels as the files have them,
/// never from what is on screen. Differences are the second image less the first.
/// </summary>
public static unsafe class CompareMetrics
{
    const int BandRows = 16;

    /// <summary>The number of bins, an odd number so that zero is the middle of one, for pairs whose differences are not whole numbers.</summary>
    const int FloatBins = 4095;

    /// <summary>The units two layouts are compared in, and what a stored sample of each is multiplied by to get there.</summary>
    public static (ValueUnit Unit, float ScaleFirst, float ScaleSecond) Units(PixelLayout first, PixelLayout second)
    {
        int a = CompareSource.DepthOf(first), b = CompareSource.DepthOf(second);
        if (a == b) return (a == 8 ? ValueUnit.Bits8 : a == 16 ? ValueUnit.Bits16 : ValueUnit.Float, 1f, 1f);
        return (ValueUnit.Normalised, Normalising(a), Normalising(b));

        static float Normalising(int depth) => depth == 8 ? 1f / 255 : depth == 16 ? 1f / 65535 : 1f;
    }

    /// <summary>What one step of a unit is in the units the GPU samples (0..1 for integer layouts): 1/255, 1/65535, or 1.</summary>
    public static float UnitStep(ValueUnit unit) => unit switch
    {
        ValueUnit.Bits8 => 1f / 255,
        ValueUnit.Bits16 => 1f / 65535,
        _ => 1f,
    };

    sealed class Sums
    {
        public readonly double[] Abs, Squares, Signed, Max;
        public readonly int[][]? Bins;
        public readonly float[] Rows;
        public double Lowest = double.PositiveInfinity, Highest = double.NegativeInfinity;
        public long Pixels, Ignored;
        public bool NotOpaque;

        public Sums(int channels, int width, int bins)
        {
            Abs = new double[channels];
            Squares = new double[channels];
            Signed = new double[channels];
            Max = new double[channels];
            Rows = new float[width * 8];
            if (bins > 0)
            {
                Bins = new int[channels][];
                for (int c = 0; c < channels; c++) Bins[c] = new int[bins];
            }
        }
    }

    /// <summary>
    /// Compares two images over <paramref name="region"/> (null: wherever both have pixels).
    /// With <paramref name="ssim"/> the structural similarity of every channel is computed too,
    /// which takes several times as long as the rest; with <paramref name="ssimMap"/> its
    /// per-pixel map over the whole common area as well. Returns null if cancelled.
    /// <paramref name="early"/> is given the result without the structural similarity as soon
    /// as there is one, on the calling thread, if more is still to come.
    /// </summary>
    public static MetricsResult? Compute(CompareSource first, CompareSource second, CanvasRect? region, bool ssim, bool ssimMap,
        CancellationToken cancel = default, Action<MetricsResult>? early = null)
    {
        long started = Stopwatch.GetTimestamp();
        CanvasRect common = first.Bounds.Intersect(second.Bounds);
        CanvasRect area = region is { } wanted ? common.Intersect(wanted) : common;
        (ValueUnit unit, float scaleFirst, float scaleSecond) = Units(first.Image.Layout, second.Image.Layout);
        bool colour = first.IsColor || second.IsColor, anyFloat = first.Depth == 0 || second.Depth == 0;
        bool aligned = first.PixelAligned && second.PixelAligned;
        double fixedPeak = unit switch
        {
            ValueUnit.Bits8 => 255,
            ValueUnit.Bits16 => 65535,
            _ => 1,
        };
        if (area.IsEmpty) return new MetricsResult { Area = area, Unit = unit, Peak = fixedPeak, PixelAligned = aligned };

        // Whole differences can be counted exactly in one pass. Otherwise the size of the
        // largest difference has to be known before bins can be laid out: two passes.
        bool exact = unit is ValueUnit.Bits8 or ValueUnit.Bits16 && aligned;
        int channels = colour ? 6 : 1;
        Sums? sums;
        float[] binsPerUnit = new float[channels];
        int half;
        if (exact)
        {
            half = (int)fixedPeak;
            Array.Fill(binsPerUnit, 1f);
            sums = Pass(first, second, area, scaleFirst, scaleSecond, colour, anyFloat, 2 * half + 1, half, binsPerUnit, cancel);
        }
        else
        {
            half = FloatBins / 2;
            Sums? ranges = Pass(first, second, area, scaleFirst, scaleSecond, colour, anyFloat, 0, 0, binsPerUnit, cancel);
            if (ranges == null) return null;
            for (int c = 0; c < channels; c++) binsPerUnit[c] = ranges.Max[c] > 0 ? (float)(half / ranges.Max[c]) : 0f;
            sums = Pass(first, second, area, scaleFirst, scaleSecond, colour, anyFloat, FloatBins, half, binsPerUnit, cancel);
        }
        if (sums == null) return null;

        double peak = fixedPeak;
        if (anyFloat) peak = sums.Highest > sums.Lowest ? sums.Highest - sums.Lowest : 1;
        double basicMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The histograms are the same for the early and the final result: made once.
        var histograms = new DifferenceHistogram[channels];
        for (int c = 0; c < channels; c++) histograms[c] = Histogram(sums.Bins![c], half, binsPerUnit[c], exact);
        double pixels = Math.Max(1, sums.Pixels);

        MetricsResult Result(double[]? similarity, DecodedImage? map, CanvasRect mapArea, double ssimMs)
        {
            ChannelMetrics Channel(string name, int c, double similarityOf) => new()
            {
                Name = name,
                Mae = sums.Abs[c] / pixels,
                Mse = sums.Squares[c] / pixels,
                Psnr = Psnr(sums.Squares[c] / pixels, peak),
                MaxError = sums.Max[c],
                MeanDifference = sums.Signed[c] / pixels,
                Ssim = similarityOf,
                Histogram = histograms[c],
            };
            double Similarity(int plane) => similarity != null ? similarity[plane] : double.NaN;

            var list = new List<ChannelMetrics>();
            if (!colour)
            {
                list.Add(Channel(ChannelMetrics.Value, 0, Similarity(0)));
            }
            else
            {
                list.Add(Channel(ChannelMetrics.Red, 0, Similarity(0)));
                list.Add(Channel(ChannelMetrics.Green, 1, Similarity(1)));
                list.Add(Channel(ChannelMetrics.Blue, 2, Similarity(2)));
                if (sums.NotOpaque || sums.Max[3] > 0) list.Add(Channel(ChannelMetrics.Alpha, 3, double.NaN));
                list.Add(Channel(ChannelMetrics.Luma, 4, Similarity(3)));
                // All: the three colour channels together. Its histogram is of the channel that
                // differs most in each pixel, so "over the threshold" means "in any channel".
                double mse = (sums.Squares[0] + sums.Squares[1] + sums.Squares[2]) / (3 * pixels);
                list.Add(new ChannelMetrics
                {
                    Name = ChannelMetrics.All,
                    Mae = (sums.Abs[0] + sums.Abs[1] + sums.Abs[2]) / (3 * pixels),
                    Mse = mse,
                    Psnr = Psnr(mse, peak),
                    MaxError = Math.Max(sums.Max[0], Math.Max(sums.Max[1], sums.Max[2])),
                    MeanDifference = (sums.Signed[0] + sums.Signed[1] + sums.Signed[2]) / (3 * pixels),
                    Ssim = similarity != null ? (similarity[0] + similarity[1] + similarity[2]) / 3 : double.NaN,
                    Histogram = histograms[5],
                });
            }
            return new MetricsResult
            {
                Area = area,
                Pixels = sums.Pixels,
                Ignored = sums.Ignored,
                Unit = unit,
                Peak = peak,
                PixelAligned = aligned,
                Channels = list,
                SsimMap = map,
                SsimArea = mapArea,
                BasicMs = basicMs,
                SsimMs = ssimMs,
            };
        }

        if (!ssim && !ssimMap) return Result(null, null, default, 0);
        early?.Invoke(Result(null, null, default, 0));

        // Structural similarity: red, green, blue and luma for colour, the value for grey.
        long ssimStarted = Stopwatch.GetTimestamp();
        DecodedImage? map = null;
        CanvasRect mapArea = default;
        bool mapHere = ssimMap && area == common; // one pass gives both the numbers and the map
        if (mapHere && Ssim.Fits(area)) map = new DecodedImage(area.Width, area.Height, PixelLayout.GrayFloat, area.Width, area.Height);
        double[]? similarity = Ssim.Compute(first, second, area, scaleFirst, scaleSecond, colour, peak, map, cancel);
        if (ssimMap && !mapHere && Ssim.Fits(common) && !cancel.IsCancellationRequested)
        {
            // A region was measured; the map is of the whole common area.
            map = new DecodedImage(common.Width, common.Height, PixelLayout.GrayFloat, common.Width, common.Height);
            Ssim.Compute(first, second, common, scaleFirst, scaleSecond, colour, peak, map, cancel, mapOnly: true);
        }
        if (map != null) mapArea = mapHere ? area : common;
        if (cancel.IsCancellationRequested)
        {
            map?.Dispose();
            return null;
        }
        return Result(similarity, map, mapArea, Stopwatch.GetElapsedTime(ssimStarted).TotalMilliseconds);
    }
    static double Psnr(double mse, double peak) => mse > 0 ? 10 * Math.Log10(peak * peak / mse) : double.PositiveInfinity;

    static DifferenceHistogram Histogram(int[] bins, int half, float binsPerUnit, bool exact)
    {
        var counts = new long[bins.Length];
        for (int i = 0; i < bins.Length; i++) counts[i] = bins[i];
        double width = exact ? 1 : binsPerUnit > 0 ? 1.0 / binsPerUnit : 1;
        return new DifferenceHistogram(counts, -half * width, width, exact);
    }

    /// <summary>
    /// One pass over the area: sums of the differences, their largest size, and, with
    /// <paramref name="bins"/> above zero, how often each occurs (bin = difference * binsPerUnit + half).
    /// Channels: the value for grey; red, green, blue, alpha, luma and "the colour channel that
    /// differs most" for colour.
    /// </summary>
    static Sums? Pass(CompareSource first, CompareSource second, CanvasRect area, float scaleFirst, float scaleSecond, bool colour, bool checkFinite,
        int bins, int half, float[] binsPerUnit, CancellationToken cancel)
    {
        int width = area.Width, channels = colour ? 6 : 1;
        int bands = (area.Height + BandRows - 1) / BandRows;
        var total = new Sums(channels, 0, bins);
        float opaqueFirst = first.FullValue * scaleFirst, opaqueSecond = second.FullValue * scaleSecond;
        float wr = DifferenceMath.LumaWeights.X, wg = DifferenceMath.LumaWeights.Y, wb = DifferenceMath.LumaWeights.Z;

        Parallel.For(0, bands, () => new Sums(channels, width, bins), (band, state, sums) =>
        {
            if (cancel.IsCancellationRequested)
            {
                state.Stop();
                return sums;
            }
            int top = area.Y + band * BandRows, bottom = Math.Min(area.Bottom, top + BandRows);
            float* d = stackalloc float[6];
            fixed (float* rows = sums.Rows)
            {
                float* ar = rows, ag = rows + width, ab = rows + 2 * width, aa = rows + 3 * width;
                float* br = rows + 4 * width, bg = rows + 5 * width, bb = rows + 6 * width, ba = rows + 7 * width;
                for (int y = top; y < bottom; y++)
                {
                    first.ReadRow(y, area.X, width, ar, ag, ab, aa, scaleFirst);
                    second.ReadRow(y, area.X, width, br, bg, bb, ba, scaleSecond);
                    for (int i = 0; i < width; i++)
                    {
                        if (colour)
                        {
                            d[0] = br[i] - ar[i];
                            d[1] = bg[i] - ag[i];
                            d[2] = bb[i] - ab[i];
                            d[3] = ba[i] - aa[i];
                            d[4] = d[0] * wr + d[1] * wg + d[2] * wb;
                            float sr = MathF.Abs(d[0]), sg = MathF.Abs(d[1]), sb = MathF.Abs(d[2]);
                            d[5] = sr >= sg && sr >= sb ? d[0] : sg >= sb ? d[1] : d[2];
                            if (aa[i] != opaqueFirst || ba[i] != opaqueSecond) sums.NotOpaque = true;
                        }
                        else
                        {
                            d[0] = br[i] - ar[i];
                        }
                        if (checkFinite)
                        {
                            if (!float.IsFinite(colour ? d[4] + d[3] : d[0]))
                            {
                                sums.Ignored++;
                                continue;
                            }
                            float low = MathF.Min(ar[i], br[i]), high = MathF.Max(ar[i], br[i]);
                            if (colour)
                            {
                                low = MathF.Min(low, MathF.Min(MathF.Min(ag[i], bg[i]), MathF.Min(ab[i], bb[i])));
                                high = MathF.Max(high, MathF.Max(MathF.Max(ag[i], bg[i]), MathF.Max(ab[i], bb[i])));
                            }
                            if (low < sums.Lowest) sums.Lowest = low;
                            if (high > sums.Highest) sums.Highest = high;
                        }
                        for (int c = 0; c < channels; c++)
                        {
                            float value = d[c], size = MathF.Abs(value);
                            sums.Abs[c] += size;
                            sums.Squares[c] += (double)value * value;
                            sums.Signed[c] += value;
                            if (size > sums.Max[c]) sums.Max[c] = size;
                            if (sums.Bins != null)
                            {
                                int[] counts = sums.Bins[c];
                                counts[Math.Clamp((int)MathF.Round(value * binsPerUnit[c]) + half, 0, counts.Length - 1)]++;
                            }
                        }
                    }
                    sums.Pixels += width;
                }
            }
            return sums;
        }, sums =>
        {
            lock (total)
            {
                for (int c = 0; c < channels; c++)
                {
                    total.Abs[c] += sums.Abs[c];
                    total.Squares[c] += sums.Squares[c];
                    total.Signed[c] += sums.Signed[c];
                    total.Max[c] = Math.Max(total.Max[c], sums.Max[c]);
                    if (total.Bins != null)
                    {
                        int[] to = total.Bins[c], from = sums.Bins![c];
                        for (int i = 0; i < to.Length; i++) to[i] += from[i];
                    }
                }
                total.Lowest = Math.Min(total.Lowest, sums.Lowest);
                total.Highest = Math.Max(total.Highest, sums.Highest);
                total.Pixels += sums.Pixels - sums.Ignored;
                total.Ignored += sums.Ignored;
                total.NotOpaque |= sums.NotOpaque;
            }
        });
        return cancel.IsCancellationRequested ? null : total;
    }
}
