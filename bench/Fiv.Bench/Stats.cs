namespace Fiv.Bench;

/// <summary>Order statistics of a set of timings in milliseconds.</summary>
public readonly struct Stats
{
    readonly double[] _sorted;

    public Stats(IEnumerable<double> values)
    {
        _sorted = values.ToArray();
        Array.Sort(_sorted);
    }

    public int Count => _sorted.Length;
    public double Min => _sorted.Length == 0 ? double.NaN : _sorted[0];
    public double Max => _sorted.Length == 0 ? double.NaN : _sorted[^1];
    public double Median => Percentile(50);
    public double P10 => Percentile(10);
    public double P90 => Percentile(90);

    public double Percentile(double p)
    {
        if (_sorted.Length == 0) return double.NaN;
        double rank = p / 100 * (_sorted.Length - 1);
        int low = (int)Math.Floor(rank), high = (int)Math.Ceiling(rank);
        return _sorted[low] + (_sorted[high] - _sorted[low]) * (rank - low);
    }

    /// <summary>"median (p10–p90)"</summary>
    public override string ToString() => $"{Median:F1} ({P10:F1}–{P90:F1})";
}
