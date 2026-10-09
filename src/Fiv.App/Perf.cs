using System.Diagnostics;
using System.Text;

namespace Fiv.App;

/// <summary>
/// Timestamps of the stages between process start and the full image on screen. Marked from any
/// thread, shown by the F12 overlay and written by --perf-out.
/// </summary>
internal static class Perf
{
    public readonly record struct Mark(string Name, long Ticks);

    static readonly Mark[] s_marks = new Mark[128];
    static int s_count;

    public static void Stamp(string name) => Stamp(name, Stopwatch.GetTimestamp());

    public static void Stamp(string name, long ticks)
    {
        int index = Interlocked.Increment(ref s_count) - 1;
        if (index < s_marks.Length) s_marks[index] = new Mark(name, ticks);
    }

    /// <summary>Marks a stage only the first time it is reached.</summary>
    public static void StampOnce(ref bool done, string name) => StampOnce(ref done, name, Stopwatch.GetTimestamp());

    public static void StampOnce(ref bool done, string name, long ticks)
    {
        if (done) return;
        done = true;
        Stamp(name, ticks);
    }

    public static ReadOnlySpan<Mark> Marks => s_marks.AsSpan(0, Math.Min(Volatile.Read(ref s_count), s_marks.Length));

    public static double MillisecondsSinceStart(long ticks) =>
        s_count == 0 ? 0 : (ticks - s_marks[0].Ticks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>One "name=ticks" pair per mark, plus the timer frequency, for the benchmark harness.</summary>
    public static string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("qpf=").Append(Stopwatch.Frequency);
        foreach (Mark mark in Marks)
        {
            if (mark.Name == null) continue; // being written by another thread
            sb.Append('\n').Append(mark.Name.Replace(' ', '_')).Append('=').Append(mark.Ticks);
        }
        return sb.Append('\n').ToString();
    }
}
