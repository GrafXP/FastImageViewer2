using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Fiv.Bench;

/// <summary>
/// The compare modes of the real viewer: how long until every image of a set is on screen in
/// full, whether zooming and panning keep up with the screen's refresh in each mode, and how
/// long the switch from one image to the other takes. Input goes to the viewer through its
/// test messages, which work without the mouse being over its window.
/// </summary>
public static partial class CompareCommand
{
    const uint WM_CLOSE = 0x0010, WM_MOUSEWHEEL = 0x020A;
    const uint WM_APP_RESET_STATISTICS = 0x8000 + 20, WM_APP_TEST_MOUSE = 0x8000 + 22, WM_APP_TEST_KEY = 0x8000 + 23;

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32")]
    private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    private static partial int ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32")]
    private static partial int GetClientRect(nint window, out Rect rect);

    /// <summary>A mode to measure. With <paramref name="Values"/> the view is zoomed in until the pixel values are written into the pixels.</summary>
    sealed record Case(string Label, string? Mode, string Images, string[] Files, bool Values = false);

    sealed record Result(double ReadyMs, Dictionary<string, string> Frames, int ClientWidth, int ClientHeight, double[] SwitchMs, double MapMs, string? Metrics);

    public static int Run(string repoRoot, string corpus, string? outFile)
    {
        string exe = Path.Combine(repoRoot, "publish", "FastImageViewer2.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"missing {exe}; run build.ps1 first");
            return 1;
        }
        string compare = Path.Combine(corpus, "compare"), series = Path.Combine(corpus, "series", "jpeg_12mp");
        string[] pair = [Path.Combine(compare, "scene_1_original.jpg"), Path.Combine(compare, "scene_3_changed.jpg")];
        string[] four = [.. pair, Path.Combine(compare, "scene_2_quality50.jpg"), Path.Combine(compare, "scene_4_half.jpg")];
        string[] sixteen = Directory.Exists(series) ? Directory.GetFiles(series, "*.jpg").Order(StringComparer.OrdinalIgnoreCase).Take(16).ToArray() : [];
        if (four.Any(f => !File.Exists(f)) || sixteen.Length < 16)
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }

        Case[] cases =
        [
            new("one image, not comparing", null, "1 of 24 MP", [pair[0]]),
            new("single", "single", "2 of 24 MP", pair),
            new("side by side", "side", "2 of 24 MP", pair),
            new("grid", "grid", "3 of 24 MP, 1 of 6 MP", four),
            new("grid", "grid", "16 of 12 MP", sixteen),
            new("split", "split", "2 of 24 MP", pair),
            new("blend", "blend", "2 of 24 MP", pair),
            new("checkerboard", "checker", "2 of 24 MP", pair),
            new("difference", "diff", "2 of 24 MP", pair),
            new("difference, values written into the pixels", "diff", "2 of 24 MP", pair, Values: true),
            new("structural similarity", "ssim", "2 of 24 MP", pair),
        ];

        var sb = new StringBuilder();
        var rows = new StringBuilder();
        double[] switches = [];
        (int Width, int Height) client = default;
        string? similarity = null;
        foreach (Case c in cases)
        {
            Result? result = Measure(exe, c, switching: c.Mode == "single");
            if (result == null) return 1;
            client = (result.ClientWidth, result.ClientHeight);
            if (result.SwitchMs.Length > 0) switches = result.SwitchMs;
            if (result.Metrics != null)
            {
                similarity = $"The similarity map of two 24 MP images: on screen {result.MapMs:F0} ms after the start, {result.MapMs - result.ReadyMs:F0} ms after the images " +
                             $"(error numbers {Field(result.Metrics, "basic_ms")} ms, structural similarity of four channels with its map {Field(result.Metrics, "ssim_ms")} ms, on all cores).";
            }
            string F(string name) => result.Frames.GetValueOrDefault(name, "?");
            rows.AppendLine($"| {c.Label} | {c.Images} | {result.ReadyMs:F0} ms | {F("frames")} | {F("interval_mean_ms")} / {F("interval_max_ms")} ms | {F("cpu_mean_ms")} / {F("cpu_max_ms")} ms |");
        }

        sb.AppendLine($"Comparing in a window of {client.Width} x {client.Height} pixels, files in the OS cache. \"All in full\" counts from just before the");
        sb.AppendLine("viewer's process is created to the first frame that has every image of the set at full resolution. Zoom and pan: 2 s of");
        sb.AppendLine("scripted wheel and drag input, an event every 4 ms.");
        sb.AppendLine();
        sb.AppendLine("| Mode | Images | All in full on screen | Frames in 2 s | Between frames, mean / longest | CPU per frame, mean / longest |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.Append(rows);
        if (switches.Length > 0)
        {
            var stats = new Stats(switches);
            sb.AppendLine();
            sb.AppendLine($"Switching between two 24 MP images in the single view ({stats.Count} switches, 300 ms apart), key handled to `Present` returned:");
            sb.AppendLine($"median {stats.Median:F1} ms, p90 {stats.P90:F1} ms, longest {stats.Max:F1} ms.");
        }
        if (similarity != null) sb.AppendLine().AppendLine(similarity);

        Console.Write(sb);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    /// <summary>Starts the viewer on the files of a case, waits until all are on screen in full, runs the input script, closes it.</summary>
    static Result? Measure(string exe, Case c, bool switching)
    {
        string instance = "cmp" + Environment.ProcessId + "_" + Environment.TickCount64;
        string log = Path.Combine(Path.GetTempPath(), $"fiv_cmp_{instance}.txt");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string file in c.Files) start.ArgumentList.Add(file);
        // A case that changes what the viewer remembers (pixel values on) runs on a copy of the
        // profile: the same window, and nothing of it left for the cases after it.
        string profile = BenchProfile.Directory;
        if (c.Values)
        {
            profile = BenchProfile.Fresh();
            string settings = Path.Combine(BenchProfile.Directory, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(profile, "settings.json"));
        }
        foreach (string argument in new[] { "--instance", instance, "--no-activate", "--perf-log", log, "--no-panels", "--profile", profile })
            start.ArgumentList.Add(argument);
        if (c.Mode != null)
        {
            start.ArgumentList.Add("--compare-mode");
            start.ArgumentList.Add(c.Mode);
        }

        long spawned = Stopwatch.GetTimestamp();
        using Process process = Process.Start(start)!;
        try
        {
            // One image reports "full_frame=", a set "compare_ready ... ready=": both are timestamps of the same clock as ours.
            string? ready = null;
            var timeout = Stopwatch.StartNew();
            while (ready == null)
            {
                if (timeout.Elapsed.TotalSeconds > 60 || process.HasExited)
                {
                    Console.Error.WriteLine($"the viewer did not show: {c.Label}, {c.Images}");
                    return null;
                }
                Thread.Sleep(10);
                ready = Lines(log).FirstOrDefault(l => c.Mode == null ? l.Contains("full_frame=") : l.StartsWith("compare_ready "));
            }
            long readyAt = long.Parse(Field(ready, c.Mode == null ? "full_frame" : "ready")!);
            double readyMs = (readyAt - spawned) * 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(800);

            nint window = FindWindowW("FastImageViewer2." + instance, null);
            if (window == 0 || GetClientRect(window, out Rect rect) == 0)
            {
                Console.Error.WriteLine("the viewer's window was not found");
                return null;
            }
            int width = rect.Right, height = rect.Bottom;

            // The similarity view computes its map after the images are there: wait for it, so that zooming and panning are measured on the map.
            double mapMs = 0;
            string? metrics = null;
            if (c.Mode == "ssim")
            {
                var waited = Stopwatch.StartNew();
                while (metrics == null && waited.Elapsed.TotalSeconds < 60 && !process.HasExited)
                {
                    Thread.Sleep(10);
                    metrics = Lines(log).FirstOrDefault(l => l.StartsWith("metrics ") && l.Contains("map=1"));
                }
                if (metrics == null)
                {
                    Console.Error.WriteLine("the similarity map did not arrive");
                    return null;
                }
                mapMs = (long.Parse(Field(metrics, "at")!) - spawned) * 1000.0 / Stopwatch.Frequency;
                Thread.Sleep(500);
            }
            if (c.Values)
            {
                // V, then the wheel in by 34 notches at the point the script zooms and pans about: some 150 screen pixels per image pixel.
                PostMessageW(window, WM_APP_TEST_KEY, 0x56, 0);
                var at = new Point { X = width / 4, Y = height / 2 };
                ClientToScreen(window, ref at);
                PostMessageW(window, WM_MOUSEWHEEL, (120 * 34) << 16, (at.Y << 16) | (at.X & 0xFFFF));
                // The viewer says what V did in a notice that goes away after 2.6 s, with a frame of
                // its own: that frame must not fall into the measurement.
                Thread.Sleep(3200);
            }

            double[] switchMs = [];
            if (switching)
            {
                int before = StepTimes(log).Length;
                for (int i = 0; i < 20; i++)
                {
                    int key = i % 2 == 0 ? '2' : '1';
                    PostMessageW(window, WM_APP_TEST_KEY, key, 0);
                    Thread.Sleep(60);
                    PostMessageW(window, WM_APP_TEST_KEY, key, 4); // released: a tap, not a peek
                    Thread.Sleep(240);
                }
                switchMs = StepTimes(log).Skip(before).ToArray();
            }

            ZoomAndPan(window, width, height);
            Thread.Sleep(300);
            PostMessageW(window, WM_CLOSE, 0, 0);
            if (!process.WaitForExit(8000)) process.Kill();
            string summary = Lines(log).LastOrDefault(l => l.StartsWith("images_shown=")) ?? "";
            var frames = summary.Split(' ').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1]);
            return new Result(readyMs, frames, width, height, switchMs, mapMs, metrics);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            try
            {
                File.Delete(log);
                if (profile != BenchProfile.Directory) Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// One second of the wheel turning in and out over the left half of the view, then one
    /// second of dragging the image around in a circle: an event every 4 ms throughout.
    /// </summary>
    static void ZoomAndPan(nint window, int width, int height)
    {
        int x = width / 4, y = height / 2;
        var screen = new Point { X = x, Y = y };
        ClientToScreen(window, ref screen);
        nint at = (screen.Y << 16) | (screen.X & 0xFFFF);

        PostMessageW(window, WM_APP_RESET_STATISTICS, 0, 0); // count only what follows
        var watch = Stopwatch.StartNew();
        const int events = 250;
        for (int i = 0; i < events; i++)
        {
            // A tenth of a notch per event, in for 60 events, out for 60, and so on.
            int delta = i / 60 % 2 == 0 ? 12 : -12;
            PostMessageW(window, WM_MOUSEWHEEL, delta << 16, at);
            Pace(watch, (i + 1) * 4.0);
        }
        PostMessageW(window, WM_APP_TEST_MOUSE, 1, (y << 16) | x);
        double radius = Math.Min(width, height) / 6.0;
        for (int i = 0; i < events; i++)
        {
            double angle = i * (4 * Math.PI / events);
            int mx = x + (int)(radius * (Math.Cos(angle) - 1)), my = y + (int)(radius * Math.Sin(angle));
            PostMessageW(window, WM_APP_TEST_MOUSE, 0, ((my & 0xFFFF) << 16) | (mx & 0xFFFF));
            Pace(watch, (events + i + 1) * 4.0);
        }
        PostMessageW(window, WM_APP_TEST_MOUSE, 2, (y << 16) | x);
    }

    /// <summary>Waits until the watch shows the time. Thread.Sleep is too coarse for 4 ms steps, so this spins.</summary>
    static void Pace(Stopwatch watch, double untilMs)
    {
        while (watch.Elapsed.TotalMilliseconds < untilMs) Thread.SpinWait(200);
    }

    static double[] StepTimes(string log) =>
        Lines(log).Where(l => l.StartsWith("step_ms=")).Select(l => double.Parse(Field(l, "step_ms")!, CultureInfo.InvariantCulture)).ToArray();

    static string? Field(string line, string name) =>
        line.Split(' ').Select(f => f.Split('=', 2)).FirstOrDefault(f => f.Length == 2 && f[0] == name)?[1];

    static string[] Lines(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return [];
        }
    }
}
