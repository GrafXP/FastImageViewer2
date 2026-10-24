using System.Diagnostics;
using System.Text;
using Fiv.Imaging;
using Fiv.Surface;

namespace Fiv.Bench;

/// <summary>
/// What the 3D height map costs before it can turn: the height grid of an image (reduced to
/// 2048 values on the long side, smoothed) and its mip levels, for an 8-bit colour, a 16-bit
/// grey and a float image of 24 MP and a JPEG of 50 MP. No viewer is started; the upload to the
/// GPU (some 11 MB) is not in it.
/// </summary>
public static unsafe class SurfaceCommand
{
    public static int Run(string corpus, int repetitions, string? outFile)
    {
        string single = Path.Combine(corpus, "single");
        (string Label, string File)[] images =
        [
            ("24 MP JPEG, 8-bit colour", "jpeg_24mp.jpg"),
            ("24 MP PNG, 16-bit grey", "png16_24mp.png"),
            ("24 MP TIFF, float grey", "tiff_float_24mp.tif"),
            ("50 MP JPEG, 8-bit colour", "jpeg_50mp.jpg"),
        ];
        if (images.Any(image => !File.Exists(Path.Combine(single, image.File))))
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }
        int[] smoothings = [0, 2, 8, 20];

        var sb = new StringBuilder();
        sb.AppendLine($"The height grid on the CPU, {Environment.ProcessorCount} logical cores. Milliseconds, median of {repetitions}.");
        sb.AppendLine();
        sb.AppendLine("| Image | Grid | " + string.Join(" | ", smoothings.Select(s => $"Smoothing {s}")) + " | Mip levels |");
        sb.AppendLine("|---|---|" + string.Concat(smoothings.Select(_ => "---|")) + "---|");
        foreach ((string label, string file) in images)
        {
            using DecodedImage image = Load(Path.Combine(single, file));
            string size = "";
            var cells = new List<string>();
            foreach (int smoothing in smoothings)
            {
                cells.Add(Time(repetitions, () =>
                {
                    using DecodedImage grid = HeightField.Build(image, HeightSource.Brightness, smoothing)!;
                    size = $"{grid.Width} x {grid.Height}";
                }).ToString("F0"));
            }
            using DecodedImage heights = HeightField.Build(image, HeightSource.Brightness, 2)!;
            double mipMs = Time(repetitions, () => new MipChain(heights).Dispose());
            string line = $"| {label} | {size} | {string.Join(" | ", cells)} | {mipMs:F1} |";
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

    static double Time(int repetitions, Action run)
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
