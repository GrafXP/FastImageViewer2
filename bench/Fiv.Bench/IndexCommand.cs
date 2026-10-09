using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    public static int Run(string corpus, int repetitions, string? outFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Milliseconds, median of {repetitions}.");
        sb.AppendLine();
        sb.AppendLine("| Folder | Images | Read (viewer) | Sort (viewer) | Total (viewer) | Directory.GetFiles + FileInfo | Sort with StrCmpLogicalW |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
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

            string line = $"| {name} | {images} | {new Stats(read).Median:F1} | {new Stats(sort).Median:F1} | {new Stats(total).Median:F1} | {new Stats(naive).Median:F0} | {new Stats(logical).Median:F0} |";
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
}
