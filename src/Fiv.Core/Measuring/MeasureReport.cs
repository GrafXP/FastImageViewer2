using System.Globalization;
using System.Text;
using Fiv.Comparing;

namespace Fiv.Measuring;

/// <summary>
/// Measurements as text for other programs: the list, the values along a line, and the numbers
/// and histogram of an area, each as a CSV table with a header row. Where a number does not
/// apply or is not defined, its field is empty.
/// </summary>
public static class MeasureReport
{
    static string Number(double value) => MetricsReport.Number(value);

    /// <summary>
    /// One row per measurement, numbered as on screen. A line has its ends in x0, y0 and x1, y1,
    /// a rectangle its top left and bottom right corners, a circle its centre in x0, y0. With a
    /// <paramref name="pixelSize"/> above zero the sizes follow once more in that unit.
    /// </summary>
    public static string ListCsv(IReadOnlyList<MeasureShape> shapes, double pixelSize = 0, string unit = "")
    {
        bool scaled = pixelSize > 0;
        var sb = new StringBuilder();
        sb.Append("number,kind,x0,y0,x1,y1,length,dx,dy,angle_deg,width,height,radius,area,perimeter");
        if (scaled) sb.Append(",unit,unit_per_pixel,length_in_unit,width_in_unit,height_in_unit,radius_in_unit,area_in_unit2,perimeter_in_unit");
        sb.Append("\r\n");
        for (int i = 0; i < shapes.Count; i++)
        {
            MeasureShape shape = shapes[i];
            bool line = shape.Kind == MeasureKind.Line, circle = shape.Kind == MeasureKind.Circle;
            double none = double.NaN;
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1},{KindName(shape.Kind)},{Number(shape.X0)},{Number(shape.Y0)},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(circle ? none : shape.X1)},{Number(circle ? none : shape.Y1)},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(line ? shape.Length : none)},{Number(line ? shape.Dx : none)},{Number(line ? shape.Dy : none)},{Number(line ? shape.Angle : none)},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(line ? none : shape.Width)},{Number(line ? none : shape.Height)},{Number(circle ? shape.Radius : none)},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(line ? none : shape.Area)},{Number(line ? none : shape.Perimeter)}");
            if (scaled)
            {
                sb.Append(CultureInfo.InvariantCulture, $",{MetricsReport.Quote(unit)},{Number(pixelSize)},{Number(line ? shape.Length * pixelSize : none)},");
                sb.Append(CultureInfo.InvariantCulture, $"{Number(line ? none : shape.Width * pixelSize)},{Number(line ? none : shape.Height * pixelSize)},{Number(circle ? shape.Radius * pixelSize : none)},");
                sb.Append(CultureInfo.InvariantCulture, $"{Number(line ? none : shape.Area * pixelSize * pixelSize)},{Number(line ? none : shape.Perimeter * pixelSize)}");
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static string KindName(MeasureKind kind) => kind switch
    {
        MeasureKind.Rectangle => "rectangle",
        MeasureKind.Circle => "circle",
        _ => "line",
    };

    /// <summary>
    /// One row per sample of a line: its number, how far along the line it is, where it is on
    /// the pixel grid, and the values there. Given the profiles of several images, all taken
    /// along the same line, the columns of each end in the number the image has on screen
    /// (<paramref name="numbers"/>: red_1, red_3, ...); one profile without numbers has plain columns.
    /// </summary>
    public static string ProfileCsv(IReadOnlyList<LineProfile> profiles, IReadOnlyList<int>? numbers = null, double pixelSize = 0, string unit = "")
    {
        bool scaled = pixelSize > 0;
        var sb = new StringBuilder();
        sb.Append("index,distance_px,");
        if (scaled) sb.Append(MetricsReport.Quote("distance_" + unit)).Append(',');
        sb.Append("x,y");
        for (int p = 0; p < profiles.Count; p++)
        {
            string ending = numbers != null && p < numbers.Count ? "_" + numbers[p].ToString(CultureInfo.InvariantCulture) : profiles.Count > 1 ? "_" + (p + 1).ToString(CultureInfo.InvariantCulture) : "";
            foreach (ProfileChannel channel in profiles[p].Channels) sb.Append(',').Append(channel.Name).Append(ending);
        }
        sb.Append("\r\n");
        if (profiles.Count == 0) return sb.ToString();
        LineProfile first = profiles[0];
        for (int i = 0; i < first.Count; i++)
        {
            (double x, double y) = first.PointAt(i);
            sb.Append(CultureInfo.InvariantCulture, $"{i},{Number(first.DistanceAt(i))},");
            if (scaled) sb.Append(Number(first.DistanceAt(i) * pixelSize)).Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"{Number(x)},{Number(y)}");
            foreach (LineProfile profile in profiles)
                foreach (ProfileChannel channel in profile.Channels) sb.Append(',').Append(i < profile.Count ? Number(channel.Values[i]) : "");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// One row per image and channel: what was measured, the number of pixels, and their
    /// smallest and largest value, mean, spread and median. <paramref name="names"/> says which
    /// image each entry of <paramref name="areas"/> is of.
    /// </summary>
    public static string StatisticsCsv(IReadOnlyList<AreaStatistics> areas, IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        sb.Append("image,kind,x0,y0,x1,y1,radius,pixels,channel,min,max,mean,std_dev,median\r\n");
        for (int i = 0; i < areas.Count; i++) Rows(areas[i], i < names.Count ? names[i] : "");
        return sb.ToString();

        void Rows(AreaStatistics area, string name)
        {
            MeasureShape shape = area.Shape;
            bool circle = shape.Kind == MeasureKind.Circle;
            foreach (ChannelStatistics channel in area.Channels)
            {
                sb.Append(MetricsReport.Quote(name)).Append(',').Append(KindName(shape.Kind)).Append(',');
                sb.Append(CultureInfo.InvariantCulture, $"{Number(shape.X0)},{Number(shape.Y0)},{Number(circle ? double.NaN : shape.X1)},{Number(circle ? double.NaN : shape.Y1)},");
                sb.Append(CultureInfo.InvariantCulture, $"{Number(circle ? shape.Radius : double.NaN)},{area.Pixels},{channel.Name},");
                sb.Append(CultureInfo.InvariantCulture, $"{Number(channel.Min)},{Number(channel.Max)},{Number(channel.Mean)},{Number(channel.StdDev)},{Number(channel.Median)}\r\n");
            }
        }
    }

    /// <summary>One row per image, channel and value that occurs: how many pixels of the area have it.</summary>
    public static string HistogramCsv(IReadOnlyList<AreaStatistics> areas, IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        sb.Append("image,channel,value,count\r\n");
        for (int i = 0; i < areas.Count; i++) Rows(areas[i], i < names.Count ? names[i] : "");
        return sb.ToString();

        void Rows(AreaStatistics area, string name)
        {
            string quoted = MetricsReport.Quote(name);
            foreach (ChannelStatistics channel in area.Channels)
            {
                ValueHistogram histogram = channel.Histogram;
                for (int i = 0; i < histogram.Counts.Length; i++)
                {
                    if (histogram.Counts[i] == 0) continue;
                    sb.Append(quoted).Append(',').Append(channel.Name).Append(',');
                    sb.Append(CultureInfo.InvariantCulture, $"{Number(histogram.ValueAt(i))},{histogram.Counts[i]}\r\n");
                }
            }
        }
    }
}
