using System.Diagnostics;

namespace Fiv.Indexing;

/// <summary>Compares file names the way people read them: "img2" before "img10", case ignored.</summary>
public static class NaturalOrder
{
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0, tie = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                // Two numbers: the one with more digits after its leading zeros is larger.
                int startA = i, startB = j;
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                int digitsA = i, digitsB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                int lengthA = i - digitsA, lengthB = j - digitsB;
                if (lengthA != lengthB) return lengthA - lengthB;
                int order = a.Slice(digitsA, lengthA).SequenceCompareTo(b.Slice(digitsB, lengthB));
                if (order != 0) return order;
                if (tie == 0) tie = (digitsA - startA) - (digitsB - startB); // "7" before "007"
                continue;
            }
            if (ca != cb)
            {
                char ua = Upper(ca), ub = Upper(cb);
                if (ua != ub) return ua - ub;
                if (tie == 0) tie = ca - cb;
            }
            i++;
            j++;
        }
        int rest = (a.Length - i) - (b.Length - j);
        return rest != 0 ? rest : tie;
    }

    static char Upper(char c) => (uint)(c - 'a') <= 'z' - 'a' ? (char)(c - 32) : c < 128 ? c : char.ToUpperInvariant(c);
}

/// <summary>The image files of a folder in natural name order. Built once on a background thread, then read-only.</summary>
public sealed class FolderIndex
{
    readonly int[] _order; // position in sorted order -> index in the listing

    public string Directory { get; }
    public FolderListing Listing { get; }
    public int Count => _order.Length;
    public double EnumerateMs { get; }
    public double SortMs { get; }

    FolderIndex(string directory, FolderListing listing, int[] order, double enumerateMs, double sortMs)
    {
        Directory = directory;
        Listing = listing;
        _order = order;
        EnumerateMs = enumerateMs;
        SortMs = sortMs;
    }

    public ReadOnlySpan<char> NameAt(int position) => Listing.Name(_order[position]);
    public string PathAt(int position) => Path.Join(Directory, NameAt(position));
    public long SizeAt(int position) => Listing.Size(_order[position]);

    /// <summary>The position of a file name, or -1.</summary>
    public int Find(ReadOnlySpan<char> fileName)
    {
        for (int position = 0; position < _order.Length; position++)
            if (NameAt(position).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return position;
        return -1;
    }

    public static FolderIndex Build(string directory)
    {
        long started = Stopwatch.GetTimestamp();
        FolderListing listing = FolderListing.Read(directory);
        double enumerateMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        started = Stopwatch.GetTimestamp();
        int[] order = new int[listing.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        ParallelSort(order, (x, y) => NaturalOrder.Compare(listing.Name(x), listing.Name(y)));
        return new FolderIndex(directory, listing, order, enumerateMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Sorts chunks on all cores, then merges them pairwise. Small inputs are sorted directly.</summary>
    static void ParallelSort(int[] items, Comparison<int> compare)
    {
        int count = items.Length;
        int parts = count < 4_000 ? 1 : Math.Min(Environment.ProcessorCount, 16);
        if (parts == 1)
        {
            Array.Sort(items, compare);
            return;
        }
        int[] bounds = new int[parts + 1];
        for (int i = 0; i <= parts; i++) bounds[i] = (int)((long)count * i / parts);
        Parallel.For(0, parts, p => items.AsSpan(bounds[p], bounds[p + 1] - bounds[p]).Sort(compare));

        int[] source = items, target = new int[count];
        for (int width = 1; width < parts; width *= 2)
        {
            int[] from = source, to = target;
            int step = 2 * width;
            Parallel.For(0, (parts + step - 1) / step, k =>
            {
                int low = bounds[Math.Min(parts, step * k)], middle = bounds[Math.Min(parts, step * k + width)];
                int high = bounds[Math.Min(parts, step * (k + 1))];
                int a = low, b = middle, o = low;
                while (a < middle && b < high) to[o++] = compare(from[b], from[a]) < 0 ? from[b++] : from[a++];
                while (a < middle) to[o++] = from[a++];
                while (b < high) to[o++] = from[b++];
            });
            (source, target) = (target, source);
        }
        if (source != items) Array.Copy(source, items, count);
    }
}
