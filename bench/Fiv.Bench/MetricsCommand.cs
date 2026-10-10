using System.Diagnostics;
using System.Text;
using Fiv.Comparing;
using Fiv.Imaging;

namespace Fiv.Bench;

/// <summary>
/// What the numbers of a comparison cost: the error metrics, structural similarity and its
/// per-pixel map, and the full-resolution pictures the viewer saves, each on the 24 MP pair of
/// the corpus and, for the metrics, on a 16-bit and a float pair. No viewer is started.
/// </summary>
public static unsafe class MetricsCommand
{
    public static int Run(string corpus, int repetitions, string? outFile)
    {
        string compare = Path.Combine(corpus, "compare"), single = Path.Combine(corpus, "single");
        (string Label, string First, string Second)[] pairs =
        [
            ("24 MP JPEG, original against the changed one", Path.Combine(compare, "scene_1_original.jpg"), Path.Combine(compare, "scene_3_changed.jpg")),
            ("24 MP JPEG, original against quality 50", Path.Combine(compare, "scene_1_original.jpg"), Path.Combine(compare, "scene_2_quality50.jpg")),
            ("24 MP, 16-bit PNG against float TIFF", Path.Combine(single, "png16_24mp.png"), Path.Combine(single, "tiff_float_24mp.tif")),
        ];
        if (pairs.Any(p => !File.Exists(p.First) || !File.Exists(p.Second)))
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Comparing two images on the CPU, {Environment.ProcessorCount} logical cores. Milliseconds, median of {repetitions}.");
        sb.AppendLine();
        sb.AppendLine("| Pair | Error metrics and histogram | With structural similarity | With its map as well | MAE | PSNR | SSIM |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        DecodedImage? keepFirst = null, keepSecond = null;
        foreach ((string label, string firstPath, string secondPath) in pairs)
        {
            DecodedImage first = Load(firstPath), second = Load(secondPath);
            var a = new CompareSource(first, Affine2.Identity);
            var b = new CompareSource(second, Affine2.Identity);
            MetricsResult? last = null;
            double Time(bool ssim, bool map)
            {
                var times = new List<double>();
                for (int i = 0; i < repetitions + 1; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    MetricsResult result = CompareMetrics.Compute(a, b, null, ssim, map)!;
                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (i > 0) times.Add(ms); // the first run compiles the code
                    result.SsimMap?.Dispose();
                    last = result;
                }
                return new Stats(times).Median;
            }
            double basic = Time(false, false), withSsim = Time(true, false), withMap = Time(true, true);
            ChannelMetrics main = last!.Main!;
            string line = $"| {label} | {basic:F0} | {withSsim:F0} | {withMap:F0} | {main.Mae:G4} | {main.Psnr:F2} dB | {main.Ssim:F4} |";
            sb.AppendLine(line);
            Console.WriteLine(line);
            if (keepFirst == null)
            {
                (keepFirst, keepSecond) = (first, second);
            }
            else
            {
                first.Dispose();
                second.Dispose();
            }
        }

        // Saving what the views show, at full resolution.
        sb.AppendLine();
        sb.AppendLine("Saving the 24 MP pair's views at full resolution (rendering on all cores, then writing the file):");
        sb.AppendLine();
        sb.AppendLine("| File | Render | Write | Size |");
        sb.AppendLine("|---|---|---|---|");
        string folder = Path.Combine(Path.GetTempPath(), "fiv_metrics_" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        try
        {
            var a = new CompareSource(keepFirst!, Affine2.Identity);
            var b = new CompareSource(keepSecond!, Affine2.Identity);
            int width = keepFirst!.Width, height = keepFirst.Height;
            var view = new DifferenceView { Kind = DifferenceKind.Absolute, Map = Colormap.Viridis, Gain = 8, FullScale = 1 };
            void Row(string label, string name, Func<DecodedImage> render, Action<string, DecodedImage> write)
            {
                long started = Stopwatch.GetTimestamp();
                using DecodedImage image = render();
                double renderMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string path = Path.Combine(folder, name);
                started = Stopwatch.GetTimestamp();
                write(path, image);
                double writeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string line = $"| {label} | {renderMs:F0} | {writeMs:F0} | {new FileInfo(path).Length / 1e6:F1} MB |";
                sb.AppendLine(line);
                Console.WriteLine(line);
            }
            Row("difference in colours, PNG", "difference.png", () => CompareExport.Difference(a, b, width, height, view, ToneSettings.Default), (p, i) => ImageWriter.WritePng(p, i, alpha: true));
            Row("blend, PNG", "blend.png", () => CompareExport.Mix(a, b, width, height, MixExport.Blend, 0.5f, 0, 0, ToneSettings.Default), (p, i) => ImageWriter.WritePng(p, i, alpha: true));
            Row("difference values, float TIFF", "difference.tif", () => CompareExport.DifferenceValues(a, b, width, height, DifferenceMath.Colour), ImageWriter.WriteTiffFloat);
        }
        finally
        {
            keepFirst?.Dispose();
            keepSecond?.Dispose();
            Directory.Delete(folder, recursive: true);
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
