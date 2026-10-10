using System.Numerics;
using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>
/// Structural similarity (Wang, Bovik, Sheikh, Simoncelli 2004) as its authors compute it: an
/// 11 x 11 Gaussian window of sigma 1.5, K1 = 0.01, K2 = 0.03, and the mean over every position
/// the whole window fits in.
///
/// Worked in tiles on all cores, so that a 24 MP pair needs a megabyte or two of scratch memory
/// per core and not a set of float copies of both images. The sums are double precision:
/// variance and covariance are small differences of large sums, and in 32-bit float a flat area
/// near black or white came out wrong in the fourth decimal place.
/// </summary>
internal static unsafe class Ssim
{
    public const int Window = 11, Radius = Window / 2;
    const int TileRows = 48, TileColumns = 240;
    static readonly double[] Kernel = MakeKernel();

    static double[] MakeKernel()
    {
        var kernel = new double[Window];
        double sum = 0;
        for (int i = 0; i < Window; i++) sum += kernel[i] = Math.Exp(-(i - Radius) * (i - Radius) / (2 * 1.5 * 1.5));
        for (int i = 0; i < Window; i++) kernel[i] /= sum;
        return kernel;
    }

    /// <summary>The window has to fit: an area smaller than 11 pixels either way has no structural similarity.</summary>
    public static bool Fits(CanvasRect area) => area.Width >= Window && area.Height >= Window;

    sealed class Work(int planes)
    {
        const int InputRows = TileRows + 2 * Radius, InputColumns = TileColumns + 2 * Radius;
        public readonly float[] Read = new float[InputColumns * 8];
        public readonly double[] First = new double[planes * InputRows * InputColumns];
        public readonly double[] Second = new double[planes * InputRows * InputColumns];
        public readonly double[] Filtered = new double[5 * InputRows * TileColumns];
        public readonly double[] Product = new double[InputColumns];
        public readonly double[] Column = new double[5 * TileColumns];
        public readonly double[] Totals = new double[planes];
    }

    /// <summary>
    /// The mean structural similarity of each plane over <paramref name="area"/>: red, green,
    /// blue and luma for colour, the one value for grey. With <paramref name="map"/> (a float
    /// image of the area's size) the similarity at every pixel of the last plane is written
    /// into it, the border the window cannot reach taking the nearest value;
    /// <paramref name="mapOnly"/> then leaves the other planes out. Null if the area is too
    /// small or the work was cancelled.
    /// </summary>
    public static double[]? Compute(CompareSource first, CompareSource second, CanvasRect area, float scaleFirst, float scaleSecond, bool colour,
        double peak, DecodedImage? map, CancellationToken cancel, bool mapOnly = false)
    {
        if (!Fits(area)) return null;
        int outWidth = area.Width - 2 * Radius, outHeight = area.Height - 2 * Radius;
        int planes = colour ? 4 : 1, mapPlane = planes - 1;
        double c1 = 0.01 * peak * (0.01 * peak), c2 = 0.03 * peak * (0.03 * peak);
        double wr = DifferenceMath.LumaWeights.X, wg = DifferenceMath.LumaWeights.Y, wb = DifferenceMath.LumaWeights.Z;
        var totals = new double[planes];
        int tileRows = (outHeight + TileRows - 1) / TileRows, tileColumns = (outWidth + TileColumns - 1) / TileColumns;

        Parallel.For(0, tileRows * tileColumns, () => new Work(planes), (tile, state, work) =>
        {
            if (cancel.IsCancellationRequested)
            {
                state.Stop();
                return work;
            }
            // The tile's output pixels, counted from the first position the window fits at, and the input they need.
            int firstRow = tile / tileColumns * TileRows, firstColumn = tile % tileColumns * TileColumns;
            int rows = Math.Min(TileRows, outHeight - firstRow), columns = Math.Min(TileColumns, outWidth - firstColumn);
            int inputRows = rows + 2 * Radius, inputColumns = columns + 2 * Radius, planeSize = inputRows * inputColumns;
            fixed (float* read = work.Read)
            fixed (double* planesFirst = work.First, planesSecond = work.Second, filtered = work.Filtered, product = work.Product, column = work.Column)
            {
                for (int j = 0; j < inputRows; j++)
                {
                    int y = area.Y + firstRow + j, x = area.X + firstColumn, n = inputColumns;
                    first.ReadRow(y, x, n, read, read + n, read + 2 * n, read + 3 * n, scaleFirst);
                    second.ReadRow(y, x, n, read + 4 * n, read + 5 * n, read + 6 * n, read + 7 * n, scaleSecond);
                    for (int side = 0; side < 2; side++)
                    {
                        float* r = read + side * 4 * n, g = r + n, b = r + 2 * n;
                        double* target = (side == 0 ? planesFirst : planesSecond) + j * n;
                        if (!colour)
                        {
                            for (int i = 0; i < n; i++) target[i] = r[i];
                            continue;
                        }
                        double* red = target, green = target + planeSize, blue = target + 2 * planeSize, luma = target + 3 * planeSize;
                        for (int i = 0; i < n; i++)
                        {
                            red[i] = r[i];
                            green[i] = g[i];
                            blue[i] = b[i];
                            luma[i] = r[i] * wr + g[i] * wg + b[i] * wb;
                        }
                    }
                }

                for (int p = mapOnly ? mapPlane : 0; p < planes; p++)
                {
                    double* a = planesFirst + p * planeSize, b = planesSecond + p * planeSize;
                    // Along the rows: the local means of a, b, a*a, b*b and a*b.
                    for (int j = 0; j < inputRows; j++)
                    {
                        double* rowA = a + j * inputColumns, rowB = b + j * inputColumns;
                        FilterRow(rowA, filtered + (0 * inputRows + j) * columns, columns);
                        FilterRow(rowB, filtered + (1 * inputRows + j) * columns, columns);
                        Multiply(rowA, rowA, product, inputColumns);
                        FilterRow(product, filtered + (2 * inputRows + j) * columns, columns);
                        Multiply(rowB, rowB, product, inputColumns);
                        FilterRow(product, filtered + (3 * inputRows + j) * columns, columns);
                        Multiply(rowA, rowB, product, inputColumns);
                        FilterRow(product, filtered + (4 * inputRows + j) * columns, columns);
                    }
                    // Down the columns, then the similarity of each pixel.
                    double sum = 0;
                    for (int o = 0; o < rows; o++)
                    {
                        for (int q = 0; q < 5; q++) FilterColumn(filtered + (q * inputRows + o) * columns, columns, column + q * columns);
                        float* mapRow = map != null && p == mapPlane
                            ? (float*)(map.Pixels + (long)(firstRow + o + Radius) * map.Stride) + firstColumn + Radius
                            : null;
                        sum += SimilarityRow(column, columns, c1, c2, mapRow);
                    }
                    work.Totals[p] += sum;
                }
            }
            return work;
        }, work =>
        {
            lock (totals)
                for (int p = 0; p < planes; p++) totals[p] += work.Totals[p];
        });
        if (cancel.IsCancellationRequested) return null;
        if (map != null) FillBorder(map);
        double count = (double)outWidth * outHeight;
        for (int p = 0; p < planes; p++) totals[p] /= count;
        return totals;
    }

    /// <summary>output[x] = the kernel over input[x .. x + 10].</summary>
    static void FilterRow(double* input, double* output, int count)
    {
        fixed (double* kernel = Kernel)
        {
            int x = 0, step = Vector<double>.Count;
            for (; x <= count - step; x += step)
            {
                Vector<double> sum = Vector<double>.Zero;
                for (int i = 0; i < Window; i++) sum += Vector.Load(input + x + i) * kernel[i];
                sum.Store(output + x);
            }
            for (; x < count; x++)
            {
                double sum = 0;
                for (int i = 0; i < Window; i++) sum += input[x + i] * kernel[i];
                output[x] = sum;
            }
        }
    }

    /// <summary>output[x] = the kernel over the eleven rows of <paramref name="width"/> values starting at <paramref name="rows"/>.</summary>
    static void FilterColumn(double* rows, int width, double* output)
    {
        fixed (double* kernel = Kernel)
        {
            int x = 0, step = Vector<double>.Count;
            for (; x <= width - step; x += step)
            {
                Vector<double> sum = Vector<double>.Zero;
                for (int i = 0; i < Window; i++) sum += Vector.Load(rows + i * width + x) * kernel[i];
                sum.Store(output + x);
            }
            for (; x < width; x++)
            {
                double sum = 0;
                for (int i = 0; i < Window; i++) sum += rows[i * width + x] * kernel[i];
                output[x] = sum;
            }
        }
    }

    static void Multiply(double* a, double* b, double* product, int count)
    {
        int x = 0, step = Vector<double>.Count;
        for (; x <= count - step; x += step) (Vector.Load(a + x) * Vector.Load(b + x)).Store(product + x);
        for (; x < count; x++) product[x] = a[x] * b[x];
    }

    /// <summary>
    /// The similarity of one row of pixels from the five local means (of a, b, a*a, b*b, a*b,
    /// each <paramref name="count"/> long, one after the other). Returns their sum.
    /// </summary>
    static double SimilarityRow(double* means, int count, double c1, double c2, float* map)
    {
        double* meanA = means, meanB = means + count, meanAA = means + 2 * count, meanBB = means + 3 * count, meanAB = means + 4 * count;
        int x = 0, step = Vector<double>.Count;
        Vector<double> total = Vector<double>.Zero, vc1 = new(c1), vc2 = new(c2), two = new(2.0);
        double* values = stackalloc double[step];
        for (; x <= count - step; x += step)
        {
            Vector<double> a = Vector.Load(meanA + x), b = Vector.Load(meanB + x);
            Vector<double> aa = a * a, bb = b * b, ab = a * b;
            Vector<double> varianceA = Vector.Load(meanAA + x) - aa, varianceB = Vector.Load(meanBB + x) - bb, covariance = Vector.Load(meanAB + x) - ab;
            Vector<double> similarity = (two * ab + vc1) * (two * covariance + vc2) / ((aa + bb + vc1) * (varianceA + varianceB + vc2));
            total += similarity;
            if (map != null)
            {
                similarity.Store(values);
                for (int i = 0; i < step; i++) map[x + i] = (float)values[i];
            }
        }
        double sum = Vector.Sum(total);
        for (; x < count; x++)
        {
            double a = meanA[x], b = meanB[x];
            double aa = a * a, bb = b * b, ab = a * b;
            double similarity = (2 * ab + c1) * (2 * (meanAB[x] - ab) + c2) / ((aa + bb + c1) * (meanAA[x] - aa + (meanBB[x] - bb) + c2));
            if (map != null) map[x] = (float)similarity;
            sum += similarity;
        }
        return sum;
    }

    /// <summary>The five pixels at each edge that no window is centred on take the nearest value there is.</summary>
    static void FillBorder(DecodedImage map)
    {
        int width = map.Width, height = map.Height;
        for (int y = Radius; y < height - Radius; y++)
        {
            float* row = (float*)(map.Pixels + (long)y * map.Stride);
            for (int x = 0; x < Radius; x++) row[x] = row[Radius];
            for (int x = width - Radius; x < width; x++) row[x] = row[width - Radius - 1];
        }
        long bytes = (long)width * sizeof(float);
        byte* top = map.Pixels + (long)Radius * map.Stride, bottom = map.Pixels + (long)(height - Radius - 1) * map.Stride;
        for (int y = 0; y < Radius; y++) Buffer.MemoryCopy(top, map.Pixels + (long)y * map.Stride, bytes, bytes);
        for (int y = height - Radius; y < height; y++) Buffer.MemoryCopy(bottom, map.Pixels + (long)y * map.Stride, bytes, bytes);
    }
}
