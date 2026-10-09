namespace Fiv.Bench;

/// <summary>
/// The viewer started by a benchmark keeps its settings, panel layout and thumbnails here, not
/// in the user's profile: a benchmark must neither depend on nor change how the user has set up the viewer.
/// </summary>
public static class BenchProfile
{
    public static string Directory { get; } = Path.Combine(Path.GetTempPath(), "fivbench-profile");

    /// <summary>A profile that has never been used: no settings, no layout, no thumbnails.</summary>
    public static string Fresh()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fivbench-profile-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }
}
