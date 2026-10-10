using System.Diagnostics;
using Fiv.Comparing;

namespace Fiv.Measuring;

/// <summary>
/// How often each value occurs among the pixels of an area. For 8- and 16-bit images every bin
/// is one whole value; for float images, and where the values were scaled to be compared with
/// those of another depth, the range between the smallest and the largest is cut into bins.
/// </summary>
public sealed class ValueHistogram
{
    public long[] Counts { get; }

    /// <summary>The value in the middle of bin 0.</summary>
    public double Lowest { get; }

    public double BinWidth { get; }

    /// <summary>True if every bin is one whole value.</summary>
    public bool Whole { get; }

    public long Total { get; }

    public ValueHistogram(long[] counts, double lowest, double binWidth, bool whole)
    {
        Counts = counts;
        Lowest = lowest;
        BinWidth = binWidth;
        Whole = whole;
        long total = 0;
        foreach (long count in counts) total += count;
        Total = total;
    }

    /// <summary>The value in the middle of a bin.</summary>
    public double ValueAt(int bin) => Lowest + bin * BinWidth;

    /// <summary>The value half of the pixels do not exceed: the lower of the two middle ones if their number is even, and as fine as the bins are.</summary>
    public double Median
    {
        get
        {
            if (Total == 0) return double.NaN;
            long target = (Total + 1) / 2, seen = 0;
            for (int i = 0; i < Counts.Length; i++)
            {
                seen += Counts[i];
                if (seen >= target) return ValueAt(i);
            }
            return ValueAt(Counts.Length - 1);
        }
    }
}

/// <summary>The numbers for one channel of the pixels in an area.</summary>
public sealed class ChannelStatistics
{
    public required string Name { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }

    /// <summary>The spread of these pixels about their mean (divided by their number, not by one less).</summary>
    public double StdDev { get; init; }

    public double Median { get; init; }
    public required ValueHistogram Histogram { get; init; }
}

/// <summary>
/// The pixels inside a rectangle or a circle: how many, and for every channel the smallest and
/// largest value, mean, spread, median and histogram. Taken from the full-resolution pixels as
/// the file has them; a pixel belongs to the area if its centre is inside.
/// </summary>
public sealed unsafe class AreaStatistics
{
    const int BandRows = 16;

    /// <summary>The number of bins for values that are not whole numbers.</summary>
    public const int FloatBins = 1024;

    public MeasureShape Shape { get; private init; }

    /// <summary>The whole pixels the numbers can be of: the shape's, cut to where the image is.</summary>
    public CanvasRect Bounds { get; private init; }

    /// <summary>The pixels counted: those inside the shape that the image has.</summary>
    public long Pixels { get; private init; }

    /// <summary>Pixels left out because a float sample was not a finite number.</summary>
    public long Ignored { get; private init; }

    /// <summary>The value of white: 255, 65535 or 1, times the scale the values were taken with.</summary>
    public double FullValue { get; private init; }

    /// <summary>True if the values are whole numbers as stored, so that every bin of the histograms is one value.</summary>
    public bool Whole { get; private init; }

    /// <summary>False if the image is scaled, turned or moved by part of a pixel on the grid: its pixels were blended.</summary>
    public bool PixelAligned { get; private init; }

    /// <summary>Grey: one entry, "value". Colour: red, green, blue, alpha if not every pixel is opaque, and luma.</summary>
    public IReadOnlyList<ChannelStatistics> Channels { get; private init; } = [];

    public double Milliseconds { get; private init; }

    public ChannelStatistics? Find(string name)
    {
        foreach (ChannelStatistics channel in Channels)
            if (channel.Name == name) return channel;
        return null;
    }

    sealed class Sums
    {
        public readonly double[] Sum, Squares;
        public readonly float[] Min, Max;
        public readonly int[][]? Bins;
        public readonly float[] Rows;
        public long Pixels, Ignored;
        public bool NotOpaque;

        public Sums(int channels, int width, int bins)
        {
            Sum = new double[channels];
            Squares = new double[channels];
            Min = new float[channels];
            Max = new float[channels];
            Array.Fill(Min, float.PositiveInfinity);
            Array.Fill(Max, float.NegativeInfinity);
            Rows = new float[width * 4];
            if (bins > 0)
            {
                Bins = new int[channels][];
                for (int c = 0; c < channels; c++) Bins[c] = new int[bins];
            }
        }
    }

    /// <summary>
    /// Takes the numbers of the pixels of <paramref name="source"/> inside
    /// <paramref name="shape"/>, which is in the coordinates the source's transform leads to.
    /// Each stored value is multiplied by <paramref name="scale"/>. Returns null if cancelled.
    /// </summary>
    public static AreaStatistics? Compute(CompareSource source, MeasureShape shape, float scale = 1, CancellationToken cancel = default)
    {
        long started = Stopwatch.GetTimestamp();
        CanvasRect area = shape.HasArea ? shape.PixelBounds.Intersect(source.Bounds) : default;
        bool colour = source.IsColor, whole = source.Depth != 0 && scale == 1;
        int channels = colour ? 5 : 1;
        AreaStatistics Empty() => new()
        {
            Shape = shape, Bounds = area, FullValue = source.FullValue * scale, Whole = whole, PixelAligned = source.PixelAligned,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
        if (area.IsEmpty) return Empty();

        // Whole values can be counted in one pass, a bin each. Otherwise the range has to be
        // known before bins can be laid out over it: two passes.
        float[] low = new float[channels], perUnit = new float[channels];
        Sums? sums;
        if (whole)
        {
            Array.Fill(perUnit, 1f);
            sums = Pass(source, shape, area, scale, colour, (int)source.FullValue + 1, low, perUnit, cancel);
        }
        else
        {
            Sums? ranges = Pass(source, shape, area, scale, colour, 0, low, perUnit, cancel);
            if (ranges == null) return null;
            for (int c = 0; c < channels; c++)
            {
                float span = ranges.Max[c] - ranges.Min[c];
                low[c] = float.IsFinite(ranges.Min[c]) ? ranges.Min[c] : 0;
                perUnit[c] = span > 0 && float.IsFinite(span) ? (FloatBins - 1) / span : 0;
            }
            sums = Pass(source, shape, area, scale, colour, FloatBins, low, perUnit, cancel);
        }
        if (sums == null) return null;
        if (sums.Pixels == 0) return new AreaStatistics
        {
            Shape = shape, Bounds = area, Ignored = sums.Ignored, FullValue = source.FullValue * scale, Whole = whole, PixelAligned = source.PixelAligned,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };

        double pixels = sums.Pixels;
        ChannelStatistics Channel(string name, int c)
        {
            var counts = new long[sums.Bins![c].Length];
            for (int i = 0; i < counts.Length; i++) counts[i] = sums.Bins[c][i];
            // A bin holds what rounds to it: luma, a weighted sum, is not whole even where red, green and blue are.
            var histogram = new ValueHistogram(counts, low[c], perUnit[c] > 0 ? 1.0 / perUnit[c] : 1, whole);
            double mean = sums.Sum[c] / pixels;
            return new ChannelStatistics
            {
                Name = name,
                Min = sums.Min[c],
                Max = sums.Max[c],
                Mean = mean,
                StdDev = Math.Sqrt(Math.Max(0, sums.Squares[c] / pixels - mean * mean)),
                Median = histogram.Median,
                Histogram = histogram,
            };
        }

        var list = new List<ChannelStatistics>();
        if (!colour)
        {
            list.Add(Channel(ChannelMetrics.Value, 0));
        }
        else
        {
            list.Add(Channel(ChannelMetrics.Red, 0));
            list.Add(Channel(ChannelMetrics.Green, 1));
            list.Add(Channel(ChannelMetrics.Blue, 2));
            if (sums.NotOpaque) list.Add(Channel(ChannelMetrics.Alpha, 3));
            list.Add(Channel(ChannelMetrics.Luma, 4));
        }
        return new AreaStatistics
        {
            Shape = shape,
            Bounds = area,
            Pixels = sums.Pixels,
            Ignored = sums.Ignored,
            FullValue = source.FullValue * scale,
            Whole = whole,
            PixelAligned = source.PixelAligned,
            Channels = list,
            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    /// <summary>
    /// One pass over the rows of the area: sums, smallest and largest value, and, with
    /// <paramref name="bins"/> above zero, how often each value occurs
    /// (bin = (value - low) * perUnit, rounded). Channels: the value for grey; red, green, blue,
    /// alpha and luma for colour.
    /// </summary>
    static Sums? Pass(CompareSource source, MeasureShape shape, CanvasRect area, float scale, bool colour, int bins, float[] low, float[] perUnit, CancellationToken cancel)
    {
        int width = area.Width, channels = colour ? 5 : 1;
        int bands = (area.Height + BandRows - 1) / BandRows;
        var total = new Sums(channels, 0, bins);
        float opaque = source.FullValue * scale;
        bool checkFinite = source.Depth == 0;
        float wr = DifferenceMath.LumaWeights.X, wg = DifferenceMath.LumaWeights.Y, wb = DifferenceMath.LumaWeights.Z;

        Parallel.For(0, bands, () => new Sums(channels, width, bins), (band, state, sums) =>
        {
            if (cancel.IsCancellationRequested)
            {
                state.Stop();
                return sums;
            }
            int top = area.Y + band * BandRows, bottom = Math.Min(area.Bottom, top + BandRows);
            float* v = stackalloc float[5];
            fixed (float* rows = sums.Rows)
            {
                float* r = rows, g = rows + width, b = rows + 2 * width, a = rows + 3 * width;
                for (int y = top; y < bottom; y++)
                {
                    // The pixels of the row that are inside the shape and that the image covers.
                    shape.RowSpan(y, out int left, out int right);
                    source.RowSpan(y, out int imageLeft, out int imageRight);
                    left = Math.Max(left, Math.Max(imageLeft, area.X));
                    right = Math.Min(right, Math.Min(imageRight, area.Right));
                    int count = right - left;
                    if (count <= 0) continue;
                    source.ReadRow(y, left, count, r, g, b, a, scale);
                    for (int i = 0; i < count; i++)
                    {
                        if (colour)
                        {
                            v[0] = r[i];
                            v[1] = g[i];
                            v[2] = b[i];
                            v[3] = a[i];
                            v[4] = r[i] * wr + g[i] * wg + b[i] * wb;
                            if (a[i] != opaque) sums.NotOpaque = true;
                        }
                        else
                        {
                            v[0] = r[i];
                        }
                        if (checkFinite && !float.IsFinite(colour ? v[4] + v[3] : v[0]))
                        {
                            sums.Ignored++;
                            continue;
                        }
                        for (int c = 0; c < channels; c++)
                        {
                            float value = v[c];
                            sums.Sum[c] += value;
                            sums.Squares[c] += (double)value * value;
                            if (value < sums.Min[c]) sums.Min[c] = value;
                            if (value > sums.Max[c]) sums.Max[c] = value;
                            if (sums.Bins != null)
                            {
                                int[] counts = sums.Bins[c];
                                counts[Math.Clamp((int)MathF.Round((value - low[c]) * perUnit[c]), 0, counts.Length - 1)]++;
                            }
                        }
                    }
                    sums.Pixels += count;
                }
            }
            return sums;
        }, sums =>
        {
            lock (total)
            {
                for (int c = 0; c < channels; c++)
                {
                    total.Sum[c] += sums.Sum[c];
                    total.Squares[c] += sums.Squares[c];
                    total.Min[c] = Math.Min(total.Min[c], sums.Min[c]);
                    total.Max[c] = Math.Max(total.Max[c], sums.Max[c]);
                    if (total.Bins != null)
                    {
                        int[] to = total.Bins[c], from = sums.Bins![c];
                        for (int i = 0; i < to.Length; i++) to[i] += from[i];
                    }
                }
                total.Pixels += sums.Pixels - sums.Ignored;
                total.Ignored += sums.Ignored;
                total.NotOpaque |= sums.NotOpaque;
            }
        });
        return cancel.IsCancellationRequested ? null : total;
    }
}
