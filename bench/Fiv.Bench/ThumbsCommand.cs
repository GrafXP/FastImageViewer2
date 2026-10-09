using System.Diagnostics;
using System.Text;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Thumbnails;

namespace Fiv.Bench;

/// <summary>
/// Thumbnail timings: making them for folders seen for the first time (from the image, and from
/// the thumbnail cameras embed), and getting a screenful back from the pack of a folder seen before.
/// </summary>
public static unsafe class ThumbsCommand
{
    public static int Run(string corpus, string? outFile)
    {
        var sb = new StringBuilder();
        int cores = Environment.ProcessorCount;
        int[] threadCounts = [1, Math.Max(1, cores / 2), Math.Max(1, cores - 2)];

        sb.AppendLine("**Making thumbnails, folder not seen before** (read file, decode, reduce to 256 px, encode, store). Images per second:");
        sb.AppendLine();
        sb.AppendLine($"| Folder | Source | {string.Join(" | ", threadCounts.Select(t => t + (t == 1 ? " thread" : " threads")))} |");
        sb.AppendLine($"|---|---|{string.Concat(threadCounts.Select(_ => "---|"))}");
        foreach ((string folder, string label, bool embeddedOnly) in new[]
                 {
                     (Path.Combine(corpus, "series", "jpeg_12mp"), "40 x 12 MP JPEG", false),
                     (Path.Combine(corpus, "series", "jpeg_12mp_exif"), "40 x 12 MP JPEG with embedded thumbnail", false),
                     (Path.Combine(corpus, "series", "jpeg_12mp_exif"), "40 x 12 MP JPEG with embedded thumbnail", true),
                     (Path.Combine(corpus, "single"), "15 mixed files, 2 to 80 MP", false),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            FolderIndex index = FolderIndex.Build(folder);
            var rates = new List<string>();
            foreach (int threads in threadCounts)
            {
                // Enough rounds for a second or so; every round starts with an empty pack.
                int rounds = embeddedOnly ? 20 : Math.Max(1, 200 / index.Count * threads / 4);
                double seconds = 0;
                for (int round = 0; round < Math.Max(1, rounds); round++)
                {
                    string root = Directory.CreateTempSubdirectory("fivthumbs").FullName;
                    try
                    {
                        using ThumbnailPack pack = ThumbnailPack.Open(root, folder);
                        long t0 = Stopwatch.GetTimestamp();
                        Parallel.For(0, index.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                        {
                            if (embeddedOnly) Embedded(index.PathAt(i));
                            else Load(pack, index, i, 256)?.Dispose();
                        });
                        seconds += Stopwatch.GetElapsedTime(t0).TotalSeconds;
                    }
                    finally
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                rates.Add($"{index.Count * Math.Max(1, rounds) / seconds:F0}");
            }
            string line = $"| {label} | {(embeddedOnly ? "embedded thumbnail only (first, provisional picture)" : "the image")} | {string.Join(" | ", rates)} |";
            sb.AppendLine(line);
            Console.WriteLine(line);
        }

        // A folder seen before: a pack of 2,000 real 256 px thumbnails, then one screenful from it.
        string series = Path.Combine(corpus, "series", "jpeg_12mp");
        if (Directory.Exists(series))
        {
            string root = Directory.CreateTempSubdirectory("fivthumbs").FullName;
            try
            {
                FolderIndex index = FolderIndex.Build(series);
                const int entries = 2000;
                using (ThumbnailPack pack = ThumbnailPack.Open(root, series))
                {
                    var masters = new List<(byte[] Jpeg, int Width, int Height)>();
                    for (int i = 0; i < index.Count; i++)
                    {
                        FileBytes file = FileBytes.Read(index.PathAt(i));
                        using Thumbnail thumbnail = ThumbnailGenerator.FromImage(file, ImageDecoding.Probe(file))!;
                        masters.Add((JpegEncoder.Encode(thumbnail.Pixels, 88), thumbnail.Pixels.Width, thumbnail.Pixels.Height));
                        file.Release();
                    }
                    for (int i = 0; i < entries; i++)
                    {
                        var (jpeg, width, height) = masters[i % masters.Count];
                        pack.Add($"IMG_{i:D5}.jpg", 1000 + i, 5000 + i, 4000, 3000, Orientation.Normal, width, height, jpeg);
                    }
                    Console.WriteLine($"pack of {entries} thumbnails: {pack.DataBytes / 1e6:F1} MB, {pack.DataBytes / entries / 1024.0:F1} KB each");
                    sb.AppendLine();
                    sb.AppendLine($"**A folder seen before**: pack of {entries} thumbnails of 256 px, {pack.DataBytes / 1e6:F1} MB ({pack.DataBytes / entries / 1024.0:F1} KB each).");
                    sb.AppendLine();
                }

                sb.AppendLine("| Step | ms |");
                sb.AppendLine("|---|---|");
                var open = new List<double>();
                for (int i = 0; i < 9; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    using ThumbnailPack pack = ThumbnailPack.Open(root, series);
                    open.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    if (pack.Count != entries) throw new InvalidOperationException("pack did not reload completely");
                }
                string openLine = $"| open the pack (read the index of {entries} entries) | {new Stats(open).Median:F1} |";
                sb.AppendLine(openLine);
                Console.WriteLine(openLine);

                using ThumbnailPack cached = ThumbnailPack.Open(root, series);
                foreach ((int size, int count) in new[] { (256, 60), (128, 250), (96, 400) })
                {
                    foreach (int threads in new[] { 1, Math.Max(1, cores - 2) })
                    {
                        var times = new List<double>();
                        for (int repeat = 0; repeat < 7; repeat++)
                        {
                            int first = repeat * 97 % (entries - count);
                            long t0 = Stopwatch.GetTimestamp();
                            Parallel.For(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                            {
                                string name = $"IMG_{i:D5}.jpg";
                                using DecodedImage? image = ThumbnailLoader.Load(cached, name, name, 1000 + i, 5000 + i, size, null, null, out _, out ThumbnailSource source);
                                if (image == null || source != ThumbnailSource.Cache) throw new InvalidOperationException("not served from the pack");
                            });
                            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                        }
                        string line = $"| a screenful: {count} thumbnails at {size} px, {threads} {(threads == 1 ? "thread" : "threads")} | {new Stats(times).Median:F1} |";
                        sb.AppendLine(line);
                        Console.WriteLine(line);
                    }
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static DecodedImage? Load(ThumbnailPack pack, FolderIndex index, int position, int size) =>
        ThumbnailLoader.Load(pack, index.PathAt(position), index.NameAt(position), index.SizeAt(position), index.ModifiedAt(position),
            size, null, null, out _, out _);

    /// <summary>Only what it takes to show the embedded thumbnail: the first 128 KB of the file.</summary>
    static void Embedded(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] head = new byte[128 * 1024];
        int read = RandomAccess.Read(file, head, 0);
        fixed (byte* p = head)
        {
            using Thumbnail? thumbnail = ThumbnailGenerator.FromEmbedded(p, (nuint)read);
            if (thumbnail == null) throw new InvalidOperationException("no embedded thumbnail in " + path);
        }
    }
}
