using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fiv.Comparing;

/// <summary>The numbers of a comparison as text for other programs: one CSV row per channel, or one JSON object.</summary>
public static class MetricsReport
{
    public static string UnitName(ValueUnit unit) => unit switch
    {
        ValueUnit.Bits8 => "8-bit",
        ValueUnit.Bits16 => "16-bit",
        ValueUnit.Float => "float",
        _ => "normalised",
    };

    /// <summary>
    /// A header row and one row per channel. The columns that say what was compared are repeated
    /// in every row, so that the rows of several reports can be put into one table.
    /// <paramref name="threshold"/> is in the units of the result.
    /// </summary>
    public static string Csv(MetricsResult result, string first, string second, double threshold)
    {
        var sb = new StringBuilder();
        sb.Append("first,second,x,y,width,height,pixels,unit,peak,threshold,channel,mae,mse,rmse,psnr_db,max_error,mean_difference,over_threshold_percent,ssim\r\n");
        foreach (ChannelMetrics channel in result.Channels)
        {
            sb.Append(Quote(first)).Append(',').Append(Quote(second)).Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"{result.Area.X},{result.Area.Y},{result.Area.Width},{result.Area.Height},{result.Pixels},");
            sb.Append(CultureInfo.InvariantCulture, $"{UnitName(result.Unit)},{Number(result.Peak)},{Number(threshold)},{channel.Name},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(channel.Mae)},{Number(channel.Mse)},{Number(channel.Rmse)},{Number(channel.Psnr)},{Number(channel.MaxError)},");
            sb.Append(CultureInfo.InvariantCulture, $"{Number(channel.MeanDifference)},{Number(100 * channel.Histogram.FractionBeyond(threshold))},{Number(channel.Ssim)}\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Nine significant digits; "inf" for the PSNR of identical images, nothing where a number is not defined.</summary>
    internal static string Number(double value) =>
        double.IsNaN(value) ? "" : double.IsPositiveInfinity(value) ? "inf" : double.IsNegativeInfinity(value) ? "-inf" : value.ToString("G9", CultureInfo.InvariantCulture);

    internal static string Quote(string text) => text.AsSpan().IndexOfAny(",\"\r\n") < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";

    /// <summary>One object: what was compared, and the numbers by channel. JSON has no infinity: the PSNR of identical images is null, and "identical" says why.</summary>
    public static string Json(MetricsResult result, string first, string second, double threshold)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("first", first);
            writer.WriteString("second", second);
            writer.WriteStartObject("area");
            writer.WriteNumber("x", result.Area.X);
            writer.WriteNumber("y", result.Area.Y);
            writer.WriteNumber("width", result.Area.Width);
            writer.WriteNumber("height", result.Area.Height);
            writer.WriteEndObject();
            writer.WriteNumber("pixels", result.Pixels);
            if (result.Ignored > 0) writer.WriteNumber("ignoredPixels", result.Ignored);
            writer.WriteString("unit", UnitName(result.Unit));
            Write(writer, "peak", result.Peak);
            Write(writer, "threshold", threshold);
            writer.WriteBoolean("pixelAligned", result.PixelAligned);
            writer.WriteBoolean("identical", result.Main is { MaxError: 0 });
            writer.WriteStartObject("channels");
            foreach (ChannelMetrics channel in result.Channels)
            {
                writer.WriteStartObject(channel.Name);
                Write(writer, "mae", channel.Mae);
                Write(writer, "mse", channel.Mse);
                Write(writer, "rmse", channel.Rmse);
                Write(writer, "psnr", channel.Psnr);
                Write(writer, "maxError", channel.MaxError);
                Write(writer, "meanDifference", channel.MeanDifference);
                Write(writer, "overThresholdPercent", 100 * channel.Histogram.FractionBeyond(threshold));
                Write(writer, "ssim", channel.Ssim);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    static void Write(Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value)) writer.WriteNumber(name, value);
        else writer.WriteNull(name);
    }
}
