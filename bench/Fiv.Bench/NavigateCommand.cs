using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Fiv.Bench;

/// <summary>
/// Navigation timing of the real viewer in a folder of images, driven with key messages:
/// single steps with a pause between them (the next image is prefetched by then), and the right
/// arrow held down at keyboard repeat rate through the whole folder.
/// </summary>
public static partial class NavigateCommand
{
    const uint WM_KEYDOWN = 0x0100, WM_CLOSE = 0x0010;
    const int VK_RIGHT = 0x27, VK_LEFT = 0x25;

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32")]
    private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    public static int Run(string repoRoot, string folder, string? outFile, bool panels = false)
    {
        string exe = Path.Combine(repoRoot, "publish", "FastImageViewer2.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"missing {exe}; run build.ps1 first");
            return 1;
        }
        string[] files = Directory.GetFiles(folder, "*.jpg");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length < 10)
        {
            Console.Error.WriteLine($"{folder} needs at least 10 JPEGs; run 'fivbench corpus'");
            return 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Folder: {Path.GetFileName(folder)}, {files.Length} JPEG files, {files.Sum(f => new FileInfo(f).Length) / 1e6 / files.Length:F1} MB on average. Folder panel {(panels ? "open" : "closed")}.");
        sb.AppendLine();

        // 1. Single steps, 600 ms apart: 15 forward, 5 back.
        s_panels = panels;
        var stepLines = Drive(exe, files[0], window =>
        {
            for (int i = 0; i < 20; i++)
            {
                PostMessageW(window, WM_KEYDOWN, i < 15 ? VK_RIGHT : VK_LEFT, 0);
                Thread.Sleep(600);
            }
        });
        if (stepLines == null) return 1;
        var steps = new Stats(StepTimes(stepLines).Skip(1)); // the first line is the initial open
        sb.AppendLine("**Single steps to a prefetched neighbour** (key message handled to `Present` returned; the picture is on screen at the next refresh):");
        sb.AppendLine();
        sb.AppendLine($"{steps.Count} steps: median {steps.Median:F1} ms, p90 {steps.P90:F1} ms, longest {steps.Max:F1} ms.");
        sb.AppendLine();

        // 2. Right arrow held: one key message every 33 ms until the last file.
        int presses = files.Length - 1;
        var holdWatch = new Stopwatch();
        var holdLines = Drive(exe, files[0], window =>
        {
            // Keyboard auto-repeat is at most about 30 per second. Thread.Sleep is too coarse to
            // pace that (it rounds up to the 15.6 ms system tick), so the wait spins.
            holdWatch.Start();
            for (int i = 0; i < presses; i++)
            {
                PostMessageW(window, WM_KEYDOWN, VK_RIGHT, 0);
                double next = (i + 1) * (1000.0 / 30);
                while (holdWatch.Elapsed.TotalMilliseconds < next) Thread.SpinWait(200);
            }
            holdWatch.Stop();
            Thread.Sleep(1500);
        });
        if (holdLines == null) return 1;
        string summary = holdLines.Last(l => l.StartsWith("images_shown="));
        int shown = int.Parse(Field(summary, "images_shown")) - 1; // minus the image opened first
        var held = new Stats(StepTimes(holdLines).Skip(1));
        sb.AppendLine($"**Right arrow held** ({presses} key messages, one every {holdWatch.Elapsed.TotalMilliseconds / presses:F0} ms):");
        sb.AppendLine();
        sb.AppendLine($"{shown} of {presses} images shown in {holdWatch.Elapsed.TotalSeconds:F2} s = {shown / holdWatch.Elapsed.TotalSeconds:F0} images/s; " +
                      $"key to frame: median {held.Median:F1} ms, p90 {held.P90:F1} ms, longest {held.Max:F1} ms; ended on {Field(summary, "last_index")}.");

        Console.Write(sb);
        if (outFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, sb.ToString());
        }
        return 0;
    }

    static bool s_panels;

    static IEnumerable<double> StepTimes(string[] lines) =>
        lines.Where(l => l.StartsWith("step_ms=")).Select(l => double.Parse(Field(l, "step_ms"), System.Globalization.CultureInfo.InvariantCulture));

    static string Field(string line, string name) =>
        line.Split(' ').Select(f => f.Split('=', 2)).First(f => f.Length == 2 && f[0] == name)[1];

    /// <summary>Starts the viewer on a file, waits until the full image is shown, runs the input script, closes it, returns its log.</summary>
    static string[]? Drive(string exe, string file, Action<nint> script)
    {
        string instance = "nav" + Environment.ProcessId + "_" + Environment.TickCount64;
        string log = Path.Combine(Path.GetTempPath(), $"fiv_nav_{instance}.txt");
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { file, "--instance", instance, "--no-activate", "--perf-log", log, s_panels ? "--browser" : "--no-panels", "--profile", BenchProfile.Directory },
        })!;
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!(File.Exists(log) && ReadShared(log).Contains("full_frame=")))
            {
                if (timeout.Elapsed.TotalSeconds > 30 || process.HasExited)
                {
                    Console.Error.WriteLine("the viewer did not show the first image");
                    return null;
                }
                Thread.Sleep(20);
            }
            Thread.Sleep(1200); // folder index and prefetch of the neighbours
            nint window = FindWindowW("FastImageViewer2." + instance, null);
            if (window == 0)
            {
                Console.Error.WriteLine("the viewer's window was not found");
                return null;
            }
            script(window);
            PostMessageW(window, WM_CLOSE, 0, 0);
            if (!process.WaitForExit(5000)) process.Kill();
            return ReadShared(log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            try
            {
                File.Delete(log);
            }
            catch (IOException)
            {
            }
        }
    }

    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
