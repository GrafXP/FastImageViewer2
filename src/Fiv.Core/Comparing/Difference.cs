using System.Numerics;

namespace Fiv.Comparing;

/// <summary>What is shown of the difference between two images.</summary>
public enum DifferenceKind
{
    /// <summary>How far apart the two values are.</summary>
    Absolute,

    /// <summary>The second value less the first, with its sign.</summary>
    Signed,

    /// <summary>The square of the difference, which brings out the large ones.</summary>
    Squared,

    /// <summary>The difference as a fraction of the first image's value.</summary>
    Relative,
}

/// <summary>
/// The difference between two pixels as the difference view shows it. The shader
/// (combine.hlsl) and the export on the CPU both follow these definitions; they are here so
/// that there is one place to read them and to test them.
/// </summary>
public static class DifferenceMath
{
    /// <summary>How much red, green and blue count towards luma (Rec. 709).</summary>
    public static readonly Vector3 LumaWeights = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Channel selections, numbered as the display controls number them.</summary>
    public const int Colour = 0, Red = 1, Green = 2, Blue = 3, Alpha = 4, Luma = 5;

    public static float LumaOf(Vector4 color) => color.X * LumaWeights.X + color.Y * LumaWeights.Y + color.Z * LumaWeights.Z;

    /// <summary>
    /// The second pixel less the first (R, G, B, A in X, Y, Z, W) for a channel selection. For
    /// colour that is the difference of the channel that differs most, so that no change in a
    /// single channel goes unseen. <paramref name="reference"/> is the first image's value of
    /// the same channel, which a relative difference is taken of.
    /// </summary>
    public static float Signed(Vector4 first, Vector4 second, int channel, out float reference)
    {
        Vector4 d = second - first;
        switch (channel)
        {
            case Red:
                reference = first.X;
                return d.X;
            case Green:
                reference = first.Y;
                return d.Y;
            case Blue:
                reference = first.Z;
                return d.Z;
            case Alpha:
                reference = first.W;
                return d.W;
            case Luma:
                reference = LumaOf(first);
                return LumaOf(d);
            default:
            {
                float r = MathF.Abs(d.X), g = MathF.Abs(d.Y), b = MathF.Abs(d.Z);
                if (r >= g && r >= b)
                {
                    reference = first.X;
                    return d.X;
                }
                if (g >= b)
                {
                    reference = first.Y;
                    return d.Y;
                }
                reference = first.Z;
                return d.Z;
            }
        }
    }

    /// <summary>
    /// Where on the colour map a difference lies, 0..1 once clamped. <paramref name="scale"/> is
    /// gain / full scale: at gain 1 a difference of the full value range reaches the end of the
    /// map (for the signed kind, either end, with no difference in the middle). A relative
    /// difference reaches the end at 100 % / gain; values of the first image smaller than
    /// <paramref name="floor"/> count as that, so that near-black pixels do not blow up.
    /// </summary>
    public static float Position(DifferenceKind kind, float difference, float reference, float scale, float gain, float floor)
    {
        float size = MathF.Abs(difference);
        switch (kind)
        {
            case DifferenceKind.Signed:
                return 0.5f + 0.5f * difference * scale;
            case DifferenceKind.Squared:
                return size * scale * (size * scale);
            case DifferenceKind.Relative:
                return size / MathF.Max(MathF.Abs(reference), floor) * gain;
            default:
                return size * scale;
        }
    }
}
