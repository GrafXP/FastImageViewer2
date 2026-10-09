namespace Fiv.Imaging;

public static unsafe class ImageStatistics
{
    /// <summary>
    /// Smallest and largest sample of a 16-bit or float grayscale image, in the units the GPU
    /// samples: 0..1 for 16-bit, the stored values for float (non-finite samples are ignored).
    /// 8-bit layouts are always shown over their full range and return (0, 1).
    /// </summary>
    public static (float Min, float Max) ValueRange(DecodedImage image)
    {
        switch (image.Layout)
        {
            case PixelLayout.Gray16:
            {
                var rows = new (ushort Min, ushort Max)[image.Height];
                Parallel.For(0, image.Height, y =>
                {
                    var row = new ReadOnlySpan<ushort>(image.Pixels + (long)y * image.Stride, image.Width);
                    ushort min = ushort.MaxValue, max = 0;
                    foreach (ushort v in row)
                    {
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    rows[y] = (min, max);
                });
                ushort lo = ushort.MaxValue, hi = 0;
                foreach (var (min, max) in rows)
                {
                    lo = Math.Min(lo, min);
                    hi = Math.Max(hi, max);
                }
                return hi > lo ? (lo / 65535f, hi / 65535f) : (0, 1);
            }
            case PixelLayout.GrayFloat:
            {
                var rows = new (float Min, float Max)[image.Height];
                Parallel.For(0, image.Height, y =>
                {
                    var row = new ReadOnlySpan<float>(image.Pixels + (long)y * image.Stride, image.Width);
                    float min = float.PositiveInfinity, max = float.NegativeInfinity;
                    foreach (float v in row)
                    {
                        if (!float.IsFinite(v)) continue;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    rows[y] = (min, max);
                });
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                foreach (var (min, max) in rows)
                {
                    lo = Math.Min(lo, min);
                    hi = Math.Max(hi, max);
                }
                return hi > lo ? (lo, hi) : (0, 1);
            }
            default:
                return (0, 1);
        }
    }
}
