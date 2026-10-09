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

public enum FolderSort
{
    Name,
    Modified,
    Size,
    Type,

    /// <summary>By pixel count. Images whose dimensions are not known yet sort as if they had none.</summary>
    Dimensions,
}

/// <summary>Decides from a file name whether a file belongs to a filtered view.</summary>
public delegate bool NameFilter(ReadOnlySpan<char> name);

/// <summary>
/// The image files of a folder in some order: a list of positions into a <see cref="FolderListing"/>.
/// Sorting differently or filtering makes another index over the same listing; the file data is
/// never copied. Immutable once built, so it can be handed between threads.
/// </summary>
public sealed class FolderIndex
{
    readonly int[] _order; // position in this view -> index in the listing

    public string Directory { get; }
    public FolderListing Listing { get; }
    public FolderSort Sort { get; }
    public bool Descending { get; }
    public int Count => _order.Length;
    public double EnumerateMs { get; }
    public double SortMs { get; }

    FolderIndex(string directory, FolderListing listing, int[] order, FolderSort sort, bool descending, double enumerateMs, double sortMs)
    {
        Directory = directory;
        Listing = listing;
        _order = order;
        Sort = sort;
        Descending = descending;
        EnumerateMs = enumerateMs;
        SortMs = sortMs;
    }

    /// <summary>The listing's index of the file at a position: the same for a file in every view of the listing.</summary>
    public int ListingIndexAt(int position) => _order[position];

    public ReadOnlySpan<char> NameAt(int position) => Listing.Name(_order[position]);
    public string PathAt(int position) => Path.Join(Directory, NameAt(position));
    public long SizeAt(int position) => Listing.Size(_order[position]);

    /// <summary>Last write time as a Windows FILETIME.</summary>
    public long ModifiedAt(int position) => Listing.ModifiedFileTime(_order[position]);

    /// <summary>The position of a file name, or -1.</summary>
    public int Find(ReadOnlySpan<char> fileName)
    {
        for (int position = 0; position < _order.Length; position++)
            if (NameAt(position).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return position;
        return -1;
    }

    /// <summary>Reads the directory and sorts it.</summary>
    public static FolderIndex Build(string directory, FolderSort sort = FolderSort.Name, bool descending = false)
    {
        long started = Stopwatch.GetTimestamp();
        FolderListing listing = FolderListing.Read(directory);
        double enumerateMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int[] order = new int[listing.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        return Order(directory, listing, order, sort, descending, enumerateMs);
    }

    /// <summary>The same files in another order.</summary>
    public FolderIndex Sorted(FolderSort sort, bool descending) =>
        Order(Directory, Listing, (int[])_order.Clone(), sort, descending, EnumerateMs);

    /// <summary>The files of this view whose names pass the filter, in this view's order.</summary>
    public FolderIndex Where(NameFilter filter)
    {
        var kept = new List<int>(_order.Length);
        foreach (int index in _order)
            if (filter(Listing.Name(index))) kept.Add(index);
        return new FolderIndex(Directory, Listing, kept.ToArray(), Sort, Descending, EnumerateMs, SortMs);
    }

    static FolderIndex Order(string directory, FolderListing listing, int[] order, FolderSort sort, bool descending, double enumerateMs)
    {
        long started = Stopwatch.GetTimestamp();
        Comparison<int> byName = (x, y) => NaturalOrder.Compare(listing.Name(x), listing.Name(y));
        Comparison<int> compare = sort switch
        {
            FolderSort.Modified => (x, y) => listing.ModifiedFileTime(x).CompareTo(listing.ModifiedFileTime(y)) is var c and not 0 ? c : byName(x, y),
            FolderSort.Size => (x, y) => listing.Size(x).CompareTo(listing.Size(y)) is var c and not 0 ? c : byName(x, y),
            FolderSort.Type => (x, y) => Extension(listing.Name(x)).CompareTo(Extension(listing.Name(y)), StringComparison.OrdinalIgnoreCase) is var c and not 0 ? c : byName(x, y),
            FolderSort.Dimensions => (x, y) => listing.PixelCount(x).CompareTo(listing.PixelCount(y)) is var c and not 0 ? c : byName(x, y),
            _ => byName,
        };
        ParallelSort(order, descending ? (x, y) => compare(y, x) : compare);
        return new FolderIndex(directory, listing, order, sort, descending, enumerateMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    static ReadOnlySpan<char> Extension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? default : name[(dot + 1)..];
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
