using System.Numerics;
using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>The display controls as a file export needs them; the same values the shaders are given.</summary>
public struct ToneSettings
{
    /// <summary>Sample values shown as black and as white, in the units the GPU samples (0..1 for integer layouts).</summary>
    public float Low, High;

    public float Gamma;

    /// <summary>Brightness in stops.</summary>
    public float Exposure;

    /// <summary>0 colour; 1 red, 2 green, 3 blue, 5 luma, each as grey; 4 alpha as grey.</summary>
    public int Channel;

    public static ToneSettings Default => new() { Low = 0, High = 1, Gamma = 1 };
}

/// <summary>How a difference is turned into colours: what the difference view is set to.</summary>
public struct DifferenceView
{
    public DifferenceKind Kind;
    public Colormap Map;

    /// <summary>Gain 1 puts a difference of the whole value range at the end of the colour map; gain 8 an eighth of it.</summary>
    public float Gain;

    /// <summary>The value range, in the units the GPU samples: the span between the display's black and white.</summary>
    public float FullScale;

    /// <summary>See <see cref="DifferenceMath"/>.</summary>
    public int Channel;

    /// <summary>Instead of the colours: the first image dimmed, and every pixel that differs by more than <see cref="Threshold"/> marked.</summary>
    public bool Mask;

    /// <summary>In the units the GPU samples.</summary>
    public float Threshold;

    public readonly float Scale => Gain / MathF.Max(FullScale, 1e-12f);

    /// <summary>The smallest value of the first image a relative difference is taken of: one 255th of the range.</summary>
    public readonly float Floor => MathF.Max(FullScale, 1e-12f) / 255f;
}

/// <summary>How two images share one picture, besides their difference.</summary>
public enum MixExport
{
    SplitColumns,
    SplitRows,
    Blend,
    Checker,
}

/// <summary>
/// What the two-image views show, computed on the CPU at the full resolution of the canvas, for
/// saving to a file. The formulas are those of the shaders (display.hlsli, combine.hlsl); the
/// results are BGRA with alpha 0 where there is nothing to show.
/// </summary>
public static unsafe class CompareExport
{
    /// <summary>The colour that marks pixels over the threshold: one that photographs rarely contain.</summary>
    public static readonly Vector3 MaskColor = new(1f, 0.05f, 0.85f);

    /// <summary>How much of the first image's brightness is left under the mask.</summary>
    public const float MaskDim = 0.55f;

    const int CheckerCell = 8;
    const float CheckerDark = 0.36f, CheckerLight = 0.48f;

    delegate void RowWriter(int y, float* first, float* second, bool* hasFirst, bool* hasSecond, byte* output);

    /// <summary>The difference view over the whole canvas.</summary>
    public static DecodedImage Difference(CompareSource first, CompareSource second, int canvasWidth, int canvasHeight, DifferenceView view, ToneSettings tone)
    {
        byte[] table = Colormaps.Table(view.Map);
        float scale = view.Scale, floor = view.Floor;
        return Render(first, second, canvasWidth, canvasHeight, (y, a, b, hasA, hasB, output) =>
        {
            for (int x = 0; x < canvasWidth; x++, output += 4)
            {
                if (!hasA[x] || !hasB[x])
                {
                    *(uint*)output = 0;
                    continue;
                }
                Vector4 pa = Pixel(a, x, canvasWidth), pb = Pixel(b, x, canvasWidth);
                float d = DifferenceMath.Signed(pa, pb, view.Channel, out float reference);
                Vector3 color;
                if (view.Mask)
                {
                    Vector3 shown = Shade(pa, x, y, tone);
                    float grey = Vector3.Dot(shown, DifferenceMath.LumaWeights) * MaskDim;
                    color = MathF.Abs(d) > view.Threshold ? MaskColor : new Vector3(grey);
                }
                else
                {
                    (float r, float g, float bl) = Colormaps.Lookup(table, DifferenceMath.Position(view.Kind, d, reference, scale, view.Gain, floor));
                    color = new Vector3(r, g, bl);
                }
                Store(output, color, 255);
            }
        });
    }

    /// <summary>
    /// The split, blend or checkerboard view over the whole canvas. <paramref name="amount"/> is
    /// the divider's canvas position, the opacity of the second image, or the cell size in
    /// canvas pixels; the cells are centred on (<paramref name="originX"/>, <paramref name="originY"/>).
    /// </summary>
    public static DecodedImage Mix(CompareSource first, CompareSource second, int canvasWidth, int canvasHeight, MixExport kind,
        float amount, float originX, float originY, ToneSettings tone)
    {
        if (kind == MixExport.Checker) amount = MathF.Max(1, amount);
        return Render(first, second, canvasWidth, canvasHeight, (y, a, b, hasA, hasB, output) =>
        {
            for (int x = 0; x < canvasWidth; x++, output += 4)
            {
                bool useSecond;
                float opacity = 0;
                switch (kind)
                {
                    case MixExport.SplitColumns:
                        useSecond = x + 0.5f >= amount;
                        break;
                    case MixExport.SplitRows:
                        useSecond = y + 0.5f >= amount;
                        break;
                    case MixExport.Checker:
                        useSecond = ((long)MathF.Floor((x + 0.5f - originX) / amount) + (long)MathF.Floor((y + 0.5f - originY) / amount) & 1) != 0;
                        break;
                    default:
                        // The second image lies over the first: where it has no pixels, the first is seen in full.
                        useSecond = false;
                        opacity = hasB[x] ? hasA[x] ? amount : 1 : 0;
                        break;
                }
                if (kind != MixExport.Blend) opacity = useSecond ? 1 : 0;
                bool needA = opacity < 1, needB = opacity > 0;
                if ((needA && !hasA[x] && !needB) || (needB && !hasB[x] && !needA) || (!hasA[x] && !hasB[x]))
                {
                    *(uint*)output = 0;
                    continue;
                }
                Vector3 colorA = hasA[x] ? Shade(Pixel(a, x, canvasWidth), x, y, tone) : default;
                Vector3 colorB = hasB[x] ? Shade(Pixel(b, x, canvasWidth), x, y, tone) : default;
                Store(output, Vector3.Lerp(colorA, colorB, opacity), 255);
            }
        });
    }

    /// <summary>
    /// The structural-similarity view: 1 - similarity, times <paramref name="gain"/>, in the
    /// colours of <paramref name="colormap"/>. <paramref name="map"/> covers <paramref name="mapArea"/> of the canvas.
    /// </summary>
    public static DecodedImage Heat(DecodedImage map, CanvasRect mapArea, int canvasWidth, int canvasHeight, Colormap colormap, float gain)
    {
        byte[] table = Colormaps.Table(colormap);
        var result = new DecodedImage(canvasWidth, canvasHeight, PixelLayout.Bgra8, canvasWidth, canvasHeight);
        Parallel.For(0, canvasHeight, y =>
        {
            byte* output = result.Pixels + (long)y * result.Stride;
            bool inRows = y >= mapArea.Y && y < mapArea.Bottom;
            float* values = inRows ? (float*)(map.Pixels + (long)(y - mapArea.Y) * map.Stride) : null;
            for (int x = 0; x < canvasWidth; x++, output += 4)
            {
                if (!inRows || x < mapArea.X || x >= mapArea.Right)
                {
                    *(uint*)output = 0;
                    continue;
                }
                (float r, float g, float b) = Colormaps.Lookup(table, (1 - values[x - mapArea.X]) * gain);
                Store(output, new Vector3(r, g, b), 255);
            }
        });
        return result;
    }

    /// <summary>
    /// The difference itself, second less first, as 32-bit float in the units of
    /// <see cref="CompareMetrics.Units"/>, for a channel selection of <see cref="DifferenceMath"/>.
    /// Not a number where the two images do not both have pixels.
    /// </summary>
    public static DecodedImage DifferenceValues(CompareSource first, CompareSource second, int canvasWidth, int canvasHeight, int channel)
    {
        (_, float scaleFirst, float scaleSecond) = CompareMetrics.Units(first.Image.Layout, second.Image.Layout);
        var result = new DecodedImage(canvasWidth, canvasHeight, PixelLayout.GrayFloat, canvasWidth, canvasHeight);
        Parallel.For(0, canvasHeight, () => new float[canvasWidth * 8], (y, state, rows) =>
        {
            float* output = (float*)(result.Pixels + (long)y * result.Stride);
            new Span<float>(output, canvasWidth).Fill(float.NaN);
            if (!CompareMetrics.CommonSpan(first, second, y, new CanvasRect(0, y, canvasWidth, 1), out int left, out int n)) return rows;
            fixed (float* r = rows)
            {
                first.ReadRow(y, left, n, r, r + n, r + 2 * n, r + 3 * n, scaleFirst);
                second.ReadRow(y, left, n, r + 4 * n, r + 5 * n, r + 6 * n, r + 7 * n, scaleSecond);
                for (int i = 0; i < n; i++)
                    output[left + i] = DifferenceMath.Signed(Pixel(r, i, n), Pixel(r + 4 * n, i, n), channel, out _);
            }
            return rows;
        }, _ => { });
        return result;
    }

    /// <summary>
    /// An image as it lies on the canvas, cut to <paramref name="area"/>: in its own layout and
    /// units, each pixel what <see cref="CompareSource.ReadRow"/> gives there (one stored pixel
    /// where the image is pixel-aligned, four blended where it is not). This is the aligned
    /// image for saving: laid over the image it was aligned to, it is what the numbers were
    /// taken of. Where the image has no pixel the result is transparent black for colour, 0 for
    /// grey and not a number for float; <paramref name="complete"/> says whether there is no such place.
    /// </summary>
    public static DecodedImage Resampled(CompareSource source, CanvasRect area, out bool complete)
    {
        PixelLayout layout = source.Image.Layout;
        int width = area.Width, height = area.Height;
        var result = new DecodedImage(width, height, layout, width, height);
        long covered = 0;
        Parallel.For(0, height, () => new float[width * 4], (row, _, planes) =>
        {
            int y = area.Y + row;
            byte* output = result.Pixels + (long)row * result.Stride;
            if (layout == PixelLayout.GrayFloat) new Span<float>(output, width).Fill(float.NaN);
            else new Span<byte>(output, result.Stride).Clear();
            source.RowSpan(y, out int left, out int right);
            left = Math.Max(left, area.X);
            int n = Math.Min(right, area.Right) - left;
            if (n <= 0) return planes;
            Interlocked.Add(ref covered, n);
            fixed (float* p = planes)
            {
                float* r = p, g = p + width, b = p + 2 * width, a = p + 3 * width;
                source.ReadRow(y, left, n, r, g, b, a, 1f);
                int at = left - area.X;
                switch (layout)
                {
                    case PixelLayout.Bgra8:
                        for (int i = 0; i < n; i++)
                        {
                            byte* q = output + (at + i) * 4;
                            q[0] = Whole8(b[i]);
                            q[1] = Whole8(g[i]);
                            q[2] = Whole8(r[i]);
                            q[3] = Whole8(a[i]);
                        }
                        break;
                    case PixelLayout.Gray8:
                        for (int i = 0; i < n; i++) output[at + i] = Whole8(r[i]);
                        break;
                    case PixelLayout.Gray16:
                        for (int i = 0; i < n; i++) ((ushort*)output)[at + i] = Whole16(r[i]);
                        break;
                    case PixelLayout.GrayFloat:
                        for (int i = 0; i < n; i++) ((float*)output)[at + i] = r[i];
                        break;
                    case PixelLayout.Rgba16:
                        for (int i = 0; i < n; i++)
                        {
                            ushort* q = (ushort*)output + (at + i) * 4;
                            q[0] = Whole16(r[i]);
                            q[1] = Whole16(g[i]);
                            q[2] = Whole16(b[i]);
                            q[3] = Whole16(a[i]);
                        }
                        break;
                }
            }
            return planes;
        }, _ => { });
        complete = covered == (long)width * height;
        return result;

        static byte Whole8(float value) => (byte)(Math.Clamp(value, 0f, 255f) + 0.5f);
        static ushort Whole16(float value) => (ushort)(Math.Clamp(value, 0f, 65535f) + 0.5f);
    }

    /// <summary>Row by row over the canvas: both images in the units the GPU samples, and which pixels each has.</summary>
    static DecodedImage Render(CompareSource first, CompareSource second, int width, int height, RowWriter write)
    {
        var result = new DecodedImage(width, height, PixelLayout.Bgra8, width, height);
        float scaleFirst = 1f / first.FullValue, scaleSecond = 1f / second.FullValue;
        Parallel.For(0, height, () => (Values: new float[width * 8], Has: new bool[width * 2]), (y, _, buffers) =>
        {
            fixed (float* values = buffers.Values)
            fixed (bool* has = buffers.Has)
            {
                Read(first, y, width, values, has, scaleFirst);
                Read(second, y, width, values + 4 * width, has + width, scaleSecond);
                write(y, values, values + 4 * width, has, has + width, result.Pixels + (long)y * result.Stride);
            }
            return buffers;
        }, _ => { });
        return result;
    }

    /// <summary>One canvas row of an image into four planes of <paramref name="width"/> floats, with a flag per pixel for "the image has a pixel here".</summary>
    static void Read(CompareSource source, int y, int width, float* planes, bool* has, float scale)
    {
        new Span<bool>(has, width).Clear();
        source.RowSpan(y, out int left, out int right);
        left = Math.Max(left, 0);
        int count = Math.Min(right, width) - left;
        if (count <= 0) return;
        source.ReadRow(y, left, count, planes + left, planes + width + left, planes + 2 * width + left, planes + 3 * width + left, scale);
        new Span<bool>(has + left, count).Fill(true);
    }

    static Vector4 Pixel(float* planes, int x, int width) => new(planes[x], planes[width + x], planes[2 * width + x], planes[3 * width + x]);

    static void Store(byte* output, Vector3 color, byte alpha)
    {
        output[0] = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255 + 0.5f);
        output[1] = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255 + 0.5f);
        output[2] = (byte)(Math.Clamp(color.X, 0f, 1f) * 255 + 0.5f);
        output[3] = alpha;
    }

    /// <summary>Window, exposure and gamma: a stored value to screen brightness 0..1. As Tone in display.hlsli.</summary>
    public static float Tone(float value, in ToneSettings tone)
    {
        float span = tone.High - tone.Low;
        float invSpan = 1f / (MathF.Abs(span) > 1e-12f ? span : 1e-12f);
        value = Math.Clamp((value - tone.Low) * invSpan * MathF.Pow(2, tone.Exposure), 0f, 1f);
        float invGamma = 1f / MathF.Max(tone.Gamma, 1e-3f);
        return invGamma == 1 ? value : MathF.Pow(value, invGamma);
    }

    /// <summary>A stored pixel (R, G, B, A, each in the units the GPU samples) to the colour on screen. As Shade in display.hlsli.</summary>
    public static Vector3 Shade(Vector4 color, int x, int y, in ToneSettings tone)
    {
        if (tone.Channel == DifferenceMath.Alpha) return new Vector3(color.W);
        Vector3 rgb = tone.Channel switch
        {
            DifferenceMath.Red => new Vector3(color.X),
            DifferenceMath.Green => new Vector3(color.Y),
            DifferenceMath.Blue => new Vector3(color.Z),
            DifferenceMath.Luma => new Vector3(DifferenceMath.LumaOf(color)),
            _ => new Vector3(color.X, color.Y, color.Z),
        };
        var toned = new Vector3(Tone(rgb.X, tone), Tone(rgb.Y, tone), Tone(rgb.Z, tone));
        if (color.W >= 1) return toned;
        float pattern = ((x / CheckerCell + y / CheckerCell) & 1) == 0 ? CheckerDark : CheckerLight;
        return Vector3.Lerp(new Vector3(pattern), toned, Math.Clamp(color.W, 0f, 1f));
    }
}
