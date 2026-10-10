using Fiv.Comparing;
using Fiv.Imaging;

namespace Fiv.Measuring;

public static class MeasureUnits
{
    /// <summary>
    /// The units the values of several images are given in together, and what a stored sample of
    /// each is multiplied by to get there (<paramref name="scales"/>, an entry per layout). Images
    /// of one depth keep their values as stored. Where the depths differ, 8- and 16-bit samples
    /// are taken as 0..1 and float ones as stored, as for two compared images
    /// (<see cref="CompareMetrics.Units"/>).
    /// </summary>
    public static ValueUnit Common(ReadOnlySpan<PixelLayout> layouts, Span<float> scales)
    {
        if (layouts.Length == 0) return ValueUnit.Bits8;
        int depth = CompareSource.DepthOf(layouts[0]);
        bool same = true;
        foreach (PixelLayout layout in layouts) same &= CompareSource.DepthOf(layout) == depth;
        for (int i = 0; i < layouts.Length; i++)
        {
            int own = CompareSource.DepthOf(layouts[i]);
            scales[i] = same ? 1f : own == 8 ? 1f / 255 : own == 16 ? 1f / 65535 : 1f;
        }
        if (!same) return ValueUnit.Normalised;
        return depth == 8 ? ValueUnit.Bits8 : depth == 16 ? ValueUnit.Bits16 : ValueUnit.Float;
    }
}
