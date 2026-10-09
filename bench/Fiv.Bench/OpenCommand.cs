using System.Diagnostics;
using System.Text;

namespace Fiv.Bench;

/// <summary>
/// Cold-start timing of the real viewer: starts publish\FastImageViewer2.exe on one image again
/// and again, each time until the full image is on screen, and reports when each stage was
/// reached, counted from just before the process was created.
/// </summary>
public static class OpenCommand
{
    /// <summary>
    /// Warm open: with a viewer already running, starts a second process per image, which hands
    /// the file over and exits, and reports when the running viewer showed it. Times count from
    /// just before the second process was created, i.e. from the double-click.
    /// </summary>
    public static int RunWarm(string repoRoot, string[] images, int runs, string? outFile)
    {
        string exe = Path.Combine(repoRoot, "publish", "FastImageViewer2.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"missing {exe}; run build.ps1 first");
            return 1;
        }
        images = images.Select(Path.GetFullPath).ToArray();
        string instance = "bench" + Environment.ProcessId;
        string log = Path.Combine(Path.GetTempPath(), $"fiv_perflog_{Environment.ProcessId}.txt");
        File.Delete(log);

        static string[] Lines(string path)
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        static string[]? WaitForLine(string path, int count)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed.TotalSeconds < 30)
            {
                string[] lines = Lines(path);
                if (lines.Length >= count) return lines;
                Thread.Sleep(5);
            }
            return null;
        }

        using var primary = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { images[0], "--instance", instance, "--no-activate", "--perf-log", log },
        })!;
        try
        {
            if (WaitForLine(log, 1) == null)
            {
                Console.Error.WriteLine("the viewer did not show the first image within 30 s");
                return 1;
            }
            Thread.Sleep(500);

            const int warmUp = 2;
            var handOver = images.Select(_ => new List<double>()).ToArray();
            var firstFrame = images.Select(_ => new List<double>()).ToArray();
            var fullFrame = images.Select(_ => new List<double>()).ToArray();
            int seen = 1;
            for (int run = 0; run < (runs + warmUp) * images.Length; run++)
            {
                int index = (run + 1) % images.Length;
                long spawn = Stopwatch.GetTimestamp();
                using (var forwarder = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    ArgumentList = { images[index], "--instance", instance },
                })!)
                {
                    forwarder.WaitForExit();
                }
                string[]? lines = WaitForLine(log, ++seen);
                if (lines == null)
                {
                    Console.Error.WriteLine("the running viewer did not show the forwarded image within 30 s");
                    return 1;
                }
                var fields = lines[seen - 1].Split(' ').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1]);
                double Ms(string key) => (long.Parse(fields[key]) - spawn) * 1000.0 / Stopwatch.Frequency;
                if (run >= warmUp * images.Length)
                {
                    handOver[index].Add(Ms("requested"));
                    firstFrame[index].Add(Ms("first_frame"));
                    fullFrame[index].Add(Ms("full_frame"));
                }
                Thread.Sleep(250);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Warm open, viewer already running. {runs} opens per image after {warmUp} discarded.");
            sb.AppendLine("Milliseconds after the forwarding process was created, median (p10–p90).");
            sb.AppendLine();
            sb.AppendLine("| Image | Handed to the viewer | First frame | Full image on screen |");
            sb.AppendLine("|---|---|---|---|");
            for (int i = 0; i < images.Length; i++)
                sb.AppendLine($"| {Path.GetFileName(images[i])} ({new FileInfo(images[i]).Length / 1e6:F1} MB) | {new Stats(handOver[i])} | {new Stats(firstFrame[i])} | {new Stats(fullFrame[i])} |");
            Console.Write(sb);
            if (outFile != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                File.WriteAllText(outFile, sb.ToString());
            }
            return 0;
        }
        finally
        {
            if (!primary.HasExited) primary.Kill();
            try
            {
                File.Delete(log);
            }
            catch (IOException)
            {
            }
        }
    }

    public static int Run(string repoRoot, string image, int runs, string? outFile)
    {
        string exe = Path.Combine(repoRoot, "publish", "FastImageViewer2.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"missing {exe}; run build.ps1 first");
            return 1;
        }
        image = Path.GetFullPath(image);
        string perfFile = Path.Combine(Path.GetTempPath(), $"fiv_perf_{Environment.ProcessId}.txt");

        const int warmUp = 2;
        var stages = new Dictionary<string, List<double>>();
        var order = new List<string>();
        for (int run = 0; run < runs + warmUp; run++)
        {
            File.Delete(perfFile);
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList = { image, "--new-instance", "--no-activate", "--exit-when-loaded", "--perf-out", perfFile },
            };
            long spawn = Stopwatch.GetTimestamp();
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                Console.Error.WriteLine("the viewer did not finish loading within 30 s");
                return 1;
            }
            if (!File.Exists(perfFile))
            {
                Console.Error.WriteLine($"the viewer exited with code {process.ExitCode} without writing timings");
                return 1;
            }
            if (run >= warmUp)
            {
                foreach (string line in File.ReadLines(perfFile))
                {
                    string[] parts = line.Split('=', 2);
                    if (parts.Length != 2 || parts[0] == "qpf" || !long.TryParse(parts[1], out long ticks)) continue;
                    if (!stages.TryGetValue(parts[0], out var list))
                    {
                        stages[parts[0]] = list = [];
                        order.Add(parts[0]);
                    }
                    list.Add((ticks - spawn) * 1000.0 / Stopwatch.Frequency);
                }
            }
            Thread.Sleep(150);
        }
        File.Delete(perfFile);

        var sb = new StringBuilder();
        sb.AppendLine($"Image: {Path.GetFileName(image)} ({new FileInfo(image).Length / 1e6:F1} MB). {runs} fresh processes after {warmUp} discarded.");
        sb.AppendLine("Milliseconds after process creation, median (p10–p90).");
        sb.AppendLine();
        sb.AppendLine("| Stage reached | ms |");
        sb.AppendLine("|---|---|");
        foreach (string name in order.OrderBy(n => new Stats(stages[n]).Median))
            sb.AppendLine($"| {name.Replace('_', ' ')} | {new Stats(stages[name])} |");
        Console.Write(sb);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }
}
