using System.Buffers;

namespace Fiv.Comparing;

/// <summary>
/// How far one picture is shifted against another, from the phases of their Fourier transforms.
/// The product of one spectrum with the conjugate of the other, each term divided by its size,
/// keeps only the phase differences; transformed back it is a single peak at the shift. Here the
/// terms are also weighted by a Gaussian over frequency, which leaves out the finest detail
/// (noise, compression, what resampling does) and turns the peak into a Gaussian. The logarithms
/// of three samples of a Gaussian lie on a parabola, so its position between the samples is
/// found exactly, to a few hundredths of a pixel on real images.
/// </summary>
public static unsafe class PhaseCorrelation
{
    /// <summary>
    /// The shift found: the second picture is the first one moved by (<see cref="Dx"/>, <see cref="Dy"/>)
    /// samples, b(p) = a(p - d). <see cref="Height"/> is the peak's height: 1 for two pictures
    /// that differ by nothing but the shift, near 0 for two that have nothing in common.
    /// </summary>
    public readonly record struct Peak(double Dx, double Dy, double Height);

    /// <summary>The width of the Gaussian weight as a fraction of the size: frequencies above about a quarter of the sampling rate count for little.</summary>
    const double WeightSigma = 1.0 / 8;

    /// <summary>A spectrum: real and imaginary parts, borrowed from the array pool.</summary>
    public sealed class Spectrum : IDisposable
    {
        public float[] Re { get; private set; }
        public float[] Im { get; private set; }
        public int Width { get; }
        public int Height { get; }

        internal Spectrum(int width, int height)
        {
            Width = width;
            Height = height;
            Re = ArrayPool<float>.Shared.Rent(width * height);
            Im = ArrayPool<float>.Shared.Rent(width * height);
        }

        public void Dispose()
        {
            if (Re.Length == 0) return;
            ArrayPool<float>.Shared.Return(Re);
            ArrayPool<float>.Shared.Return(Im);
            Re = Im = [];
        }
    }

    /// <summary>The Fourier transform of a real picture of <paramref name="width"/> by <paramref name="height"/> samples, both powers of two.</summary>
    public static Spectrum Transform(float* picture, int width, int height, bool parallel = false)
    {
        var spectrum = new Spectrum(width, height);
        int count = width * height;
        new ReadOnlySpan<float>(picture, count).CopyTo(spectrum.Re);
        spectrum.Im.AsSpan(0, count).Clear();
        fixed (float* re = spectrum.Re, im = spectrum.Im) Fft2D.For(width, height).Forward(re, im, parallel);
        return spectrum;
    }

    /// <summary>The shift of <paramref name="b"/> against <paramref name="a"/>, two real pictures of the same size, both sizes powers of two.</summary>
    public static Peak Shift(float* a, float* b, int width, int height, bool parallel = false)
    {
        using Spectrum sa = Transform(a, width, height, parallel), sb = Transform(b, width, height, parallel);
        return Shift(sa, sb, parallel);
    }

    /// <summary>The shift of the picture with spectrum <paramref name="b"/> against the one with spectrum <paramref name="a"/>. Neither spectrum is changed.</summary>
    public static Peak Shift(Spectrum a, Spectrum b, bool parallel = false)
    {
        int width = a.Width, height = a.Height, count = width * height;
        if (b.Width != width || b.Height != height) throw new ArgumentException("The two spectra have to have the same size.");
        float[] crossRe = ArrayPool<float>.Shared.Rent(count), crossIm = ArrayPool<float>.Shared.Rent(count);
        try
        {
            // conj(A) B, and how large its terms are on average: a term much smaller than that
            // says nothing about the shift, and is not blown up to the size of the others.
            double total = 0;
            fixed (float* ar = a.Re, ai = a.Im, br = b.Re, bi = b.Im, cr = crossRe, ci = crossIm)
            {
                for (int i = 0; i < count; i++)
                {
                    float re = ar[i] * br[i] + ai[i] * bi[i], im = ar[i] * bi[i] - ai[i] * br[i];
                    cr[i] = re;
                    ci[i] = im;
                    total += MathF.Sqrt(re * re + im * im);
                }
                if (!(total > 0) || !double.IsFinite(total)) return default; // one of the pictures is flat
                float floor = (float)(total / count * 0.01);

                Span<float> weightX = width <= 1024 ? stackalloc float[width] : new float[width];
                Span<float> weightY = height <= 1024 ? stackalloc float[height] : new float[height];
                double sumX = Weights(weightX), sumY = Weights(weightY);
                for (int y = 0; y < height; y++)
                {
                    float wy = weightY[y];
                    float* rowRe = cr + y * width, rowIm = ci + y * width;
                    for (int x = 0; x < width; x++)
                    {
                        float re = rowRe[x], im = rowIm[x];
                        float w = wy * weightX[x] / (MathF.Sqrt(re * re + im * im) + floor);
                        rowRe[x] = re * w;
                        rowIm[x] = im * w;
                    }
                }
                cr[0] = ci[0] = 0; // no constant part: the pictures' mean levels are not what is compared

                Fft2D.For(width, height).Inverse(cr, ci, parallel);

                int best = 0;
                float highest = float.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    if (cr[i] > highest)
                    {
                        highest = cr[i];
                        best = i;
                    }
                }
                int px = best % width, py = best / width;
                float left = cr[py * width + (px + width - 1) % width], right = cr[py * width + (px + 1) % width];
                float up = cr[(py + height - 1) % height * width + px], down = cr[(py + 1) % height * width + px];
                double dx = px + Between(left, highest, right), dy = py + Between(up, highest, down);
                if (dx > width / 2.0) dx -= width;
                if (dy > height / 2.0) dy -= height;
                // The height two pictures alike but for the shift would give: the sum of the weights over the number of samples.
                double ideal = sumX * sumY / count;
                return new Peak(dx, dy, highest / ideal);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(crossRe);
            ArrayPool<float>.Shared.Return(crossIm);
        }
    }

    /// <summary>The Gaussian weight of each frequency along one direction; returns their sum.</summary>
    static double Weights(Span<float> weights)
    {
        int n = weights.Length;
        double sigma = Math.Max(1, n * WeightSigma), sum = 0;
        for (int k = 0; k < n; k++)
        {
            int frequency = k <= n / 2 ? k : k - n;
            weights[k] = (float)Math.Exp(-0.5 * frequency * frequency / (sigma * sigma));
            sum += weights[k];
        }
        return sum;
    }

    /// <summary>
    /// Where between three neighbouring samples the top of the peak lies, -0.5 .. 0.5 from the
    /// middle one, which is the largest: the vertex of the parabola through their logarithms,
    /// or through the samples themselves where one of them is not positive.
    /// </summary>
    static double Between(double before, double top, double after)
    {
        if (before > 0 && top > 0 && after > 0)
        {
            before = Math.Log(before);
            top = Math.Log(top);
            after = Math.Log(after);
        }
        double curvature = before - 2 * top + after;
        if (!(curvature < 0)) return 0;
        return Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5);
    }
}
