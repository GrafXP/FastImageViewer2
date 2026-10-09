using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Fiv.Imaging;
using Fiv.Indexing;

namespace Fiv.Bench;

/// <summary>
/// Folder index timings on corpus\folders: the viewer's own directory reader and natural sort,
/// next to the obvious .NET way and Explorer's comparison function for reference. The file
/// system's metadata is in the OS cache after the first pass; these are warm numbers.
/// </summary>
public static partial class IndexCommand
{
    [LibraryImport("shlwapi", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    public static int Run(string corpus, int repetitions, string? outFile, bool quick = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Milliseconds, median of {repetitions}.");
        sb.AppendLine();
        sb.AppendLine("| Folder | Images | Read (viewer) | Sort (viewer) | Total (viewer) | Directory.GetFiles + FileInfo | Sort with StrCmpLogicalW | Dimensions of every file, 1 thread | all cores |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (string name in new[] { "1k", "10k", "100k" })
        {
            string folder = Path.Combine(corpus, "folders", name);
            if (!Directory.Exists(folder)) continue;
            FolderIndex.Build(folder); // first pass fills the OS cache and compiles the code

            var read = new List<double>();
            var sort = new List<double>();
            var total = new List<double>();
            int images = 0;
            for (int i = 0; i < repetitions; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                FolderIndex index = FolderIndex.Build(folder);
                total.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                read.Add(index.EnumerateMs);
                sort.Add(index.SortMs);
                images = index.Count;
            }

            if (quick)
            {
                // Only the viewer's own reader and sort, e.g. to compare a network path with a local one.
                string quickLine = $"| {name} | {images} | {new Stats(read).Median:F1} | {new Stats(sort).Median:F1} | {new Stats(total).Median:F1} |";
                sb.AppendLine(quickLine);
                Console.WriteLine(quickLine);
                continue;
            }

            var naive = new List<double>();
            var logical = new List<double>();
            for (int i = 0; i < Math.Min(repetitions, 3); i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                string[] files = Directory.GetFiles(folder);
                long bytes = 0;
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    bytes += info.Length + info.LastWriteTimeUtc.Ticks % 2;
                }
                naive.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                GC.KeepAlive(bytes);

                string[] names = Array.ConvertAll(files, f => Path.GetFileName(f)!);
                t0 = Stopwatch.GetTimestamp();
                Array.Sort(names, StrCmpLogicalW);
                logical.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }

            // Header probes: open each file, read its first 4 KB, find the size.
            FolderIndex probed = FolderIndex.Build(folder);
            string[] paths = Enumerable.Range(0, probed.Count).Select(probed.PathAt).ToArray();
            int failed = 0;
            long p0 = Stopwatch.GetTimestamp();
            foreach (string path in paths)
                if (!ImageProbe.TryReadSize(path, out _, out _, out _)) failed++;
            double probeSingle = Stopwatch.GetElapsedTime(p0).TotalMilliseconds;
            p0 = Stopwatch.GetTimestamp();
            Parallel.ForEach(paths, path => ImageProbe.TryReadSize(path, out _, out _, out _));
            double probeParallel = Stopwatch.GetElapsedTime(p0).TotalMilliseconds;
            if (failed > 0) Console.Error.WriteLine($"{name}: {failed} of {paths.Length} files could not be probed");

            string line = $"| {name} | {images} | {new Stats(read).Median:F1} | {new Stats(sort).Median:F1} | {new Stats(total).Median:F1} | {new Stats(naive).Median:F0} | {new Stats(logical).Median:F0} | {probeSingle:F0} ({paths.Length / probeSingle * 1000:F0}/s) | {probeParallel:F0} ({paths.Length / probeParallel * 1000:F0}/s) |";
            sb.AppendLine(line);
            Console.WriteLine(line);
        }
        // The probes against what the decoders say, on one file of every kind.
        string single = Path.Combine(corpus, "single");
        if (!quick && Directory.Exists(single))
        {
            int wrong = 0, checkedFiles = 0;
            foreach (string path in Directory.GetFiles(single))
            {
                if (!ImageProbe.TryReadSize(path, out int width, out int height, out ImageFileFormat format))
                {
                    Console.WriteLine($"probe failed: {Path.GetFileName(path)}");
                    wrong++;
                    continue;
                }
                checkedFiles++;
                FileBytes file = FileBytes.Read(path);
                try
                {
                    unsafe
                    {
                        ImageInfo info = ImageDecoding.Probe(file);
                        using DecodedImage? decoded = info.IsJpeg ? null : WicDecoder.Decode(file.Data, file.Length);
                        (int w, int h) = info.IsJpeg ? (info.Width, info.Height) : (decoded!.Width, decoded.Height);
                        if ((w, h) != (width, height))
                        {
                            Console.WriteLine($"probe says {width}x{height} ({format}), decoder says {w}x{h}: {Path.GetFileName(path)}");
                            wrong++;
                        }
                    }
                }
                finally
                {
                    file.Release();
                }
            }
            string check = $"Header probe checked against the decoders on {checkedFiles} corpus files: {(wrong == 0 ? "all agree" : wrong + " disagree")}.";
            sb.AppendLine().AppendLine(check);
            Console.WriteLine(check);
        }
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }
}
