using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Fiv.Imaging;

namespace Fiv.Bench;

/// <summary>
/// Decoder timings on the corpus: libjpeg-turbo at every reduction and with its speed options,
/// WIC for every format, and thumbnail-size decode throughput across cores. Files are read into
/// memory first, so the numbers are decode time only.
/// </summary>
public static unsafe class DecodeCommand
{
    delegate DecodedImage Decoder(byte* data, nuint size);

    public static int Run(string corpus, int repetitions, string? outFile)
    {
        string single = Path.Combine(corpus, "single");
        var sb = new StringBuilder();
        sb.AppendLine($"Decode time in ms, median of {repetitions} (min). MP/s is source megapixels per second at the median.");
        sb.AppendLine();
        sb.AppendLine("| File | Size | Decoder | Output | ms | MP/s |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (string path in Directory.EnumerateFiles(single).OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            byte* data = ReadAll(path, out nuint size);
            string sizeText = $"{size / 1e6:F1} MB";
            void Row(string decoder, Decoder decode)
            {
                try
                {
                    var times = new List<double>();
                    string output = "";
                    double megapixels = 0;
                    for (int i = 0; i < repetitions + 1; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        using DecodedImage image = decode(data, size);
                        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        if (i > 0) times.Add(ms); // first call pays one-time costs
                        output = $"{image.Width}x{image.Height} {image.Layout}";
                        megapixels = (double)image.SourceWidth * image.SourceHeight / 1e6;
                    }
                    var stats = new Stats(times);
                    string line = $"| {name} | {sizeText} | {decoder} | {output} | {stats.Median:F1} ({stats.Min:F1}) | {megapixels / stats.Median * 1000:F0} |";
                    sb.AppendLine(line);
                    Console.WriteLine(line);
                }
                catch (Exception e)
                {
                    string line = $"| {name} | {sizeText} | {decoder} | failed: {e.Message.ReplaceLineEndings(" ")} | | |";
                    sb.AppendLine(line);
                    Console.WriteLine(line);
                }
            }

            if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                foreach (int denominator in new[] { 1, 2, 4, 8 })
                    Row($"libjpeg-turbo 1/{denominator}", (d, s) => JpegDecoder.Decode(d, s, denominator)!);
                if (name is "jpeg_24mp.jpg" or "jpeg_12mp.jpg")
                {
                    Row("libjpeg-turbo 1/1 fast upsample", (d, s) => JpegDecoder.Decode(d, s, 1, JpegDecodeOptions.FastUpsample)!);

                    Row("libjpeg-turbo 1/4 fast upsample", (d, s) => JpegDecoder.Decode(d, s, 4, JpegDecodeOptions.FastUpsample)!);
                }
            }
            Row("WIC", WicDecoder.Decode);
            NativeMemory.Free(data);
        }

        // Thumbnail throughput: the 12 MP series decoded at 1/8 on n threads, from memory.
        string series = Path.Combine(corpus, "series", "jpeg_12mp");
        if (Directory.Exists(series))
        {
            var files = Directory.GetFiles(series, "*.jpg").Select(p => File.ReadAllBytes(p)).ToArray();
            sb.AppendLine();
            sb.AppendLine($"Reduced decode throughput, {files.Length} distinct 12 MP JPEGs from memory, images per second:");
            sb.AppendLine();
            sb.AppendLine("| Threads | 1/8 (500x375) | 1/4 (1000x750) | 1/1 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (int threads in new[] { 1, 2, 4, 6, 12 })
            {
                string line = $"| {threads} | {Throughput(files, threads, 8):F0} | {Throughput(files, threads, 4):F0} | {Throughput(files, threads, 1):F0} |";
                sb.AppendLine(line);
                Console.WriteLine(line);
            }
        }

        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static double Throughput(byte[][] files, int threads, int denominator)
    {
        int rounds = denominator == 1 ? 1 : 4;
        double best = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Parallel.For(0, files.Length * rounds, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                byte[] file = files[i % files.Length];
                fixed (byte* p = file)
                {
                    using DecodedImage image = JpegDecoder.Decode(p, (nuint)file.Length, denominator, JpegDecodeOptions.FastUpsample)!;
                }
            });
            best = Math.Max(best, files.Length * rounds / Stopwatch.GetElapsedTime(t0).TotalSeconds);
        }
        return best;
    }

    static byte* ReadAll(string path, out nuint size)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte* data = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        size = (nuint)bytes.Length;
        return data;
    }
}
