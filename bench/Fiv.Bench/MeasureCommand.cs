using System.Diagnostics;
using System.Text;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Measuring;

namespace Fiv.Bench;

/// <summary>
/// What measuring costs: the numbers and histogram of an area, for the whole of a 24 MP image, a
/// circle in it and a small rectangle, and the values along a line from corner to corner; for an
/// 8-bit colour, a 16-bit grey and a float image, and for an image turned by half a degree,
/// whose pixels have to be blended. No viewer is started.
/// </summary>
public static unsafe class MeasureCommand
{
    public static int Run(string corpus, int repetitions, string? outFile)
    {
        string single = Path.Combine(corpus, "single");
        (string Label, string File, bool Turned)[] images =
        [
            ("24 MP JPEG, 8-bit colour", "jpeg_24mp.jpg", false),
            ("24 MP PNG, 16-bit grey", "png16_24mp.png", false),
            ("24 MP TIFF, float grey", "tiff_float_24mp.tif", false),
            ("24 MP JPEG, turned by 0.5 degrees", "jpeg_24mp.jpg", true),
        ];
        if (images.Any(image => !File.Exists(Path.Combine(single, image.File))))
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Measuring on the CPU, {Environment.ProcessorCount} logical cores. Milliseconds, median of {repetitions}.");
        sb.AppendLine();
        sb.AppendLine("| Image | Area: the whole image | Area: a circle 4000 across | Area: 1000 x 1000 | Line from corner to corner | Pixels in the circle | Samples of the line |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach ((string label, string file, bool turned) in images)
        {
            using DecodedImage image = Load(Path.Combine(single, file));
            int width = image.Width, height = image.Height;
            Affine2 toGrid = Affine2.Identity;
            if (turned)
            {
                // About its centre, as an alignment turns an image.
                double angle = 0.5 * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle), cx = width / 2.0, cy = height / 2.0;
                toGrid = new Affine2(cos, sin, -sin, cos, cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
            }
            var source = new CompareSource(image, toGrid);
            MeasureShape whole = MeasureShape.Rectangle(0, 0, width, height);
            MeasureShape circle = MeasureShape.Circle(width / 2.0, height / 2.0, height / 2.0);
            MeasureShape small = MeasureShape.Rectangle(width / 2 - 500, height / 2 - 500, width / 2 + 500, height / 2 + 500);
            MeasureShape diagonal = MeasureShape.Line(0.5, 0.5, width - 0.5, height - 0.5);

            long pixels = 0;
            int samples = 0;
            double Time(Action run)
            {
                var times = new List<double>();
                for (int i = 0; i < repetitions + 1; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    run();
                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (i > 0) times.Add(ms); // the first run compiles the code
                }
                return new Stats(times).Median;
            }
            double wholeMs = Time(() => AreaStatistics.Compute(source, whole));
            double circleMs = Time(() => pixels = AreaStatistics.Compute(source, circle)!.Pixels);
            double smallMs = Time(() => AreaStatistics.Compute(source, small));
            double lineMs = Time(() => samples = LineProfile.Sample(source, diagonal).Count);
            string line = $"| {label} | {wholeMs:F0} | {circleMs:F0} | {smallMs:F1} | {lineMs:F2} | {pixels} | {samples} |";
            sb.AppendLine(line);
            Console.WriteLine(line);
        }

        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static DecodedImage Load(string path)
    {
        FileBytes file = FileBytes.Read(path);
        try
        {
            return ImageDecoding.DecodeFull(file, ImageDecoding.Probe(file), null)!;
        }
        finally
        {
            file.Release();
        }
    }
}
