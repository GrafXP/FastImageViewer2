using System.Diagnostics;
using System.Text;

namespace Fiv.Bench;

/// <summary>
/// Runs the C# Native AOT and C++ bake-off programs alternately on the same image and compares
/// every stage from process spawn to first present.
/// </summary>
public static class BakeoffCommand
{
    static readonly (string Label, string From, string To)[] Stages =
    [
        ("process spawn -> main()", "spawn", "main"),
        ("create window", "main", "window"),
        ("create D3D11 device", "window", "device"),
        ("swap chain + back buffer", "device", "swapchain"),
        ("shaders + sampler", "swapchain", "pipeline"),
        ("decode thread: start", "main", "decodeStart"),
        ("decode thread: read file", "decodeStart", "read"),
        ("decode thread: header", "read", "header"),
        ("decode thread: decode", "header", "decoded"),
        ("show window", "joined", "shown"),
        ("upload + draw + Present", "shown", "presented"),
        ("DwmFlush", "presented", "flushed"),
        ("TOTAL spawn -> Present returned", "spawn", "presented"),
        ("TOTAL spawn -> next composition", "spawn", "flushed"),
        ("TOTAL main() -> Present returned", "main", "presented"),
    ];

    public static int Run(string repoRoot, string image, int runs, int warmRuns, string? outFile)
    {
        (string Name, string Exe)[] contenders =
        [
            ("C# AOT", Path.Combine(repoRoot, @"bench\bakeoff\cs\publish\bakeoff_cs.exe")),
            ("C++", Path.Combine(repoRoot, @"bench\bakeoff\cpp\obj\Release\bakeoff_cpp.exe")),
        ];
        foreach (var c in contenders)
        {
            if (!File.Exists(c.Exe))
            {
                Console.Error.WriteLine($"missing {c.Exe}; run bench\\bakeoff\\build.ps1 first");
                return 1;
            }
        }
        image = Path.GetFullPath(image);

        var cold = contenders.Select(_ => new List<Dictionary<string, long>>()).ToArray();
        string info = "";
        const int warmUp = 3;
        for (int run = 0; run < runs + warmUp; run++)
        {
            for (int k = 0; k < contenders.Length; k++)
            {
                int c = (run + k) % contenders.Length; // alternate who goes first
                var result = Launch(contenders[c].Exe, image, 0);
                if (result == null) return 1;
                if (run >= warmUp) cold[c].Add(result.Value.Stamps);
                info = result.Value.Info;
                Thread.Sleep(120);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Image: {Path.GetFileName(image)} ({new FileInfo(image).Length / 1e6:F1} MB), {info}");
        sb.AppendLine($"{runs} runs each after {warmUp} discarded, alternating. Milliseconds, median (p10–p90).");
        sb.AppendLine();
        sb.AppendLine($"| Stage | {contenders[0].Name} | {contenders[1].Name} | Δ median |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (label, from, to) in Stages)
        {
            var stats = cold.Select(list => new Stats(list.Select(s => Milliseconds(s, from, to)))).ToArray();
            sb.AppendLine($"| {label} | {stats[0]} | {stats[1]} | {stats[0].Median - stats[1].Median:+0.0;-0.0} |");
        }

        if (warmRuns > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Warm path (process, window and device already up), {warmRuns} repetitions in each of 3 processes:");
            sb.AppendLine();
            sb.AppendLine($"| Stage | {contenders[0].Name} | {contenders[1].Name} | Δ median |");
            sb.AppendLine("|---|---|---|---|");
            var decode = contenders.Select(_ => new List<double>()).ToArray();
            var present = contenders.Select(_ => new List<double>()).ToArray();
            for (int run = 0; run < 3; run++)
            {
                for (int c = 0; c < contenders.Length; c++)
                {
                    var result = Launch(contenders[c].Exe, image, warmRuns);
                    if (result == null) return 1;
                    // The first repetitions still pay first-use costs; skip two.
                    decode[c].AddRange(result.Value.WarmDecode.Skip(2));
                    present[c].AddRange(result.Value.WarmPresent.Skip(2));
                    Thread.Sleep(120);
                }
            }
            Stats[] d = [new(decode[0]), new(decode[1])], p = [new(present[0]), new(present[1])];
            sb.AppendLine($"| read + decode | {d[0]} | {d[1]} | {d[0].Median - d[1].Median:+0.0;-0.0} |");
            sb.AppendLine($"| upload + draw + Present | {p[0]} | {p[1]} | {p[0].Median - p[1].Median:+0.0;-0.0} |");
        }

        sb.AppendLine();
        foreach (var c in contenders) sb.AppendLine($"{c.Name}: {new FileInfo(c.Exe).Length / 1024} KB exe");
        Console.Write(sb);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static double Milliseconds(Dictionary<string, long> stamps, string from, string to) =>
        (stamps[to] - stamps[from]) * 1000.0 / Stopwatch.Frequency;

    static (Dictionary<string, long> Stamps, string Info, double[] WarmDecode, double[] WarmPresent)? Launch(
        string exe, string image, int warmRuns)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            ArgumentList = { image, warmRuns.ToString(), "0" },
        };
        long spawn = Stopwatch.GetTimestamp();
        using var process = Process.Start(start)!;
        string line = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1]);
        if (process.ExitCode != 0 || fields.ContainsKey("error") || !fields.ContainsKey("presented"))
        {
            Console.Error.WriteLine($"{Path.GetFileName(exe)} failed: {line}");
            return null;
        }
        var stamps = new Dictionary<string, long> { ["spawn"] = spawn };
        foreach (var (key, value) in fields)
            if (long.TryParse(value, out long ticks)) stamps[key] = ticks;
        double toMs = 1000.0 / long.Parse(fields["qpf"]);
        double[] Parse(string key) => fields[key].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => long.Parse(v) * toMs).ToArray();
        return (stamps, $"{fields["source"]} decoded at {fields["decodedSize"]}", Parse("warmDecode"), Parse("warmPresent"));
    }
}
