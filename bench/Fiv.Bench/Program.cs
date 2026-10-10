using Fiv.Bench;

// fivbench <command> [options]; run from anywhere inside the repository.

string repoRoot = FindRepoRoot();
string corpus = Path.Combine(repoRoot, "corpus");
string command = args.Length > 0 ? args[0] : "help";
var options = new Options(args.Skip(1).ToArray());

return command switch
{
    "corpus" => CorpusCommand.Run(options.Get("--root", corpus)),
    "bakeoff" => BakeoffCommand.Run(repoRoot,
        options.Get("--image", Path.Combine(corpus, "single", "jpeg_24mp.jpg")),
        options.GetInt("--runs", 50), options.GetInt("--warm", 20), options.GetOrNull("--out")),
    "open" => OpenCommand.Run(repoRoot,
        options.Get("--image", Path.Combine(corpus, "single", "jpeg_24mp.jpg")),
        options.GetInt("--runs", 30), options.GetOrNull("--out"), options.Has("--panels")),
    "reopen" => OpenCommand.RunWarm(repoRoot,
        options.GetOrNull("--images")?.Split(';') ?? new[] { "jpeg_12mp.jpg", "jpeg_24mp.jpg", "jpeg_24mp_422.jpg", "jpeg_50mp.jpg", "png8_24mp.png" }
            .Select(name => Path.Combine(corpus, "single", name)).ToArray(),
        options.GetInt("--runs", 15), options.GetOrNull("--out")),
    "navigate" => NavigateCommand.Run(repoRoot, options.Get("--folder", Path.Combine(corpus, "series", "jpeg_12mp")), options.GetOrNull("--out"), options.Has("--panels")),
    "browse" => BrowseCommand.Run(repoRoot, options.Get("--root", corpus), options.GetOrNull("--out")),
    "compare" => CompareCommand.Run(repoRoot, options.Get("--root", corpus), options.GetOrNull("--out")),
    "metrics" => MetricsCommand.Run(options.Get("--root", corpus), options.GetInt("--reps", 5), options.GetOrNull("--out")),
    "thumbs" => ThumbsCommand.Run(options.Get("--root", corpus), options.GetOrNull("--out")),
    "decode" => DecodeCommand.Run(options.Get("--root", corpus), options.GetInt("--reps", 7), options.GetOrNull("--out")),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        fivbench corpus  [--root <dir>]                       generate the test corpus (default: <repo>\corpus)
        fivbench bakeoff [--image <jpg>] [--runs 50] [--warm 20] [--out <file.md>]
                                                              C# Native AOT vs C++ time to first pixel
        fivbench open    [--image <file>] [--runs 30] [--panels] [--out <file.md>]
                                                              cold start of publish\FastImageViewer2.exe, stage by stage
        fivbench reopen  [--images <a;b;...>] [--runs 15] [--out <file.md>]
                                                              open with the viewer already running (second process hands over)
        fivbench navigate [--folder <dir>] [--panels] [--out <file.md>]
                                                              next/previous in a folder: single steps, and the arrow key held
        fivbench browse  [--root <dir>] [--out <file.md>]      the folder panel: first screen of thumbnails, new and seen before
        fivbench compare [--root <dir>] [--out <file.md>]      the compare modes: loading a set, zoom and pan in each mode, switching
        fivbench metrics [--root <dir>] [--reps 5] [--out <file.md>]
                                                              comparing on the CPU: error metrics, structural similarity, saving the views
        fivbench thumbs  [--root <dir>] [--out <file.md>]      thumbnails: making them, and reading them back from the pack
        fivbench decode  [--root <dir>] [--reps 7] [--out <file.md>]
                                                              decoder timings on the corpus
        """);
    return 2;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "PLAN.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

sealed class Options(string[] args)
{
    public bool Has(string name) => Array.IndexOf(args, name) >= 0;

    public string? GetOrNull(string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public string Get(string name, string fallback) => GetOrNull(name) ?? fallback;
    public int GetInt(string name, int fallback) => GetOrNull(name) is { } value ? int.Parse(value) : fallback;
}
