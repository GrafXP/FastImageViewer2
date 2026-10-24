using System.Diagnostics;
using System.Text;
using Fiv.Imaging;
using Fiv.Native;
using Fiv.Thumbnails;

namespace Fiv.Bench;

/// <summary>
/// Every sample in corpus\formats (bench\tools\get-format-samples.ps1) through the viewer's
/// decoding: which decoder took it, what came out, how long it took, and whether the Windows codecs
/// alone, as the viewer had them before the bundled decoders, open it on this machine. No viewer is started.
/// </summary>
public static unsafe class FormatsCommand
{
    /// <param name="dumpFolder">If given, every decode is written there as an 8-bit PNG, upright and at most 800 pixels, as the viewer shows it, with WIC's next to it.</param>
    public static int Run(string corpus, string? outFile, string? dumpFolder = null)
    {
        if (dumpFolder != null) Directory.CreateDirectory(dumpFolder);
        string root = Path.Combine(corpus, "formats");
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine(@"no samples; run bench\tools\get-format-samples.ps1");
            return 1;
        }
        var sb = new StringBuilder();
        sb.AppendLine("| File | Decoder | Size | Layout | Orientation | Mean | Decode ms | Preview | Thumbnail | Windows codecs alone |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        int failures = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            FileBytes file = FileBytes.Read(path);
            try
            {
                ImageInfo info = ImageDecoding.Probe(file);
                string decoder = info.Codec.ToString();
                string size = "", layout = "", mean = "", ms = "", preview = "", thumbnail = "";
                var watch = Stopwatch.StartNew();
                try
                {
                    using DecodedImage image = DecodeAsViewer(file, info, ref decoder);
                    ms = watch.Elapsed.TotalMilliseconds.ToString("F1");
                    size = $"{image.Width} x {image.Height}";
                    layout = image.Layout.ToString();
                    mean = Mean(image);
                    if (dumpFolder != null) Dump(image, info.Orientation, Path.Combine(dumpFolder, name.Replace('/', '_') + ".png"));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    decoder += ": " + e.Message.ReplaceLineEndings(" ");
                    failures++;
                }
                if (ImageDecoding.HasPreview(info, 1920, 1080))
                {
                    watch.Restart();
                    using DecodedImage? image = ImageDecoding.DecodePreview(file, info, 1920, 1080, null);
                    preview = image == null ? "none" : $"{image.Width} x {image.Height}, {watch.Elapsed.TotalMilliseconds:F1} ms";
                }
                watch.Restart();
                try
                {
                    using Thumbnail? thumb = ThumbnailGenerator.FromImage(file, info);
                    thumbnail = thumb == null ? "none" : $"{thumb.Pixels.Width} x {thumb.Pixels.Height}, {watch.Elapsed.TotalMilliseconds:F1} ms";
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    thumbnail = "failed: " + e.Message.ReplaceLineEndings(" ");
                }
                string wic;
                try
                {
                    using DecodedImage image = WicDecoder.Decode(file.Data, file.Length);
                    wic = $"{image.Width} x {image.Height} {image.Layout}";
                    if (dumpFolder != null) Dump(image, info.Orientation, Path.Combine(dumpFolder, name.Replace('/', '_') + ".wic.png"));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    wic = "fails: " + e.Message.ReplaceLineEndings(" ").Trim();
                }
                string orientation = info.Orientation == Orientation.Normal ? "" : info.Orientation.ToString();
                string row = $"| {name} | {decoder} | {size} | {layout} | {orientation} | {mean} | {ms} | {preview} | {thumbnail} | {wic} |";
                sb.AppendLine(row);
                Console.WriteLine(row);
            }
            finally
            {
                file.Release();
            }
        }
        if (dumpFolder != null) WriteSheets(dumpFolder);
        sb.AppendLine();
        sb.AppendLine($"{failures} file(s) the viewer cannot decode.");
        if (outFile != null) File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine($"{failures} file(s) the viewer cannot decode.");
        return failures == 0 ? 0 : 1;
    }

    // ImageDecoding.DecodeFull, noting which decoder produced the pixels.
    static DecodedImage DecodeAsViewer(FileBytes file, in ImageInfo info, ref string decoder)
    {
        if (info.IsJpeg)
        {
            try
            {
                return JpegDecoder.Decode(file.Data, file.Length)!;
            }
            catch (InvalidDataException)
            {
                decoder = "Jpeg, then WIC";
            }
        }
        if (ImageDecoding.NativeFormat(info.Codec) is FivCodecs.Format native)
        {
            try
            {
                return NativeDecoder.Decode(file.Data, file.Length, native)!;
            }
            catch (InvalidDataException e)
            {
                decoder = $"{info.Codec} ({e.Message.ReplaceLineEndings(" ")}), then WIC";
            }
        }
        if (!ImageDecoding.IsTiff(file)) return WicDecoder.Decode(file.Data, file.Length);
        try
        {
            return WicDecoder.Decode(file.Data, file.Length);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            decoder = "Wic, then Tiff";
            return NativeDecoder.Decode(file.Data, file.Length, FivCodecs.Format.Tiff)!;
        }
    }

    static void Dump(DecodedImage image, Orientation orientation, string path)
    {
        (float low, float high) = ImageStatistics.ValueRange(image);
        (int width, int height) = Resampler.FitWithin(image.Width, image.Height, 800);
        using DecodedImage reduced = Resampler.ToBgra(image, width, height, low, high);
        using DecodedImage upright = Resampler.Upright(reduced, orientation);
        ImageWriter.WritePng(path, upright);
        s_tiles.Add((Path.GetFileNameWithoutExtension(path), Resampler.ToBgra(upright, Math.Max(1, upright.Width * Tile / Math.Max(upright.Width, upright.Height)),
            Math.Max(1, upright.Height * Tile / Math.Max(upright.Width, upright.Height)))));
    }

    const int Tile = 240, Columns = 6, Rows = 4;
    static readonly List<(string Name, DecodedImage Pixels)> s_tiles = [];

    /// <summary>The dumps side by side, 24 to a sheet on a grey ground, to look over many at once. The order goes to the console.</summary>
    public static void WriteSheets(string folder)
    {
        for (int first = 0, sheet = 1; first < s_tiles.Count; first += Columns * Rows, sheet++)
        {
            using var page = new DecodedImage(Columns * (Tile + 8), Rows * (Tile + 8), PixelLayout.Bgra8, Columns * (Tile + 8), Rows * (Tile + 8));
            new Span<byte>(page.Pixels, (int)page.ByteCount).Fill(0x60);
            Console.WriteLine($"sheet-{sheet}.png:");
            for (int i = first; i < Math.Min(s_tiles.Count, first + Columns * Rows); i++)
            {
                (string name, DecodedImage tile) = s_tiles[i];
                int cell = i - first, x0 = cell % Columns * (Tile + 8) + 4, y0 = cell / Columns * (Tile + 8) + 4;
                for (int y = 0; y < tile.Height; y++)
                    Buffer.MemoryCopy(tile.Pixels + (long)y * tile.Stride, page.Pixels + (long)(y0 + y) * page.Stride + x0 * 4, tile.Stride, tile.Stride);
                Console.WriteLine($"  {cell / Columns + 1},{cell % Columns + 1} {name}");
            }
            ImageWriter.WritePng(Path.Combine(folder, $"sheet-{sheet}.png"), page);
        }
    }

    /// <summary>The mean of each channel over 0..1, enough to tell a picture from a black or a garbage decode.</summary>
    static string Mean(DecodedImage image)
    {
        int channels = image.Layout is PixelLayout.Bgra8 or PixelLayout.Rgba16 ? 4 : 1;
        var sums = new double[channels];
        long count = 0;
        int step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / 250_000));
        for (int y = 0; y < image.Height; y += step)
        {
            byte* row = image.Pixels + (long)y * image.Stride;
            for (int x = 0; x < image.Width; x += step, count++)
            {
                for (int c = 0; c < channels; c++)
                {
                    sums[c] += image.Layout switch
                    {
                        PixelLayout.Bgra8 => row[x * 4 + (c < 3 ? 2 - c : 3)] / 255.0, // as R, G, B, A
                        PixelLayout.Gray8 => row[x] / 255.0,
                        PixelLayout.Gray16 => ((ushort*)row)[x] / 65535.0,
                        PixelLayout.GrayFloat => float.IsFinite(((float*)row)[x]) ? ((float*)row)[x] : 0,
                        _ => ((ushort*)row)[x * 4 + c] / 65535.0,
                    };
                }
            }
        }
        return string.Join(" ", sums.Select(s => (s / Math.Max(1, count)).ToString("F2")));
    }
}
