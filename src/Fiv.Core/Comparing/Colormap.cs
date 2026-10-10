namespace Fiv.Comparing;

/// <summary>The colours a single number per pixel (a difference, a similarity) is shown in.</summary>
public enum Colormap
{
    Gray,
    Viridis,
    Turbo,
    Jet,
    Thermal,
    Rainbow,

    // Diverging: the middle of the map is "no difference", the two ends are the two directions.
    Coolwarm,
    RedBlue,
    MagentaCyan,
}

/// <summary>
/// The colour maps as functions of a position 0..1 and as tables of <see cref="Size"/> entries.
/// The screen (through a texture) and the exported files (through <see cref="Lookup"/>) read the
/// same table in the same way, so a saved difference has the colours that were on screen.
/// </summary>
public static class Colormaps
{
    public const int Size = 256;
    public const int Count = (int)Colormap.MagentaCyan + 1;

    public static bool IsDiverging(this Colormap map) => map >= Colormap.Coolwarm;

    public static string DisplayName(this Colormap map) => map switch
    {
        Colormap.Gray => "Grey",
        Colormap.Viridis => "Viridis",
        Colormap.Turbo => "Turbo",
        Colormap.Jet => "Jet",
        Colormap.Thermal => "Thermal",
        Colormap.Rainbow => "Rainbow",
        Colormap.Coolwarm => "Cool-warm",
        Colormap.RedBlue => "Red-blue",
        _ => "Magenta-cyan",
    };

    /// <summary>The colour at a position of the map, each part 0..1.</summary>
    public static (float R, float G, float B) At(Colormap map, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        switch (map)
        {
            case Colormap.Viridis:
                // A sixth-degree fit to matplotlib's viridis (Matt Zucker's coefficients).
                return (
                    Clamp(Polynomial(t, 0.2777273272f, 0.1050930431f, -0.3308618287f, -4.6342304990f, 6.2282699363f, 4.7763849977f, -5.4354558559f)),
                    Clamp(Polynomial(t, 0.0054073445f, 1.4046135299f, 0.2148475595f, -5.7991009734f, 14.1799333668f, -13.7451453777f, 4.6458526122f)),
                    Clamp(Polynomial(t, 0.3340998053f, 1.3845901626f, 0.0950951630f, -19.3324409563f, 56.6905526007f, -65.3530326334f, 26.3124352496f)));
            case Colormap.Turbo:
                // Google's fifth-degree fit to its turbo map (Anton Mikhailov).
                return (
                    Clamp(Polynomial(t, 0.13572138f, 4.61539260f, -42.66032258f, 132.13108234f, -152.94239396f, 59.28637943f)),
                    Clamp(Polynomial(t, 0.09140261f, 2.19418839f, 4.84296658f, -14.18503333f, 4.27729857f, 2.82956604f)),
                    Clamp(Polynomial(t, 0.10667330f, 12.64194608f, -60.58204836f, 110.36276771f, -89.90310912f, 27.34824973f)));
            case Colormap.Jet:
                return (Clamp(1.5f - MathF.Abs(4 * t - 3)), Clamp(1.5f - MathF.Abs(4 * t - 2)), Clamp(1.5f - MathF.Abs(4 * t - 1)));
            case Colormap.Thermal:
                // Black, red, yellow, white: each part rises in its own third.
                return (Clamp(3 * t), Clamp(3 * t - 1), Clamp(3 * t - 2));
            case Colormap.Rainbow:
                return Hue((1 - t) * 270); // violet, blue, green, yellow, red
            case Colormap.Coolwarm:
                // Moreland's cool-warm map at its quarter points, straight lines between them.
                return Piecewise(t,
                    (0.2298f, 0.2987f, 0.7537f), (0.5543f, 0.6901f, 0.9955f), (0.8674f, 0.8644f, 0.8626f),
                    (0.9566f, 0.5980f, 0.4773f), (0.7057f, 0.0156f, 0.1502f));
            case Colormap.RedBlue:
                // Blue below, white in the middle, red above.
                return t < 0.5f ? (2 * t, 2 * t, 1f) : (1f, 2 - 2 * t, 2 - 2 * t);
            case Colormap.MagentaCyan:
                // Black in the middle, so that "no difference" stays dark: magenta below, cyan above.
                return t < 0.5f ? (1 - 2 * t, 0f, 1 - 2 * t) : (0f, 2 * t - 1, 2 * t - 1);
            default:
                return (t, t, t);
        }
    }

    static float Clamp(float value) => Math.Clamp(value, 0f, 1f);

    static float Polynomial(float t, params ReadOnlySpan<float> coefficients)
    {
        float value = 0;
        for (int i = coefficients.Length - 1; i >= 0; i--) value = value * t + coefficients[i];
        return value;
    }

    static (float, float, float) Piecewise(float t, params ReadOnlySpan<(float R, float G, float B)> points)
    {
        float position = t * (points.Length - 1);
        int index = Math.Min((int)position, points.Length - 2);
        float f = position - index;
        (float R, float G, float B) a = points[index], b = points[index + 1];
        return (a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f);
    }

    /// <summary>The fully saturated colour of a hue in degrees.</summary>
    static (float, float, float) Hue(float degrees)
    {
        float h = degrees / 60f;
        float x = 1 - MathF.Abs(h % 2 - 1);
        return (int)h switch
        {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        };
    }

    static readonly byte[]?[] s_tables = new byte[Count][];

    /// <summary>The map as <see cref="Size"/> entries of R, G, B, A bytes. The same array on every call; not to be changed.</summary>
    public static byte[] Table(Colormap map)
    {
        byte[]? table = Volatile.Read(ref s_tables[(int)map]);
        if (table != null) return table;
        table = new byte[Size * 4];
        for (int i = 0; i < Size; i++)
        {
            (float r, float g, float b) = At(map, i / (float)(Size - 1));
            table[i * 4] = (byte)(r * 255 + 0.5f);
            table[i * 4 + 1] = (byte)(g * 255 + 0.5f);
            table[i * 4 + 2] = (byte)(b * 255 + 0.5f);
            table[i * 4 + 3] = 255;
        }
        Volatile.Write(ref s_tables[(int)map], table);
        return table;
    }

    /// <summary>
    /// The colour at a position 0..1 of a table, each part 0..1: the two nearest entries blended,
    /// which is how the shader reads it.
    /// </summary>
    public static (float R, float G, float B) Lookup(byte[] table, float t)
    {
        float position = float.IsNaN(t) ? 0 : Math.Clamp(t, 0f, 1f) * (Size - 1);
        int index = (int)position, next = Math.Min(index + 1, Size - 1);
        float f = position - index;
        const float unit = 1f / 255;
        return (
            (table[index * 4] + (table[next * 4] - table[index * 4]) * f) * unit,
            (table[index * 4 + 1] + (table[next * 4 + 1] - table[index * 4 + 1]) * f) * unit,
            (table[index * 4 + 2] + (table[next * 4 + 2] - table[index * 4 + 2]) * f) * unit);
    }
}
