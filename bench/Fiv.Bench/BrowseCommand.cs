using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Fiv.Bench;

/// <summary>
/// The folder panel of the real viewer: how long until the first screenful of thumbnails is
/// complete for a folder never seen (thumbnails have to be made), for the same folder in a later
/// run (they come from the pack on disk), and whether a folder of 95,000 images scrolls at the
/// screen's refresh rate while its thumbnails stream in.
/// </summary>
public static partial class BrowseCommand
{
    const uint WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_CLOSE = 0x0010, WM_APP_RESET_STATISTICS = 0x8000 + 20;

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32")]
    private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    public static int Run(string repoRoot, string corpus, string? outFile)
    {
        string exe = Path.Combine(repoRoot, "publish", "FastImageViewer2.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"missing {exe}; run build.ps1 first");
            return 1;
        }
        string start = Path.Combine(corpus, "folders", "1k", "IMG_0.jpg");
        (string Path, string Label)[] folders =
        [
            (Path.Combine(corpus, "series", "jpeg_12mp_x3"), "120 JPEGs of 12 MP"),
            (Path.Combine(corpus, "series", "jpeg_12mp_exif"), "40 JPEGs of 12 MP with embedded thumbnails"),
        ];
        string big = Path.Combine(corpus, "folders", "100k");
        if (!File.Exists(start) || folders.Any(f => !Directory.Exists(f.Path)) || !Directory.Exists(big))
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }

        string profile = BenchProfile.Fresh();
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("First screenful of thumbnails in the folder panel (128 px), milliseconds from asking for the folder:");
            sb.AppendLine();
            sb.AppendLine("| Folder | State | Thumbnails on screen | Folder listed after | All thumbnails after |");
            sb.AppendLine("|---|---|---|---|---|");

            // Run 1, fresh profile: every folder is new.
            using (var viewer = Viewer.Start(exe, start, profile))
            {
                if (viewer == null) return 1;
                foreach (var (path, label) in folders)
                {
                    var screen = viewer.OpenFolder(exe, path);
                    if (screen == null) return 1;
                    Row(sb, label, "new", screen);
                }
            }
            // Run 2, same profile: the packs are on disk.
            string scroll;
            using (var viewer = Viewer.Start(exe, start, profile))
            {
                if (viewer == null) return 1;
                foreach (var (path, label) in folders)
                {
                    var screen = viewer.OpenFolder(exe, path);
                    if (screen == null) return 1;
                    Row(sb, label, "seen before", screen);
                }

                var first = viewer.OpenFolder(exe, big);
                if (first == null) return 1;
                Row(sb, "95,000 small images", "new", first);

                // Wheel down through the folder for four seconds, 60 notches a second.
                nint window = viewer.Window;
                PostMessageW(window, WM_MOUSEMOVE, 0, (300 << 16) | 150);
                Thread.Sleep(300);
                PostMessageW(window, WM_APP_RESET_STATISTICS, 0, 0); // count only the scrolling
                var watch = Stopwatch.StartNew();
                const int notches = 240;
                for (int i = 0; i < notches; i++)
                {
                    PostMessageW(window, WM_MOUSEWHEEL, unchecked((nint)(-120 << 16)), 0);
                    double next = (i + 1) * (1000.0 / 60);
                    while (watch.Elapsed.TotalMilliseconds < next) Thread.SpinWait(200);
                }
                Thread.Sleep(1500);
                scroll = viewer.Close();
            }
            string Field(string name) => scroll.Split(' ').Select(f => f.Split('=', 2)).FirstOrDefault(f => f.Length == 2 && f[0] == name)?[1] ?? "?";
            sb.AppendLine();
            sb.AppendLine("Scrolling the folder of 95,000 with the mouse wheel for 4 s (60 notches per second) while thumbnails are made:");
            sb.AppendLine();
            sb.AppendLine($"{Field("frames")} frames, {Field("interval_mean_ms")} ms between frames on average, {Field("interval_max_ms")} ms at most; " +
                          $"{Field("cpu_mean_ms")} ms of CPU per frame on average, {Field("cpu_max_ms")} ms at most; {Field("thumbs_uploaded")} thumbnails made and shown while scrolling.");
        }
        finally
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        Console.Write(sb);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static void Row(StringBuilder sb, string label, string state, Dictionary<string, string> screen) =>
        sb.AppendLine($"| {label} | {state} | {screen["count"]} | {screen["listed_ms"]} | {screen["total_ms"]} |");

    /// <summary>A running viewer with the folder panel open, driven through its single-instance hand-over and its log.</summary>
    sealed class Viewer : IDisposable
    {
        readonly Process _process;
        readonly string _instance, _log;
        int _screens;

        Viewer(Process process, string instance, string log)
        {
            _process = process;
            _instance = instance;
            _log = log;
        }

        public nint Window => FindWindowW("FastImageViewer2." + _instance, null);

        public static Viewer? Start(string exe, string file, string profile)
        {
            string instance = "browse" + Environment.ProcessId + "_" + Environment.TickCount64;
            string log = Path.Combine(Path.GetTempPath(), $"fiv_browse_{instance}.txt");
            var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList = { file, "--instance", instance, "--no-activate", "--perf-log", log, "--profile", profile, "--browser" },
            })!;
            var viewer = new Viewer(process, instance, log);
            if (viewer.WaitForScreen() == null)
            {
                Console.Error.WriteLine("the viewer did not show its first folder");
                viewer.Dispose();
                return null;
            }
            Thread.Sleep(1200);
            return viewer;
        }

        /// <summary>Opens a folder through a second process, as Explorer would, and waits for its first screenful of thumbnails.</summary>
        public Dictionary<string, string>? OpenFolder(string exe, string folder)
        {
            using (var forwarder = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { folder, "--instance", _instance } })!)
                forwarder.WaitForExit();
            var screen = WaitForScreen();
            if (screen == null) Console.Error.WriteLine($"no thumbnails appeared for {folder}");
            Thread.Sleep(1200); // let the rows beyond the screen finish before the next measurement
            return screen;
        }

        Dictionary<string, string>? WaitForScreen()
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed.TotalSeconds < 90 && !_process.HasExited)
            {
                string[] lines = Lines().Where(l => l.StartsWith("thumbs_screen ")).ToArray();
                if (lines.Length > _screens)
                {
                    _screens = lines.Length;
                    return lines[^1].Split(' ').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1]);
                }
                Thread.Sleep(10);
            }
            return null;
        }

        string[] Lines()
        {
            if (!File.Exists(_log)) return [];
            using var stream = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Closes the viewer and returns its closing statistics line.</summary>
        public string Close()
        {
            PostMessageW(Window, WM_CLOSE, 0, 0);
            if (!_process.WaitForExit(8000)) _process.Kill();
            return Lines().LastOrDefault(l => l.StartsWith("images_shown=")) ?? "";
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                PostMessageW(Window, WM_CLOSE, 0, 0);
                if (!_process.WaitForExit(8000)) _process.Kill();
            }
            _process.Dispose();
            try
            {
                File.Delete(_log);
            }
            catch (IOException)
            {
            }
        }
    }
}
