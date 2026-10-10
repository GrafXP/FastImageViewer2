using Fiv.Indexing;

namespace Fiv.Comparing;

/// <summary>
/// Tells which files were opened together. Explorer opens a selection of several files by
/// starting the viewer once for each of them, and each start hands its one file to the viewer
/// already running: all that makes them a selection is that they arrive close together.
///
/// Measured with 15 files of 12 to 50 MP opened through the shell: 10 to 50 ms from one to the
/// next, and once 170 ms, with every core decoding. The second file has to follow the first
/// faster than a person opens two files one after the other; after that it is known to be a
/// selection, and the rest may take longer, as on a slower or busier machine.
/// </summary>
public sealed class OpenedTogether
{
    /// <summary>How long after the first file the second still belongs to it, and how long after any later one the next, in milliseconds.</summary>
    public const long FirstMs = 500, MoreMs = 1500;

    readonly List<string> _files = [];
    long _until;

    /// <summary>
    /// The files of the selection so far, in the order of their names: the order they arrive in
    /// is an accident of how fast each start went.
    /// </summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>Whatever comes next starts a selection of its own.</summary>
    public void Clear() => _files.Clear();

    /// <summary>
    /// A file was opened on its own at <paramref name="now"/>, in milliseconds of a clock that
    /// only runs forward. False if it is among the files already (opened twice); otherwise
    /// <see cref="Files"/> is what is to be shown: this file alone, or the selection it joined.
    /// </summary>
    public bool Add(string file, long now)
    {
        if (now >= _until) _files.Clear();
        if (_files.Exists(known => string.Equals(known, file, StringComparison.OrdinalIgnoreCase))) return false;
        _files.Add(file);
        _until = now + (_files.Count == 1 ? FirstMs : MoreMs);
        _files.Sort(static (a, b) =>
        {
            int order = NaturalOrder.Compare(Path.GetFileName(a.AsSpan()), Path.GetFileName(b.AsSpan()));
            return order != 0 ? order : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return true;
    }
}
