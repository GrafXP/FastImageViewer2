namespace Fiv.Comparing;

/// <summary>
/// The overlay of several images, each with an opacity of its own, in no particular order. Where
/// they have pixels, each counts by its opacity: all at 1 they make an equal mix, at 0 an image
/// is not there. Together they cover what is behind them as much as layers of those opacities
/// would, 1 - (1 - a1)(1 - a2)..., so one image at a half is half see-through.
/// </summary>
public static class OverlayMix
{
    /// <summary>
    /// The same drawn as layer over layer, which is what the GPU blends: the opacity of each pass,
    /// the first over the background and each one after it over those before, that gives the
    /// overlay of <paramref name="opacities"/> in that order. Exact where every image has a pixel.
    /// </summary>
    public static void PassOpacities(ReadOnlySpan<float> opacities, Span<float> passes)
    {
        double sum = 0, through = 1;
        foreach (float opacity in opacities)
        {
            double a = Math.Clamp(opacity, 0f, 1f);
            sum += a;
            through *= 1 - a;
        }
        double covered = 1 - through;
        // From the top down: image k's share of the result, a_k * covered / sum, is what its pass
        // lays down times what the passes above it leave of it, 1 - covered * (the opacities above) / sum.
        double above = 0;
        for (int k = opacities.Length - 1; k >= 0; k--)
        {
            double a = Math.Clamp(opacities[k], 0f, 1f), left = sum - covered * above;
            passes[k] = sum > 0 && left > 0 ? (float)Math.Clamp(a * covered / left, 0, 1) : 0;
            above += a;
        }
    }
}
