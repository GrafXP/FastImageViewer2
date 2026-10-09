using Windows.Win32;

namespace Fiv.App;

internal sealed class Options
{
    public List<string> Files { get; } = [];

    /// <summary>Do not hand the files to a running instance; do not become the instance others hand to.</summary>
    public bool NewInstance { get; private set; }

    /// <summary>Show the window without taking the keyboard focus (benchmark runs).</summary>
    public bool NoActivate { get; private set; }

    /// <summary>Write the stage timestamps to this file once the full image is on screen.</summary>
    public string? PerfOut { get; private set; }

    /// <summary>Close once the full image is on screen (benchmark runs).</summary>
    public bool ExitWhenLoaded { get; private set; }

    /// <summary>Append one line of timestamps per image shown (benchmark runs).</summary>
    public string? PerfLog { get; private set; }

    /// <summary>Hold back the Direct3D device by this many milliseconds, to look at what GDI paints before it (testing).</summary>
    public int DelayGpuMs { get; private set; }

    /// <summary>Budgets of the image cache in megabytes: decoded pixels in RAM, textures in video memory.</summary>
    public int CacheMb { get; private set; } = 4096;
    public int VramMb { get; private set; } = 2048;

    /// <summary>
    /// Where settings, the panel layout and the thumbnail cache are kept. Default:
    /// %LOCALAPPDATA%\FastImageViewer2. Tests and benchmarks point it elsewhere to leave the user's alone.
    /// </summary>
    public string ProfileDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastImageViewer2");

    public string ThumbsDir => Path.Combine(ProfileDir, "thumbs");

    /// <summary>Start with the folder panel / the filmstrip open, whatever the settings say (tests).</summary>
    public bool ShowBrowser { get; private set; }
    public bool ShowFilmstrip { get; private set; }

    /// <summary>Load only the image asked for, not its neighbours (benchmark runs).</summary>
    public bool NoPrefetch { get; private set; }

    /// <summary>A separate single-instance group, so that benchmark runs never talk to the user's own viewer.</summary>
    public string Instance { get; private set; } = "";

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--new-instance": options.NewInstance = true; break;
                case "--no-activate": options.NoActivate = true; break;
                case "--exit-when-loaded": options.ExitWhenLoaded = true; break;
                case "--perf-out" when i + 1 < args.Length: options.PerfOut = args[++i]; break;
                case "--perf-log" when i + 1 < args.Length: options.PerfLog = args[++i]; break;
                case "--instance" when i + 1 < args.Length: options.Instance = args[++i]; break;
                case "--no-prefetch": options.NoPrefetch = true; break;
                case "--browser": options.ShowBrowser = true; break;
                case "--filmstrip": options.ShowFilmstrip = true; break;
                case "--profile" when i + 1 < args.Length: options.ProfileDir = Path.GetFullPath(args[++i]); break;
                case "--cache-mb" when i + 1 < args.Length && int.TryParse(args[i + 1], out int cacheMb): options.CacheMb = Math.Max(0, cacheMb); i++; break;
                case "--vram-mb" when i + 1 < args.Length && int.TryParse(args[i + 1], out int vramMb): options.VramMb = Math.Max(0, vramMb); i++; break;
                case "--delay-gpu" when i + 1 < args.Length && int.TryParse(args[i + 1], out int delay): options.DelayGpuMs = delay; i++; break;
                default:
                    // Full paths, because a running instance that receives them has another working directory.
                    try
                    {
                        options.Files.Add(Path.GetFullPath(args[i]));
                    }
                    catch (ArgumentException)
                    {
                    }
                    break;
            }
        }
        return options;
    }
}

internal static class Program
{
    static int Main(string[] args)
    {
        Perf.Stamp("main");
        Options options = Options.Parse(args);
        if (!options.NewInstance && SingleInstance.TryForward(options.Files, options.Instance)) return 0;

        unsafe
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            PInvoke.SetProcessDpiAwarenessContext(new Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT((void*)-4));
        }
        return new MainWindow(options).Run();
    }
}
