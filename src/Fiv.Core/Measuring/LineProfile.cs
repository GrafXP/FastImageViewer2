using Fiv.Comparing;

namespace Fiv.Measuring;

/// <summary>What a set of values comes to: how many there are, the smallest and the largest, their mean and how far they spread about it.</summary>
public readonly record struct ValueSummary(long Count, double Min, double Max, double Mean, double StdDev)
{
    public static readonly ValueSummary None = new(0, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>Of the values that are numbers; the spread is that of the values themselves, not an estimate for a larger set they were drawn from.</summary>
    public static ValueSummary Of(ReadOnlySpan<float> values)
    {
        long count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) continue;
            count++;
            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }
        if (count == 0) return None;
        double mean = sum / count, squares = 0;
        foreach (float value in values)
            if (float.IsFinite(value)) squares += (value - mean) * (value - mean);
        return new ValueSummary(count, min, max, mean, Math.Sqrt(squares / count));
    }
}

/// <summary>The values of one channel along a line.</summary>
public sealed class ProfileChannel
{
    public required string Name { get; init; }

    /// <summary>One value per sample; not a number where the image has no pixel.</summary>
    public required float[] Values { get; init; }

    public ValueSummary Summary { get; init; }
}

/// <summary>
/// The values of an image along a line: one sample every pixel of length, or as near to that
/// as divides the line evenly, the first at its start and the last at its end. A sample is
/// blended from the four stored pixels around its point (see <see cref="CompareSource.Sample"/>),
/// so a line that runs along a row or a column through the centres of its pixels gives the
/// stored values exactly, and a slanted one does not skip or repeat pixels.
/// </summary>
public sealed unsafe class LineProfile
{
    /// <summary>More samples than this are not taken, however long the line.</summary>
    public const int MaxSamples = 1 << 20;

    public MeasureShape Line { get; }
    public int Count { get; }

    /// <summary>The distance from one sample to the next, in pixels.</summary>
    public double Step { get; }

    /// <summary>The samples that fell on the image.</summary>
    public int Covered { get; private init; }

    public bool IsColor { get; private init; }

    /// <summary>Grey: one entry, "value". Colour: red, green, blue, alpha and luma.</summary>
    public IReadOnlyList<ProfileChannel> Channels { get; private init; } = [];

    LineProfile(MeasureShape line, int count)
    {
        Line = line;
        Count = count;
        Step = count > 1 ? line.Length / (count - 1) : 0;
    }

    /// <summary>How far along the line a sample is, in pixels.</summary>
    public double DistanceAt(int index) => index * Step;

    /// <summary>Where on the grid a sample was taken.</summary>
    public (double X, double Y) PointAt(int index) => Point(Line, Count, index);

    static (double X, double Y) Point(in MeasureShape line, int count, int index)
    {
        double t = count > 1 ? (double)index / (count - 1) : 0;
        return (line.X0 + line.Dx * t, line.Y0 + line.Dy * t);
    }

    public ProfileChannel? Find(string name)
    {
        foreach (ProfileChannel channel in Channels)
            if (channel.Name == name) return channel;
        return null;
    }

    /// <summary>The number of samples a line of a length gets.</summary>
    public static int SamplesFor(double length) => (int)Math.Clamp(Math.Round(length) + 1, 2, MaxSamples);

    /// <summary>
    /// Samples <paramref name="source"/> along <paramref name="line"/>, which is in the
    /// coordinates the source's transform leads to. Each stored value is multiplied by
    /// <paramref name="scale"/>.
    /// </summary>
    public static LineProfile Sample(CompareSource source, MeasureShape line, float scale = 1)
    {
        int count = SamplesFor(line.Length);
        bool colour = source.IsColor;
        int channels = colour ? 5 : 1;
        var values = new float[channels][];
        for (int c = 0; c < channels; c++) values[c] = new float[count];

        float wr = DifferenceMath.LumaWeights.X, wg = DifferenceMath.LumaWeights.Y, wb = DifferenceMath.LumaWeights.Z;
        float* color = stackalloc float[4];
        int covered = 0;
        for (int i = 0; i < count; i++)
        {
            (double x, double y) = Point(line, count, i);
            if (!source.Sample(x, y, color))
            {
                for (int c = 0; c < channels; c++) values[c][i] = float.NaN;
                continue;
            }
            covered++;
            if (colour)
            {
                values[0][i] = color[0] * scale;
                values[1][i] = color[1] * scale;
                values[2][i] = color[2] * scale;
                values[3][i] = color[3] * scale;
                values[4][i] = (color[0] * wr + color[1] * wg + color[2] * wb) * scale;
            }
            else
            {
                values[0][i] = color[0] * scale;
            }
        }

        string[] names = colour
            ? [ChannelMetrics.Red, ChannelMetrics.Green, ChannelMetrics.Blue, ChannelMetrics.Alpha, ChannelMetrics.Luma]
            : [ChannelMetrics.Value];
        var list = new ProfileChannel[channels];
        for (int c = 0; c < channels; c++) list[c] = new ProfileChannel { Name = names[c], Values = values[c], Summary = ValueSummary.Of(values[c]) };
        return new LineProfile(line, count) { Covered = covered, IsColor = colour, Channels = list };
    }
}
