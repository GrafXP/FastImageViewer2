using System.Collections.Concurrent;
using System.Numerics;

namespace Fiv.Comparing;

/// <summary>
/// The discrete Fourier transform of a width x height array of complex numbers, real and
/// imaginary parts in two arrays of floats, in place. Both sizes are powers of two. Along the
/// rows it is the textbook radix-2 transform; down the columns the same transform is run with
/// whole rows as its elements, so that every step is a loop over a row that the compiler's
/// vector types do eight elements at a time.
///
/// Used by the registration: 256 x 256 in 1 ms and 512 x 512 in 5 ms on one core (`fivbench align`).
/// </summary>
public sealed unsafe class Fft2D
{
    public int Width { get; }
    public int Height { get; }

    // e^(-2 pi i k / n) for k < n / 2: the cosine, and the sine without its sign.
    readonly float[] _cosX, _sinX, _cosY, _sinY;
    readonly int[] _reverseX, _reverseY;

    static readonly ConcurrentDictionary<(int Width, int Height), Fft2D> Plans = new();

    /// <summary>The transform for a size; its tables are made once and shared.</summary>
    public static Fft2D For(int width, int height) => Plans.GetOrAdd((width, height), size => new Fft2D(size.Width, size.Height));

    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>The largest power of two that is not above <paramref name="n"/>; 0 for n below 1.</summary>
    public static int PowerOfTwoBelow(int n) => n < 1 ? 0 : 1 << (31 - int.LeadingZeroCount(n));

    Fft2D(int width, int height)
    {
        if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height)) throw new ArgumentException("The sizes of a Fourier transform have to be powers of two.");
        Width = width;
        Height = height;
        (_cosX, _sinX, _reverseX) = Tables(width);
        (_cosY, _sinY, _reverseY) = width == height ? (_cosX, _sinX, _reverseX) : Tables(height);
    }

    static (float[] Cos, float[] Sin, int[] Reverse) Tables(int n)
    {
        var cos = new float[Math.Max(1, n / 2)];
        var sin = new float[Math.Max(1, n / 2)];
        for (int k = 0; k < n / 2; k++)
        {
            double angle = 2 * Math.PI * k / n;
            cos[k] = (float)Math.Cos(angle);
            sin[k] = (float)Math.Sin(angle);
        }
        var reverse = new int[n];
        int bits = int.TrailingZeroCount(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            reverse[i] = r;
        }
        return (cos, sin, reverse);
    }

    /// <summary>
    /// X[k] = sum over n of x[n] e^(-2 pi i k n / N), in both directions, not scaled. With
    /// <paramref name="parallel"/> the rows and the columns are shared out over all cores.
    /// </summary>
    public void Forward(float* re, float* im, bool parallel = false)
    {
        int width = Width, height = Height;
        if (!parallel || (long)width * height < 128 * 128)
        {
            for (int y = 0; y < height; y++) Row(re + (long)y * width, im + (long)y * width);
            Columns(re, im, 0, width);
            return;
        }
        Parallel.For(0, height, y => Row(re + (long)y * width, im + (long)y * width));
        // Columns in strips, each a multiple of the widest vector wide.
        int strips = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, width / 32));
        int strip = (width / strips + 31) / 32 * 32;
        Parallel.For(0, (width + strip - 1) / strip, s => Columns(re, im, s * strip, Math.Min(width, (s + 1) * strip)));
    }

    /// <summary>The transform back, scaled so that Inverse(Forward(x)) is x.</summary>
    public void Inverse(float* re, float* im, bool parallel = false)
    {
        // The inverse is the conjugate of the forward transform of the conjugate, over the number of elements.
        long count = (long)Width * Height;
        float scale = 1f / count;
        Scale(im, count, -1);
        Forward(re, im, parallel);
        Scale(re, count, scale);
        Scale(im, count, -scale);
    }

    static void Scale(float* values, long count, float factor)
    {
        long i = 0;
        int step = Vector<float>.Count;
        for (; i <= count - step; i += step) (Vector.Load(values + i) * factor).Store(values + i);
        for (; i < count; i++) values[i] *= factor;
    }

    /// <summary>One row, in place.</summary>
    void Row(float* re, float* im)
    {
        int n = Width;
        int[] reverse = _reverseX;
        for (int i = 0; i < n; i++)
        {
            int j = reverse[i];
            if (j <= i) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }
        fixed (float* cos = _cosX, sin = _sinX)
        {
            for (int length = 2; length <= n; length <<= 1)
            {
                int half = length >> 1, step = n / length;
                for (int start = 0; start < n; start += length)
                {
                    float* ar = re + start, ai = im + start, br = ar + half, bi = ai + half;
                    for (int j = 0, k = 0; j < half; j++, k += step)
                    {
                        float wr = cos[k], wi = -sin[k];
                        float tr = br[j] * wr - bi[j] * wi, ti = br[j] * wi + bi[j] * wr;
                        br[j] = ar[j] - tr;
                        bi[j] = ai[j] - ti;
                        ar[j] += tr;
                        ai[j] += ti;
                    }
                }
            }
        }
    }

    /// <summary>The columns from <paramref name="x0"/> up to <paramref name="x1"/>, in place: a transform over the row index whose elements are pieces of rows.</summary>
    void Columns(float* re, float* im, int x0, int x1)
    {
        int n = Height, width = Width, count = x1 - x0;
        if (count <= 0) return;
        int[] reverse = _reverseY;
        for (int i = 0; i < n; i++)
        {
            int j = reverse[i];
            if (j <= i) continue;
            Swap(re + (long)i * width + x0, re + (long)j * width + x0, count);
            Swap(im + (long)i * width + x0, im + (long)j * width + x0, count);
        }
        float[] cos = _cosY, sin = _sinY;
        int lanes = Vector<float>.Count;
        for (int length = 2; length <= n; length <<= 1)
        {
            int half = length >> 1, step = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int j = 0; j < half; j++)
                {
                    float wr = cos[j * step], wi = -sin[j * step];
                    float* ar = re + (long)(start + j) * width + x0, ai = im + (long)(start + j) * width + x0;
                    float* br = ar + (long)half * width, bi = ai + (long)half * width;
                    int x = 0;
                    for (; x <= count - lanes; x += lanes)
                    {
                        Vector<float> vbr = Vector.Load(br + x), vbi = Vector.Load(bi + x), var = Vector.Load(ar + x), vai = Vector.Load(ai + x);
                        Vector<float> tr = vbr * wr - vbi * wi, ti = vbr * wi + vbi * wr;
                        (var - tr).Store(br + x);
                        (vai - ti).Store(bi + x);
                        (var + tr).Store(ar + x);
                        (vai + ti).Store(ai + x);
                    }
                    for (; x < count; x++)
                    {
                        float tr = br[x] * wr - bi[x] * wi, ti = br[x] * wi + bi[x] * wr;
                        br[x] = ar[x] - tr;
                        bi[x] = ai[x] - ti;
                        ar[x] += tr;
                        ai[x] += ti;
                    }
                }
            }
        }
    }

    static void Swap(float* a, float* b, int count)
    {
        int x = 0, lanes = Vector<float>.Count;
        for (; x <= count - lanes; x += lanes)
        {
            Vector<float> va = Vector.Load(a + x), vb = Vector.Load(b + x);
            vb.Store(a + x);
            va.Store(b + x);
        }
        for (; x < count; x++) (a[x], b[x]) = (b[x], a[x]);
    }
}
