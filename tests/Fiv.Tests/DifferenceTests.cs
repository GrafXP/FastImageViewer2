using System.Numerics;
using System.Text.Json;
using Fiv.Comparing;
using Fiv.Imaging;
using Xunit;

namespace Fiv.Tests;

/// <summary>Small images with known pixels for the comparison tests.</summary>
static unsafe class Pixels
{
    /// <summary>A BGRA image whose pixel (x, y) is what <paramref name="color"/> says, as (R, G, B, A).</summary>
    public static DecodedImage Bgra(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        var image = new DecodedImage(width, height, PixelLayout.Bgra8, width, height);
        for (int y = 0; y < height; y++)
        {
            byte* row = image.Pixels + (long)y * image.Stride;
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = color(x, y);
                row[x * 4] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = r;
                row[x * 4 + 3] = a;
            }
        }
        return image;
    }

    public static DecodedImage Solid(int width, int height, byte r, byte g, byte b) => Bgra(width, height, (_, _) => (r, g, b, 255));

    public static DecodedImage Gray8(int width, int height, Func<int, int, byte> value)
    {
        var image = new DecodedImage(width, height, PixelLayout.Gray8, width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) image.Pixels[(long)y * image.Stride + x] = value(x, y);
        return image;
    }

    public static DecodedImage Gray16(int width, int height, Func<int, int, ushort> value)
    {
        var image = new DecodedImage(width, height, PixelLayout.Gray16, width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) ((ushort*)(image.Pixels + (long)y * image.Stride))[x] = value(x, y);
        return image;
    }

    public static DecodedImage Float(int width, int height, Func<int, int, float> value)
    {
        var image = new DecodedImage(width, height, PixelLayout.GrayFloat, width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) ((float*)(image.Pixels + (long)y * image.Stride))[x] = value(x, y);
        return image;
    }

    /// <summary>A grey pattern with structure in it: stripes, a slope and some fixed noise.</summary>
    public static byte Texture(int x, int y) => (byte)(100 + 40 * Math.Sin(x * 0.7) + 30 * Math.Cos(y * 0.45) + (x * 7 + y * 13) % 17);

    public static (byte B, byte G, byte R, byte A) At(DecodedImage image, int x, int y)
    {
        byte* p = image.Pixels + (long)y * image.Stride + x * 4;
        return (p[0], p[1], p[2], p[3]);
    }

    public static float FloatAt(DecodedImage image, int x, int y) => ((float*)(image.Pixels + (long)y * image.Stride))[x];
}

public class ColormapTests
{
    [Fact]
    public void GreyRunsFromBlackToWhite()
    {
        Assert.Equal((0f, 0f, 0f), Colormaps.At(Colormap.Gray, 0));
        Assert.Equal((1f, 1f, 1f), Colormaps.At(Colormap.Gray, 1));
        Assert.Equal((0.25f, 0.25f, 0.25f), Colormaps.At(Colormap.Gray, 0.25f));
    }

    [Theory]
    [InlineData(0f, 0.267f, 0.005f, 0.329f)]    // #440154
    [InlineData(0.5f, 0.128f, 0.567f, 0.551f)]  // #21918c
    [InlineData(1f, 0.993f, 0.906f, 0.144f)]    // #fde725
    public void ViridisPassesThroughItsKnownColours(float t, float r, float g, float b)
    {
        (float R, float G, float B) color = Colormaps.At(Colormap.Viridis, t);
        Assert.InRange(color.R, r - 0.03f, r + 0.03f);
        Assert.InRange(color.G, g - 0.03f, g + 0.03f);
        Assert.InRange(color.B, b - 0.03f, b + 0.03f);
    }

    [Fact]
    public void TurboIsDarkThenBlueThenGreenThenDarkRed()
    {
        (float r0, float g0, float b0) = Colormaps.At(Colormap.Turbo, 0);
        (float r1, float g1, float b1) = Colormaps.At(Colormap.Turbo, 0.1f);
        (float r2, float g2, float b2) = Colormaps.At(Colormap.Turbo, 0.5f);
        (float r3, float g3, float b3) = Colormaps.At(Colormap.Turbo, 1);
        Assert.True(Math.Max(r0, Math.Max(g0, b0)) < 0.25f, "starts dark"); // the fit's dark end is less blue than the map it follows
        Assert.True(b1 > 0.7f && b1 > r1 && b1 > g1, "then blue");
        Assert.True(g2 > 0.9f && g2 > r2 && g2 > b2, "green in the middle");
        Assert.True(r3 > 0.4f && g3 < 0.1f && b3 < 0.1f, "ends dark red");
    }

    [Theory]
    [InlineData(Colormap.Coolwarm)]
    [InlineData(Colormap.RedBlue)]
    [InlineData(Colormap.MagentaCyan)]
    public void DivergingMapsAreNeutralInTheMiddleAndFarApartAtTheEnds(Colormap map)
    {
        Assert.True(map.IsDiverging());
        (float r, float g, float b) = Colormaps.At(map, 0.5f);
        Assert.InRange(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)), 0, 0.02f); // a grey
        (float lowR, float lowG, float lowB) = Colormaps.At(map, 0);
        (float highR, float highG, float highB) = Colormaps.At(map, 1);
        Assert.True(Math.Abs(lowR - highR) + Math.Abs(lowG - highG) + Math.Abs(lowB - highB) > 0.9f, "the two directions have different colours");
        Assert.True(lowR - highR > 0.4f || highR - lowR > 0.4f, "and differ in red, so the direction can be told");
    }

    [Fact]
    public void SequentialMapsAreNotDiverging()
    {
        foreach (Colormap map in new[] { Colormap.Gray, Colormap.Viridis, Colormap.Turbo, Colormap.Jet, Colormap.Thermal, Colormap.Rainbow })
            Assert.False(map.IsDiverging());
    }

    [Fact]
    public void EveryMapStaysWithinRangeAndHasAName()
    {
        for (int m = 0; m < Colormaps.Count; m++)
        {
            var map = (Colormap)m;
            Assert.False(string.IsNullOrEmpty(map.DisplayName()));
            for (int i = 0; i <= 100; i++)
            {
                (float r, float g, float b) = Colormaps.At(map, i / 100f);
                Assert.InRange(r, 0f, 1f);
                Assert.InRange(g, 0f, 1f);
                Assert.InRange(b, 0f, 1f);
            }
        }
    }

    [Fact]
    public void ThermalGoesBlackRedYellowWhite()
    {
        Assert.Equal((0f, 0f, 0f), Colormaps.At(Colormap.Thermal, 0));
        Assert.Equal((1f, 0f, 0f), Colormaps.At(Colormap.Thermal, 1f / 3));
        (float r, float g, float b) = Colormaps.At(Colormap.Thermal, 2f / 3);
        Assert.Equal((1f, 1f), (r, MathF.Round(g, 4)));
        Assert.InRange(b, 0f, 0.001f);
        Assert.Equal((1f, 1f, 1f), Colormaps.At(Colormap.Thermal, 1));
    }

    [Fact]
    public void TheTableHoldsTheMapAndIsMadeOnce()
    {
        byte[] table = Colormaps.Table(Colormap.Gray);
        Assert.Equal(Colormaps.Size * 4, table.Length);
        Assert.Same(table, Colormaps.Table(Colormap.Gray));
        for (int i = 0; i < Colormaps.Size; i++) Assert.Equal(((byte)i, (byte)i, (byte)i, (byte)255), (table[i * 4], table[i * 4 + 1], table[i * 4 + 2], table[i * 4 + 3]));
    }

    [Fact]
    public void LookupBlendsNeighbouringEntriesAndClampsOutside()
    {
        byte[] table = Colormaps.Table(Colormap.Gray);
        Assert.Equal(0f, Colormaps.Lookup(table, -3).R);
        Assert.Equal(1f, Colormaps.Lookup(table, 7).R);
        Assert.Equal(0f, Colormaps.Lookup(table, float.NaN).R);
        Assert.Equal(100f / 255, Colormaps.Lookup(table, 100f / 255).R, 5);
        Assert.Equal(100.5f / 255, Colormaps.Lookup(table, 100.5f / 255).R, 5); // half-way between two entries
    }
}

public class DifferenceMathTests
{
    [Fact]
    public void ColourTakesTheChannelThatDiffersMostWithItsSign()
    {
        var first = new Vector4(10, 20, 30, 255);
        Assert.Equal(-9, DifferenceMath.Signed(first, new Vector4(12, 11, 33, 255), DifferenceMath.Colour, out float reference));
        Assert.Equal(20, reference);
        Assert.Equal(5, DifferenceMath.Signed(first, new Vector4(10, 20, 35, 255), DifferenceMath.Colour, out reference));
        Assert.Equal(30, reference);
        Assert.Equal(0, DifferenceMath.Signed(first, first, DifferenceMath.Colour, out _));
    }

    [Fact]
    public void ASingleChannelIsTakenOnItsOwn()
    {
        Vector4 first = new(10, 20, 30, 200), second = new(13, 16, 30, 255);
        Assert.Equal(3, DifferenceMath.Signed(first, second, DifferenceMath.Red, out float reference));
        Assert.Equal(10, reference);
        Assert.Equal(-4, DifferenceMath.Signed(first, second, DifferenceMath.Green, out _));
        Assert.Equal(0, DifferenceMath.Signed(first, second, DifferenceMath.Blue, out _));
        Assert.Equal(55, DifferenceMath.Signed(first, second, DifferenceMath.Alpha, out reference));
        Assert.Equal(200, reference);
    }

    [Fact]
    public void LumaWeighsGreenMost()
    {
        float luma = DifferenceMath.Signed(Vector4.Zero, new Vector4(100, 100, 100, 0), DifferenceMath.Luma, out _);
        Assert.Equal(100, luma, 3);
        Assert.Equal(71.52f, DifferenceMath.Signed(Vector4.Zero, new Vector4(0, 100, 0, 0), DifferenceMath.Luma, out _), 3);
        // Opposite changes in two channels can cancel in luma, which is why colour looks at each channel.
        Assert.InRange(MathF.Abs(DifferenceMath.Signed(Vector4.Zero, new Vector4(71.52f, -21.26f, 0, 0), DifferenceMath.Luma, out _)), 0, 0.001f);
    }

    [Fact]
    public void PositionsOnTheColourMap()
    {
        const float scale = 4f / 255; // gain 4 over an 8-bit range
        Assert.Equal(0, DifferenceMath.Position(DifferenceKind.Absolute, 0, 100, scale, 4, 1));
        Assert.Equal(1, DifferenceMath.Position(DifferenceKind.Absolute, -63.75f, 100, scale, 4, 1), 5);
        Assert.Equal(0.5f, DifferenceMath.Position(DifferenceKind.Signed, 0, 100, scale, 4, 1));
        Assert.Equal(0, DifferenceMath.Position(DifferenceKind.Signed, -63.75f, 100, scale, 4, 1), 5);
        Assert.Equal(1, DifferenceMath.Position(DifferenceKind.Signed, 63.75f, 100, scale, 4, 1), 5);
        Assert.Equal(0.25f, DifferenceMath.Position(DifferenceKind.Squared, 31.875f, 100, scale, 4, 1), 5); // half-way, squared
        Assert.Equal(0.4f, DifferenceMath.Position(DifferenceKind.Relative, 10, 100, scale, 4, 1), 5);      // 10 % of the value, times the gain
        Assert.Equal(40, DifferenceMath.Position(DifferenceKind.Relative, 10, 0, scale, 4, 1), 4);          // black: relative to the floor
    }
}

public class CompareSourceTests
{
    [Fact]
    public void AnImageAtTheOriginCoversItsOwnPixels()
    {
        using DecodedImage image = Pixels.Solid(7, 5, 1, 2, 3);
        var source = new CompareSource(image, Affine2.Identity);
        Assert.Equal(new CanvasRect(0, 0, 7, 5), source.Bounds);
        Assert.True(source.PixelAligned && source.IsColor);
        Assert.Equal((8, 255f), (source.Depth, source.FullValue));
    }

    [Fact]
    public unsafe void ACentredImageIsReadWhereItLies()
    {
        using DecodedImage image = Pixels.Bgra(4, 2, (x, y) => ((byte)(10 * x), (byte)(100 + y), 7, 255));
        var source = new CompareSource(image, CompareGeometry.Placement(SizeMatch.Centre, 4, 2, 10, 6));
        Assert.Equal(new CanvasRect(3, 2, 4, 2), source.Bounds);
        float* row = stackalloc float[16];
        source.ReadRow(3, 3, 4, row, row + 4, row + 8, row + 12, 1);
        Assert.Equal([0f, 10, 20, 30], new ReadOnlySpan<float>(row, 4).ToArray());
        Assert.Equal([101f, 101, 101, 101], new ReadOnlySpan<float>(row + 4, 4).ToArray());
        Assert.Equal(255, row[12]);
    }

    [Theory]
    [InlineData(Orientation.Rotate90)]
    [InlineData(Orientation.Rotate270)]
    [InlineData(Orientation.Rotate180)]
    [InlineData(Orientation.MirrorHorizontal)]
    [InlineData(Orientation.Transpose)]
    public unsafe void ATurnedImageIsReadUpright(Orientation orientation)
    {
        // Every stored pixel has its own value; the upright picture must hold the same values where the transform puts them.
        using DecodedImage image = Pixels.Gray8(5, 3, (x, y) => (byte)(y * 5 + x + 1));
        Affine2 toUpright = orientation.ToUpright(5, 3);
        var source = new CompareSource(image, toUpright);
        bool swap = orientation.SwapsAxes();
        Assert.Equal(new CanvasRect(0, 0, swap ? 3 : 5, swap ? 5 : 3), source.Bounds);
        Assert.True(source.PixelAligned);

        int width = source.Bounds.Width;
        float* row = stackalloc float[4 * width];
        for (int sy = 0; sy < 3; sy++)
        {
            for (int sx = 0; sx < 5; sx++)
            {
                (double ux, double uy) = toUpright.Apply(sx + 0.5, sy + 0.5);
                source.ReadRow((int)Math.Floor(uy), 0, width, row, row + width, row + 2 * width, row + 3 * width, 1);
                Assert.Equal(sy * 5 + sx + 1, row[(int)Math.Floor(ux)]);
            }
        }
    }

    [Fact]
    public unsafe void AScaledImageIsBlendedBetweenItsPixels()
    {
        // Two pixels, 0 and 100, stretched over four: the canvas pixels in between take values in between.
        using DecodedImage image = Pixels.Gray8(2, 1, (x, _) => (byte)(x * 100));
        var source = new CompareSource(image, Affine2.ScaleTranslate(2, 2, 0, 0));
        Assert.Equal(new CanvasRect(0, 0, 4, 2), source.Bounds);
        Assert.False(source.PixelAligned);
        float* row = stackalloc float[16];
        source.ReadRow(0, 0, 4, row, row + 4, row + 8, row + 12, 1);
        Assert.Equal([0f, 25, 75, 100], new ReadOnlySpan<float>(row, 4).ToArray());
    }

    [Fact]
    public void RectanglesIntersect()
    {
        Assert.Equal(new CanvasRect(5, 5, 5, 3), new CanvasRect(0, 0, 10, 8).Intersect(new CanvasRect(5, 5, 20, 20)));
        Assert.True(new CanvasRect(0, 0, 4, 4).Intersect(new CanvasRect(4, 0, 4, 4)).IsEmpty);
        Assert.Equal(0, new CanvasRect(0, 0, 4, 4).Intersect(new CanvasRect(9, 9, 4, 4)).Area);
    }
}

public class CompareMetricsTests
{
    static MetricsResult Measure(DecodedImage first, DecodedImage second, CanvasRect? region = null, bool ssim = false, bool map = false,
        Affine2? firstAt = null, Affine2? secondAt = null) =>
        CompareMetrics.Compute(new CompareSource(first, firstAt ?? Affine2.Identity), new CompareSource(second, secondAt ?? Affine2.Identity), region, ssim, map)!;

    [Fact]
    public void IdenticalImagesHaveNoError()
    {
        using DecodedImage image = TestImages.Gradient(64, 48);
        MetricsResult result = Measure(image, image, ssim: true);
        Assert.Equal(new CanvasRect(0, 0, 64, 48), result.Area);
        Assert.Equal((64L * 48, ValueUnit.Bits8, 255.0), (result.Pixels, result.Unit, result.Peak));
        Assert.Equal([ChannelMetrics.Red, ChannelMetrics.Green, ChannelMetrics.Blue, ChannelMetrics.Luma, ChannelMetrics.All], result.Channels.Select(c => c.Name));
        foreach (ChannelMetrics channel in result.Channels)
        {
            Assert.Equal((0.0, 0.0, 0.0), (channel.Mae, channel.Mse, channel.MaxError));
            Assert.True(double.IsPositiveInfinity(channel.Psnr));
            Assert.Equal(0, channel.Histogram.FractionBeyond(0));
            Assert.Equal(1, channel.Ssim, 5);
        }
    }

    [Fact]
    public void AConstantOffsetGivesTheTextbookNumbers()
    {
        using DecodedImage first = Pixels.Solid(40, 30, 100, 100, 100), second = Pixels.Solid(40, 30, 110, 100, 95);
        MetricsResult result = Measure(first, second);
        ChannelMetrics red = result.Find(ChannelMetrics.Red)!, green = result.Find(ChannelMetrics.Green)!, blue = result.Find(ChannelMetrics.Blue)!;
        Assert.Equal((10.0, 100.0, 10.0, 10.0), (red.Mae, red.Mse, red.MaxError, red.MeanDifference));
        Assert.Equal(20 * Math.Log10(255.0 / 10), red.Psnr, 9);
        Assert.Equal((0.0, 0.0), (green.Mae, green.MeanDifference));
        Assert.Equal((5.0, 25.0, -5.0), (blue.Mae, blue.Mse, blue.MeanDifference)); // the second image is darker in blue

        ChannelMetrics luma = result.Find(ChannelMetrics.Luma)!;
        Assert.Equal(10 * 0.2126 - 5 * 0.0722, luma.MeanDifference, 4);

        ChannelMetrics all = result.Main!;
        Assert.Equal(ChannelMetrics.All, all.Name);
        Assert.Equal((5.0, 125.0 / 3, 10.0), (all.Mae, all.Mse, all.MaxError));
        Assert.Equal(10 * Math.Log10(255.0 * 255 / (125.0 / 3)), all.Psnr, 9);
    }

    [Fact]
    public void TheHistogramCountsEveryDifferenceExactly()
    {
        // A quarter of the pixels differ by +3 in red, one pixel by -200 in green.
        using DecodedImage first = Pixels.Solid(8, 8, 50, 220, 50);
        using DecodedImage second = Pixels.Bgra(8, 8, (x, y) => x == 7 && y == 7 ? ((byte)50, (byte)20, (byte)50, (byte)255) : ((byte)(y < 2 ? 53 : 50), (byte)220, (byte)50, (byte)255));
        MetricsResult result = Measure(first, second);
        DifferenceHistogram red = result.Find(ChannelMetrics.Red)!.Histogram, all = result.Main!.Histogram;
        Assert.True(red.Exact);
        Assert.Equal((64L, 511), (red.Total, red.Counts.Length));
        Assert.Equal((-255.0, 1.0, 0.0), (red.Lowest, red.BinWidth, red.ValueAt(255)));
        Assert.Equal((48L, 16L), (red.Counts[255], red.Counts[258]));
        Assert.Equal(0.25, red.FractionBeyond(2));
        Assert.Equal(0, red.FractionBeyond(3)); // "more than", not "at least"
        // All: over the threshold in any channel.
        Assert.Equal(17.0 / 64, all.FractionBeyond(0));
        Assert.Equal(1.0 / 64, all.FractionBeyond(3));
        Assert.Equal(1, all.Counts[255 - 200]);
        Assert.Equal(200, result.Main!.MaxError);
    }

    [Fact]
    public void ARegionLimitsWhatIsMeasured()
    {
        using DecodedImage first = Pixels.Gray8(20, 20, (_, _) => 10);
        using DecodedImage second = Pixels.Gray8(20, 20, (x, y) => (byte)(x >= 10 && y >= 10 ? 14 : 10));
        MetricsResult whole = Measure(first, second), changed = Measure(first, second, new CanvasRect(10, 10, 100, 100)), same = Measure(first, second, new CanvasRect(0, 0, 10, 20));
        Assert.Equal([ChannelMetrics.Value], whole.Channels.Select(c => c.Name));
        Assert.Equal(1.0, whole.Main!.Mae);
        Assert.Equal(new CanvasRect(10, 10, 10, 10), changed.Area); // cut to the image
        Assert.Equal((100L, 4.0, 16.0), (changed.Pixels, changed.Main!.Mae, changed.Main.Mse));
        Assert.Equal((200L, 0.0), (same.Pixels, same.Main!.Mae));
    }

    [Fact]
    public void ARegionOutsideTheImagesMeasuresNothing()
    {
        using DecodedImage image = Pixels.Solid(10, 10, 1, 2, 3);
        MetricsResult result = Measure(image, image, new CanvasRect(50, 50, 5, 5));
        Assert.True(result.Area.IsEmpty);
        Assert.Empty(result.Channels);
        Assert.Null(result.Main);
    }

    [Fact]
    public void ImagesOfDifferentSizesAreComparedWhereBothHavePixels()
    {
        // A 4 x 4 image centred on an 8 x 8 one: only the middle is compared.
        using DecodedImage large = Pixels.Gray8(8, 8, (x, y) => (byte)(x >= 2 && x < 6 && y >= 2 && y < 6 ? 60 : 200));
        using DecodedImage small = Pixels.Gray8(4, 4, (_, _) => 61);
        MetricsResult centred = Measure(large, small, secondAt: CompareGeometry.Placement(SizeMatch.Centre, 4, 4, 8, 8));
        Assert.Equal(new CanvasRect(2, 2, 4, 4), centred.Area);
        Assert.Equal((16L, 1.0, 1.0), (centred.Pixels, centred.Main!.Mae, centred.Main.MaxError));
        Assert.True(centred.PixelAligned);

        MetricsResult topLeft = Measure(large, small);
        Assert.Equal(new CanvasRect(0, 0, 4, 4), topLeft.Area);
        Assert.Equal(139, topLeft.Main!.MaxError); // the corner of the large image is 200
    }

    [Fact]
    public void ScaledToFitTheSmallerImageIsResampled()
    {
        using DecodedImage large = Pixels.Gray8(8, 8, (_, _) => 90), small = Pixels.Gray8(4, 4, (_, _) => 100);
        MetricsResult result = Measure(large, small, secondAt: CompareGeometry.Placement(SizeMatch.ScaleToFit, 4, 4, 8, 8));
        Assert.Equal(new CanvasRect(0, 0, 8, 8), result.Area);
        Assert.False(result.PixelAligned);
        Assert.Equal(10, result.Main!.Mae, 4);
        Assert.False(result.Main.Histogram.Exact); // blended values are not whole numbers
        Assert.Equal(1, result.Main.Histogram.FractionBeyond(9), 6);
    }

    [Fact]
    public void SixteenBitValuesKeepTheirUnits()
    {
        using DecodedImage first = Pixels.Gray16(16, 16, (x, _) => (ushort)(1000 * x)), second = Pixels.Gray16(16, 16, (x, _) => (ushort)(1000 * x + (x == 3 ? 300 : 0)));
        MetricsResult result = Measure(first, second);
        Assert.Equal((ValueUnit.Bits16, 65535.0), (result.Unit, result.Peak));
        ChannelMetrics value = result.Main!;
        Assert.Equal((300.0 / 16, 300.0), (value.Mae, value.MaxError));
        Assert.Equal(10 * Math.Log10(65535.0 * 65535 / (300.0 * 300 / 16)), value.Psnr, 9);
        Assert.True(value.Histogram.Exact);
        Assert.Equal(1.0 / 16, value.Histogram.FractionBeyond(299));
        Assert.Equal(0, value.Histogram.FractionBeyond(300));
    }

    [Fact]
    public void FloatValuesAreMeasuredAsStoredAndThePeakIsTheirRange()
    {
        using DecodedImage first = Pixels.Float(16, 16, (x, _) => -2f + x * 0.5f), second = Pixels.Float(16, 16, (x, y) => -2f + x * 0.5f + (y == 0 ? 0.25f : 0));
        MetricsResult result = Measure(first, second);
        Assert.Equal(ValueUnit.Float, result.Unit);
        Assert.Equal(5.75 - -2, result.Peak, 5); // -2 ... 5.5 in the first image, up to 5.75 in the second
        ChannelMetrics value = result.Main!;
        Assert.Equal(0.25 / 16, value.Mae, 6);
        Assert.Equal(0.25, value.MaxError, 6);
        Assert.False(value.Histogram.Exact);
        Assert.Equal(1.0 / 16, value.Histogram.FractionBeyond(0.1), 6);
        Assert.Equal(0, value.Histogram.FractionBeyond(0.26), 6);
    }

    [Fact]
    public void SamplesThatAreNotNumbersAreLeftOut()
    {
        using DecodedImage first = Pixels.Float(8, 8, (_, _) => 1f), second = Pixels.Float(8, 8, (x, y) => x == 0 && y == 0 ? float.NaN : x == 1 && y == 0 ? float.PositiveInfinity : 1.5f);
        MetricsResult result = Measure(first, second);
        Assert.Equal((62L, 2L), (result.Pixels, result.Ignored));
        Assert.Equal(0.5, result.Main!.Mae, 6);
    }

    [Fact]
    public void ImagesOfDifferentDepthsMeetOnAScaleFromZeroToOne()
    {
        using DecodedImage eight = Pixels.Gray8(8, 8, (_, _) => 255), sixteen = Pixels.Gray16(8, 8, (_, _) => 32768);
        MetricsResult result = Measure(eight, sixteen);
        Assert.Equal((ValueUnit.Normalised, 1.0), (result.Unit, result.Peak));
        Assert.Equal(1 - 32768.0 / 65535, result.Main!.Mae, 6);
        Assert.Equal(-(1 - 32768.0 / 65535), result.Main.MeanDifference, 6);
    }

    [Fact]
    public void AGreyImageAgainstAColourOneIsComparedInEveryChannel()
    {
        using DecodedImage grey = Pixels.Gray8(8, 8, (_, _) => 100), colour = Pixels.Solid(8, 8, 100, 104, 100);
        MetricsResult result = Measure(grey, colour);
        Assert.Equal(ValueUnit.Bits8, result.Unit);
        Assert.Equal((0.0, 4.0, 0.0), (result.Find(ChannelMetrics.Red)!.Mae, result.Find(ChannelMetrics.Green)!.Mae, result.Find(ChannelMetrics.Blue)!.Mae));
        Assert.Null(result.Find(ChannelMetrics.Alpha)); // both are opaque
    }

    [Fact]
    public void AlphaIsListedWhenAnImageIsNotOpaque()
    {
        using DecodedImage first = Pixels.Solid(8, 8, 9, 9, 9), second = Pixels.Bgra(8, 8, (x, _) => (9, 9, 9, (byte)(x == 0 ? 55 : 255)));
        ChannelMetrics alpha = Measure(first, second).Find(ChannelMetrics.Alpha)!;
        Assert.Equal((200.0, 200.0 / 8), (alpha.MaxError, alpha.Mae));
        Assert.True(double.IsNaN(alpha.Ssim));
    }

    [Fact]
    public void CancellingReturnsNothing()
    {
        using DecodedImage image = TestImages.Gradient(64, 64);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Null(CompareMetrics.Compute(new CompareSource(image, Affine2.Identity), new CompareSource(image, Affine2.Identity), null, true, true, cancel.Token));
    }

    // ---- structural similarity

    [Fact]
    public void TwoFlatImagesHaveTheSimilarityOfTheirMeans()
    {
        // No structure at all: what is left of the formula is the luminance term.
        using DecodedImage first = Pixels.Gray8(32, 32, (_, _) => 100), second = Pixels.Gray8(32, 32, (_, _) => 140);
        double c1 = 0.01 * 255 * (0.01 * 255), expected = (2 * 100.0 * 140 + c1) / (100.0 * 100 + 140.0 * 140 + c1);
        Assert.InRange(Measure(first, second, ssim: true).Main!.Ssim, expected - 1e-9, expected + 1e-9);
    }

    [Fact]
    public void FlatSixteenBitAndFloatImagesAreAsAccurate()
    {
        using DecodedImage first16 = Pixels.Gray16(32, 32, (_, _) => 20000), second16 = Pixels.Gray16(32, 32, (_, _) => 50000);
        double c1 = 0.01 * 65535 * (0.01 * 65535), expected = (2 * 20000.0 * 50000 + c1) / (20000.0 * 20000 + 50000.0 * 50000 + c1);
        Assert.InRange(Measure(first16, second16, ssim: true).Main!.Ssim, expected - 1e-9, expected + 1e-9);

        // Float: the peak is the span of the values, 3 here. Values some 300 times their span are
        // the hard case for the sums: the result is still good to six decimal places.
        using DecodedImage firstFloat = Pixels.Float(32, 32, (_, _) => 1000f), secondFloat = Pixels.Float(32, 32, (_, _) => 1003f);
        c1 = 0.01 * 3 * (0.01 * 3);
        expected = (2 * 1000.0 * 1003 + c1) / (1000.0 * 1000 + 1003.0 * 1003 + c1);
        Assert.InRange(Measure(firstFloat, secondFloat, ssim: true).Main!.Ssim, expected - 1e-6, expected + 1e-6);
    }

    [Fact]
    public void SimilarityDropsWithNoiseAndIsTheSameBothWays()
    {
        using DecodedImage clean = Pixels.Gray8(64, 64, Pixels.Texture);
        using DecodedImage slight = Pixels.Gray8(64, 64, (x, y) => (byte)(Pixels.Texture(x, y) + (x * 31 + y * 17) % 5 - 2));
        using DecodedImage heavy = Pixels.Gray8(64, 64, (x, y) => (byte)(Pixels.Texture(x, y) + (x * 31 + y * 17) % 41 - 20));
        double a = Measure(clean, slight, ssim: true).Main!.Ssim, b = Measure(clean, heavy, ssim: true).Main!.Ssim;
        Assert.InRange(a, 0.9, 0.9999);
        Assert.InRange(b, 0.1, a - 0.05);
        Assert.Equal(b, Measure(heavy, clean, ssim: true).Main!.Ssim, 6);
    }

    [Fact]
    public unsafe void SimilarityMatchesAStraightforwardComputation()
    {
        // The banded, vectorised code against the definition, written out plainly for one image pair.
        const int size = 40;
        using DecodedImage first = Pixels.Gray8(size, size, Pixels.Texture);
        using DecodedImage second = Pixels.Gray8(size, size, (x, y) => (byte)(Pixels.Texture(x, y) * 0.8 + (x * 3 + y * 5) % 23));
        var kernel = new double[11];
        double norm = 0;
        for (int i = 0; i < 11; i++) norm += kernel[i] = Math.Exp(-(i - 5) * (i - 5) / 4.5);
        double c1 = Math.Pow(0.01 * 255, 2), c2 = Math.Pow(0.03 * 255, 2), total = 0;
        for (int y = 5; y < size - 5; y++)
        {
            for (int x = 5; x < size - 5; x++)
            {
                double ma = 0, mb = 0, aa = 0, bb = 0, ab = 0;
                for (int j = -5; j <= 5; j++)
                {
                    for (int i = -5; i <= 5; i++)
                    {
                        double w = kernel[i + 5] * kernel[j + 5] / (norm * norm);
                        double a = first.Pixels[(y + j) * first.Stride + x + i], b = second.Pixels[(y + j) * second.Stride + x + i];
                        ma += w * a;
                        mb += w * b;
                        aa += w * a * a;
                        bb += w * b * b;
                        ab += w * a * b;
                    }
                }
                total += (2 * ma * mb + c1) * (2 * (ab - ma * mb) + c2) / ((ma * ma + mb * mb + c1) * (aa - ma * ma + bb - mb * mb + c2));
            }
        }
        double expected = total / ((size - 10) * (size - 10));
        Assert.Equal(expected, Measure(first, second, ssim: true).Main!.Ssim, 9);
    }

    [Fact]
    public void ColourImagesGetASimilarityPerChannel()
    {
        // Only green carries a change.
        using DecodedImage first = Pixels.Bgra(48, 48, (x, y) => (Pixels.Texture(x, y), Pixels.Texture(y, x), Pixels.Texture(x + 9, y), 255));
        using DecodedImage second = Pixels.Bgra(48, 48, (x, y) => (Pixels.Texture(x, y), (byte)(Pixels.Texture(y, x) + (x * 31 + y * 17) % 41), Pixels.Texture(x + 9, y), 255));
        MetricsResult result = Measure(first, second, ssim: true);
        double red = result.Find(ChannelMetrics.Red)!.Ssim, green = result.Find(ChannelMetrics.Green)!.Ssim, blue = result.Find(ChannelMetrics.Blue)!.Ssim;
        Assert.Equal((1.0, 1.0), (Math.Round(red, 5), Math.Round(blue, 5)));
        Assert.InRange(green, 0.05, 0.95);
        Assert.Equal((red + green + blue) / 3, result.Main!.Ssim, 9);
        Assert.InRange(result.Find(ChannelMetrics.Luma)!.Ssim, green, 1); // luma is mostly, but not only, green
    }

    [Fact]
    public void AnAreaSmallerThanTheWindowHasNoSimilarity()
    {
        using DecodedImage image = Pixels.Gray8(10, 40, Pixels.Texture);
        MetricsResult result = Measure(image, image, ssim: true, map: true);
        Assert.True(double.IsNaN(result.Main!.Ssim));
        Assert.Null(result.SsimMap);
        Assert.Equal(0, result.Main.Mae); // the rest is still measured
    }

    [Fact]
    public void TheSimilarityMapShowsWhereTheChangeIs()
    {
        using DecodedImage first = Pixels.Gray8(96, 64, Pixels.Texture);
        using DecodedImage second = Pixels.Gray8(96, 64, (x, y) => x is >= 60 and < 76 && y is >= 20 and < 36 ? (byte)(255 - Pixels.Texture(x, y)) : Pixels.Texture(x, y));
        MetricsResult result = Measure(first, second, map: true);
        using DecodedImage map = result.SsimMap!;
        Assert.Equal((96, 64, PixelLayout.GrayFloat), (map.Width, map.Height, map.Layout));
        Assert.Equal(new CanvasRect(0, 0, 96, 64), result.SsimArea);
        Assert.Equal(1, Pixels.FloatAt(map, 20, 30), 5);          // far from the change
        Assert.InRange(Pixels.FloatAt(map, 68, 28), -1, 0.3);     // in the middle of it
        Assert.Equal(1, Pixels.FloatAt(map, 0, 0), 5);            // the border takes the nearest value
        Assert.Equal(Pixels.FloatAt(map, 5, 5), Pixels.FloatAt(map, 2, 3));
        Assert.Equal(Pixels.FloatAt(map, 90, 58), Pixels.FloatAt(map, 95, 63));
        // The mean of the map's inner part is the number reported.
        double sum = 0;
        for (int y = 5; y < 59; y++)
            for (int x = 5; x < 91; x++) sum += Pixels.FloatAt(map, x, y);
        Assert.Equal(sum / (86 * 54), result.Main!.Ssim, 5);
    }

    [Fact]
    public void WithARegionTheNumbersAreOfTheRegionAndTheMapOfEverything()
    {
        using DecodedImage first = Pixels.Gray8(96, 64, Pixels.Texture);
        using DecodedImage second = Pixels.Gray8(96, 64, (x, y) => x >= 48 ? (byte)(255 - Pixels.Texture(x, y)) : Pixels.Texture(x, y));
        MetricsResult result = Measure(first, second, new CanvasRect(0, 0, 40, 64), ssim: true, map: true);
        using DecodedImage map = result.SsimMap!;
        Assert.Equal(1, result.Main!.Ssim, 5);                       // the left part is unchanged
        Assert.Equal(new CanvasRect(0, 0, 96, 64), result.SsimArea);
        Assert.InRange(Pixels.FloatAt(map, 80, 30), -1, 0.3);        // the map still shows the right part
    }
}

public class CompareExportTests
{
    static CompareSource At(DecodedImage image, Affine2? placement = null) => new(image, placement ?? Affine2.Identity);

    static DifferenceView View(DifferenceKind kind = DifferenceKind.Absolute, Colormap map = Colormap.Gray, float gain = 1, bool mask = false, float threshold = 0) =>
        new() { Kind = kind, Map = map, Gain = gain, FullScale = 1, Channel = DifferenceMath.Colour, Mask = mask, Threshold = threshold };

    [Fact]
    public void TheAbsoluteDifferenceInGreyIsTheDifferenceItself()
    {
        using DecodedImage first = Pixels.Solid(4, 4, 100, 100, 100), second = Pixels.Bgra(4, 4, (x, _) => ((byte)(100 + 10 * x), 100, (byte)(100 - 5 * x), 255));
        using DecodedImage plain = CompareExport.Difference(At(first), At(second), 4, 4, View(), ToneSettings.Default);
        using DecodedImage gained = CompareExport.Difference(At(first), At(second), 4, 4, View(gain: 4), ToneSettings.Default);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), Pixels.At(plain, 0, 0));
        Assert.Equal(((byte)30, (byte)30, (byte)30, (byte)255), Pixels.At(plain, 3, 2)); // red differs most: 30
        Assert.Equal(((byte)80, (byte)80, (byte)80, (byte)255), Pixels.At(gained, 2, 1));
        Assert.Equal((byte)120, Pixels.At(gained, 3, 3).R);
    }

    [Fact]
    public void TheSignedDifferenceIsNeutralWhereNothingChangedAndColouredByDirection()
    {
        using DecodedImage first = Pixels.Gray8(3, 1, (_, _) => 100), second = Pixels.Gray8(3, 1, (x, _) => (byte)(x == 0 ? 40 : x == 1 ? 100 : 160));
        using DecodedImage image = CompareExport.Difference(At(first), At(second), 3, 1, View(DifferenceKind.Signed, Colormap.RedBlue, gain: 2), ToneSettings.Default);
        (byte b, byte g, byte r, _) = Pixels.At(image, 1, 0);
        Assert.InRange(Math.Abs(r - b) + Math.Abs(r - g), 0, 4);            // white-ish: no difference
        (byte lowB, _, byte lowR, _) = Pixels.At(image, 0, 0);
        (byte highB, _, byte highR, _) = Pixels.At(image, 2, 0);
        Assert.True(lowB > lowR + 50, "the second image is darker: blue");
        Assert.True(highR > highB + 50, "the second image is brighter: red");
    }

    [Fact]
    public void TheMaskMarksOnlyWhatIsOverTheThreshold()
    {
        using DecodedImage first = Pixels.Solid(4, 1, 200, 200, 200), second = Pixels.Bgra(4, 1, (x, _) => ((byte)(200 + x * 2), 200, 200, 255));
        using DecodedImage image = CompareExport.Difference(At(first), At(second), 4, 1, View(mask: true, threshold: 3f / 255), ToneSettings.Default);
        (byte b0, byte g0, byte r0, _) = Pixels.At(image, 1, 0);                 // differs by 2: under the threshold
        Assert.Equal((r0, r0), (g0, b0));                                        // the first image, grey
        Assert.Equal((byte)(200 * CompareExport.MaskDim + 0.5f), r0);
        (byte b1, byte g1, byte r1, _) = Pixels.At(image, 2, 0);                 // differs by 4
        Assert.Equal(((byte)(CompareExport.MaskColor.Z * 255 + 0.5f), (byte)(CompareExport.MaskColor.Y * 255 + 0.5f), (byte)255), (b1, g1, r1));
    }

    [Fact]
    public void WhereOnlyOneImageHasPixelsThereIsNoDifference()
    {
        using DecodedImage large = Pixels.Solid(6, 4, 10, 10, 10), small = Pixels.Solid(2, 2, 20, 20, 20);
        using DecodedImage image = CompareExport.Difference(At(large), At(small), 6, 4, View(), ToneSettings.Default);
        Assert.Equal((byte)255, Pixels.At(image, 1, 1).A);
        Assert.Equal((byte)10, Pixels.At(image, 1, 1).R);
        Assert.Equal((byte)0, Pixels.At(image, 2, 1).A);
        Assert.Equal((byte)0, Pixels.At(image, 5, 3).A);
    }

    [Fact]
    public void SplitBlendAndCheckerboardTakeTheirPixelsFromTheRightImage()
    {
        using DecodedImage first = Pixels.Solid(8, 8, 200, 0, 0), second = Pixels.Solid(8, 8, 0, 0, 100);
        using DecodedImage columns = CompareExport.Mix(At(first), At(second), 8, 8, MixExport.SplitColumns, 3, 0, 0, ToneSettings.Default);
        Assert.Equal(((byte)200, (byte)0), (Pixels.At(columns, 2, 5).R, Pixels.At(columns, 2, 5).B));
        Assert.Equal(((byte)0, (byte)100), (Pixels.At(columns, 3, 5).R, Pixels.At(columns, 3, 5).B));
        using DecodedImage rows = CompareExport.Mix(At(first), At(second), 8, 8, MixExport.SplitRows, 6, 0, 0, ToneSettings.Default);
        Assert.Equal(((byte)200, (byte)0), (Pixels.At(rows, 7, 5).R, Pixels.At(rows, 7, 6).R));
        using DecodedImage blend = CompareExport.Mix(At(first), At(second), 8, 8, MixExport.Blend, 0.25f, 0, 0, ToneSettings.Default);
        Assert.Equal(((byte)150, (byte)25), (Pixels.At(blend, 4, 4).R, Pixels.At(blend, 4, 4).B));
        using DecodedImage checker = CompareExport.Mix(At(first), At(second), 8, 8, MixExport.Checker, 2, 4, 4, ToneSettings.Default);
        Assert.Equal((byte)200, Pixels.At(checker, 4, 4).R);   // the cell at the origin shows the first image
        Assert.Equal((byte)200, Pixels.At(checker, 5, 5).R);
        Assert.Equal((byte)0, Pixels.At(checker, 6, 4).R);     // its neighbours the second
        Assert.Equal((byte)0, Pixels.At(checker, 3, 4).R);
        Assert.Equal((byte)200, Pixels.At(checker, 2, 2).R);
    }

    [Fact]
    public void TheBlendShowsTheFirstImageWhereTheSecondHasNoPixels()
    {
        using DecodedImage large = Pixels.Solid(4, 4, 80, 80, 80), small = Pixels.Solid(2, 2, 240, 240, 240);
        using DecodedImage blend = CompareExport.Mix(At(large), At(small), 4, 4, MixExport.Blend, 0.5f, 0, 0, ToneSettings.Default);
        Assert.Equal((byte)160, Pixels.At(blend, 1, 1).R);
        Assert.Equal((byte)80, Pixels.At(blend, 3, 3).R);
    }

    [Fact]
    public void ASplitOfSeveralImagesHasAStripForEach()
    {
        using DecodedImage first = Pixels.Solid(9, 2, 10, 0, 0), second = Pixels.Solid(9, 2, 20, 0, 0), third = Pixels.Solid(9, 2, 30, 0, 0);
        using DecodedImage columns = CompareExport.Mix([At(first), At(second), At(third)], 9, 2, MixExport.SplitColumns, [2, 6.5f], 0, 0, 0, ToneSettings.Default);
        Assert.Equal([10, 10, 20, 20, 20, 20, 30, 30, 30], Enumerable.Range(0, 9).Select(x => (int)Pixels.At(columns, x, 1).R));
        // Dividers on top of each other leave the image between them no strip.
        using DecodedImage shut = CompareExport.Mix([At(first), At(second), At(third)], 9, 2, MixExport.SplitColumns, [4, 4], 0, 0, 0, ToneSettings.Default);
        Assert.Equal((10, 30), (Pixels.At(shut, 3, 0).R, Pixels.At(shut, 4, 0).R));
    }

    [Fact]
    public void ABlendOfSeveralImagesLaysEachOverThoseBeforeIt()
    {
        using DecodedImage first = Pixels.Solid(4, 4, 30, 0, 0), second = Pixels.Solid(4, 4, 90, 0, 0), third = Pixels.Solid(2, 2, 240, 0, 0);
        // Opacities of 1/2 and 1/3: an equal mix of the three, (30 + 90 + 240) / 3.
        using DecodedImage blend = CompareExport.Mix([At(first), At(second), At(third)], 4, 4, MixExport.Blend, [1, 0.5f, 1 / 3f], 0, 0, 0, ToneSettings.Default);
        Assert.Equal((byte)120, Pixels.At(blend, 1, 1).R);
        Assert.Equal((byte)60, Pixels.At(blend, 3, 3).R); // where the third has no pixels, the first two as they are
    }

    [Fact]
    public void TheOverlayCountsEachImageByItsOpacityAndIsAsTransparentAsTheyAre()
    {
        using DecodedImage first = Pixels.Solid(4, 4, 30, 0, 0), second = Pixels.Solid(4, 4, 90, 0, 0), third = Pixels.Solid(2, 2, 240, 0, 0);
        CompareSource[] sources = [At(first), At(second), At(third)];
        using DecodedImage equal = CompareExport.Mix(sources, 4, 4, MixExport.Overlay, [1, 1, 1], 0, 0, 0, ToneSettings.Default);
        Assert.Equal(((byte)120, (byte)255), (Pixels.At(equal, 1, 1).R, Pixels.At(equal, 1, 1).A)); // (30 + 90 + 240) / 3, opaque
        Assert.Equal((byte)60, Pixels.At(equal, 3, 3).R);                                             // where the third has no pixels, the other two
        using DecodedImage weighted = CompareExport.Mix(sources, 4, 4, MixExport.Overlay, [0.5f, 0, 0.5f], 0, 0, 0, ToneSettings.Default);
        Assert.Equal(((byte)135, (byte)191), (Pixels.At(weighted, 1, 1).R, Pixels.At(weighted, 1, 1).A)); // half each; 1 - 0.5 * 0.5 covered
        Assert.Equal(((byte)30, (byte)128), (Pixels.At(weighted, 3, 3).R, Pixels.At(weighted, 3, 3).A));  // the first alone, half see-through
        using DecodedImage none = CompareExport.Mix(sources, 4, 4, MixExport.Overlay, [0, 0, 0], 0, 0, 0, ToneSettings.Default);
        Assert.Equal((byte)0, Pixels.At(none, 1, 1).A);
    }

    [Fact]
    public void ACheckerboardOfSeveralImagesGoesRoundThem()
    {
        using DecodedImage first = Pixels.Solid(6, 6, 10, 0, 0), second = Pixels.Solid(6, 6, 20, 0, 0), third = Pixels.Solid(6, 6, 30, 0, 0);
        using DecodedImage checker = CompareExport.Mix([At(first), At(second), At(third)], 6, 6, MixExport.Checker, [], 2, 0, 0, ToneSettings.Default);
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 6; x++)
            Assert.Equal(10 * (1 + CompareGeometry.CheckerLayer(x / 2, y / 2, 3)), Pixels.At(checker, x, y).R);
    }

    [Fact]
    public void TheMixOfTwoIsTheMixOfSeveralWithTwo()
    {
        using DecodedImage first = Pixels.Bgra(7, 5, (x, y) => ((byte)(x * 30), (byte)(y * 40), 7, 255)), second = Pixels.Solid(5, 4, 200, 100, 50);
        (MixExport, float, float[])[] cases = [(MixExport.SplitRows, 2.5f, [2.5f]), (MixExport.Blend, 0.3f, [1, 0.3f]), (MixExport.Checker, 2f, [])];
        foreach ((MixExport kind, float amount, float[] amounts) in cases)
        {
            using DecodedImage two = CompareExport.Mix(At(first), At(second), 7, 5, kind, amount, 1, 1, ToneSettings.Default);
            using DecodedImage several = CompareExport.Mix([At(first), At(second)], 7, 5, kind, amounts, amount, 1, 1, ToneSettings.Default);
            for (int y = 0; y < 5; y++)
            for (int x = 0; x < 7; x++)
                Assert.Equal(Pixels.At(two, x, y), Pixels.At(several, x, y));
        }
    }

    [Fact]
    public void TheDisplayControlsAreAppliedAsOnScreen()
    {
        var tone = new ToneSettings { Low = 0.25f, High = 0.75f, Gamma = 1 };
        Assert.Equal(0, CompareExport.Tone(0.2f, tone));
        Assert.Equal(0.5f, CompareExport.Tone(0.5f, tone), 5);
        Assert.Equal(1, CompareExport.Tone(0.9f, tone));
        Assert.Equal(1, CompareExport.Tone(0.5f, tone with { Exposure = 1 }), 5);          // one stop: twice as bright
        Assert.Equal(MathF.Sqrt(0.5f), CompareExport.Tone(0.5f, tone with { Gamma = 2 }), 5);

        var pixel = new Vector4(0.2f, 0.6f, 1f, 1f);
        Assert.Equal(new Vector3(0.6f), CompareExport.Shade(pixel, 0, 0, ToneSettings.Default with { Channel = DifferenceMath.Green }));
        Assert.Equal(new Vector3(1f), CompareExport.Shade(pixel, 0, 0, ToneSettings.Default with { Channel = DifferenceMath.Alpha }));
        // A transparent pixel shows the checkerboard that the screen shows.
        Vector3 clear = CompareExport.Shade(pixel with { W = 0 }, 0, 0, ToneSettings.Default), next = CompareExport.Shade(pixel with { W = 0 }, 8, 0, ToneSettings.Default);
        Assert.Equal(clear.X, clear.Y);
        Assert.NotEqual(clear.X, next.X);
    }

    [Fact]
    public void TheHeatMapColoursDissimilarity()
    {
        using DecodedImage map = Pixels.Float(2, 1, (x, _) => x == 0 ? 1f : 0.5f);
        using DecodedImage image = CompareExport.Heat(map, new CanvasRect(1, 1, 2, 1), 4, 3, Colormap.Gray, 1);
        Assert.Equal((byte)0, Pixels.At(image, 0, 0).A);              // outside the map
        Assert.Equal(((byte)0, (byte)255), (Pixels.At(image, 1, 1).R, Pixels.At(image, 1, 1).A)); // identical: the start of the map
        Assert.Equal((byte)128, Pixels.At(image, 2, 1).R);
        using DecodedImage gained = CompareExport.Heat(map, new CanvasRect(1, 1, 2, 1), 4, 3, Colormap.Gray, 2);
        Assert.Equal((byte)255, Pixels.At(gained, 2, 1).R);
    }

    [Fact]
    public void TheDifferenceValuesAreInTheUnitsOfTheFilesAndUndefinedOutside()
    {
        using DecodedImage first = Pixels.Gray16(4, 2, (_, _) => 1000), second = Pixels.Gray16(2, 2, (x, _) => (ushort)(x == 0 ? 400 : 1700));
        using DecodedImage values = CompareExport.DifferenceValues(At(first), At(second), 4, 2, DifferenceMath.Colour);
        Assert.Equal(PixelLayout.GrayFloat, values.Layout);
        Assert.Equal((-600f, 700f), (Pixels.FloatAt(values, 0, 0), Pixels.FloatAt(values, 1, 1)));
        Assert.True(float.IsNaN(Pixels.FloatAt(values, 2, 0)) && float.IsNaN(Pixels.FloatAt(values, 3, 1)));
    }
}

public unsafe class ImageWriterTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "fiv_writer_" + Guid.NewGuid().ToString("N"));

    public ImageWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    DecodedImage Read(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(_folder, name));
        fixed (byte* data = bytes) return WicDecoder.Decode(data, (nuint)bytes.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AColourPngReadsBackAsItWasWritten(bool alpha)
    {
        using DecodedImage image = Pixels.Bgra(37, 21, (x, y) => ((byte)(x * 7), (byte)(y * 11), (byte)(x * y), (byte)(alpha ? 255 - x * 5 : 255)));
        ImageWriter.WritePng(Path.Combine(_folder, "a.png"), image, alpha);
        using DecodedImage back = Read("a.png");
        Assert.Equal((37, 21, PixelLayout.Bgra8), (back.Width, back.Height, back.Layout));
        for (int y = 0; y < 21; y++)
            for (int x = 0; x < 37; x++) Assert.Equal(Pixels.At(image, x, y), Pixels.At(back, x, y));
        Assert.False(File.Exists(Path.Combine(_folder, "a.png.tmp")));
    }

    [Fact]
    public void ASixteenBitPngKeepsItsValues()
    {
        using DecodedImage image = Pixels.Gray16(19, 13, (x, y) => (ushort)(x * 3000 + y * 7));
        ImageWriter.WritePng(Path.Combine(_folder, "g.png"), image);
        using DecodedImage back = Read("g.png");
        Assert.Equal(PixelLayout.Gray16, back.Layout);
        Assert.True(new ReadOnlySpan<byte>(image.Pixels, (int)image.ByteCount).SequenceEqual(new ReadOnlySpan<byte>(back.Pixels, (int)back.ByteCount)));
    }

    [Fact]
    public void AFloatTiffKeepsItsValuesIncludingNegativeOnes()
    {
        using DecodedImage image = Pixels.Float(23, 9, (x, y) => (x - 10) * 0.37f + y * 1000f);
        ImageWriter.WriteTiffFloat(Path.Combine(_folder, "d.tif"), image);
        using DecodedImage back = Read("d.tif");
        Assert.Equal((23, 9, PixelLayout.GrayFloat), (back.Width, back.Height, back.Layout));
        Assert.True(new ReadOnlySpan<byte>(image.Pixels, (int)image.ByteCount).SequenceEqual(new ReadOnlySpan<byte>(back.Pixels, (int)back.ByteCount)));
    }

    [Fact]
    public void OtherLayoutsAreRefused()
    {
        using DecodedImage floats = Pixels.Float(2, 2, (_, _) => 0), bytes = Pixels.Gray8(2, 2, (_, _) => 0);
        Assert.Throws<ArgumentException>(() => ImageWriter.WritePng(Path.Combine(_folder, "x.png"), floats));
        Assert.Throws<ArgumentException>(() => ImageWriter.WriteTiffFloat(Path.Combine(_folder, "x.tif"), bytes));
    }
}

public class MetricsReportTests
{
    static MetricsResult Sample()
    {
        using DecodedImage first = Pixels.Solid(40, 30, 100, 100, 100), second = Pixels.Solid(40, 30, 110, 100, 95);
        return CompareMetrics.Compute(new CompareSource(first, Affine2.Identity), new CompareSource(second, Affine2.Identity), new CanvasRect(4, 2, 20, 20), true, false)!;
    }

    [Fact]
    public void CsvHasOneRowPerChannelUnderAHeader()
    {
        string[] lines = MetricsReport.Csv(Sample(), @"C:\a\one, two.jpg", "b.jpg", 4).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("first,second,x,y,width,height,pixels,unit,peak,threshold,channel,mae,", lines[0]);
        Assert.Equal(lines[0].Split(',').Length + 1, lines[1].Split(',').Length); // the quoted name holds a comma
        Assert.StartsWith("\"C:\\a\\one, two.jpg\",b.jpg,4,2,20,20,400,8-bit,255,4,red,10,100,10,", lines[1]);
        Assert.Contains(",100,", lines[1]);  // every pixel is over the threshold in red
        Assert.StartsWith("\"C:\\a\\one, two.jpg\",b.jpg,4,2,20,20,400,8-bit,255,4,all,5,", lines[5]);
    }

    [Fact]
    public void CsvWritesInfinityAndUndefinedNumbers()
    {
        using DecodedImage image = Pixels.Gray8(8, 8, (_, _) => 5);
        MetricsResult result = CompareMetrics.Compute(new CompareSource(image, Affine2.Identity), new CompareSource(image, Affine2.Identity), null, true, false)!;
        string row = MetricsReport.Csv(result, "a", "b", 0).Split("\r\n")[1];
        Assert.EndsWith(",value,0,0,0,inf,0,0,0,", row); // identical: infinite PSNR; 8 x 8 is too small for a similarity
    }

    [Fact]
    public void JsonCarriesTheSameNumbers()
    {
        using JsonDocument json = JsonDocument.Parse(MetricsReport.Json(Sample(), "a.jpg", "b.jpg", 4));
        JsonElement root = json.RootElement;
        Assert.Equal(("a.jpg", "b.jpg", "8-bit"), (root.GetProperty("first").GetString(), root.GetProperty("second").GetString(), root.GetProperty("unit").GetString()));
        Assert.Equal((20, 400, 255.0, 4.0), (root.GetProperty("area").GetProperty("width").GetInt32(), root.GetProperty("pixels").GetInt32(),
            root.GetProperty("peak").GetDouble(), root.GetProperty("threshold").GetDouble()));
        Assert.False(root.GetProperty("identical").GetBoolean());
        JsonElement red = root.GetProperty("channels").GetProperty("red"), green = root.GetProperty("channels").GetProperty("green");
        Assert.Equal((10.0, 100.0, 10.0, 100.0), (red.GetProperty("mae").GetDouble(), red.GetProperty("mse").GetDouble(), red.GetProperty("maxError").GetDouble(),
            red.GetProperty("overThresholdPercent").GetDouble()));
        Assert.Equal(JsonValueKind.Null, green.GetProperty("psnr").ValueKind); // no error in green: no finite PSNR
        Assert.InRange(red.GetProperty("ssim").GetDouble(), 0.9, 1);
    }

    [Fact]
    public void TheNumbersOfSeveralImagesAgainstOneAreOneTableOrAnArray()
    {
        MetricsResult result = Sample();
        MetricsEntry[] entries = [new(result, "ref.jpg", "b.jpg", 4), new(result, "ref.jpg", "c.png", 1000)];
        string[] lines = MetricsReport.Csv(entries).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 2 * 5, lines.Length); // one header, five channels each
        Assert.Single(lines, line => line.StartsWith("first,"));
        Assert.StartsWith("ref.jpg,c.png,4,2,20,20,400,8-bit,255,1000,red,", lines[6]);
        Assert.Equal(MetricsReport.Csv(result, "ref.jpg", "b.jpg", 4), MetricsReport.Csv(entries[..1]));

        using JsonDocument json = JsonDocument.Parse(MetricsReport.Json(entries));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(["b.jpg", "c.png"], json.RootElement.EnumerateArray().Select(entry => entry.GetProperty("second").GetString()));
        Assert.Equal(MetricsReport.Json(result, "ref.jpg", "b.jpg", 4), MetricsReport.Json(entries[..1])); // of one, the one object as before
    }
}

public class DifferenceSettingsTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "fiv_settings_" + Guid.NewGuid().ToString("N"));

    string File => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void TheDifferenceViewStartsPlain()
    {
        Settings settings = Settings.Load(File);
        Assert.Equal((DifferenceKind.Absolute, Colormap.Gray, Colormap.Coolwarm), (settings.DifferenceKind, settings.DifferenceMap, settings.SignedMap));
        Assert.Equal(2f / 255, settings.DifferenceThreshold);
        Assert.False(settings.DifferenceMask || settings.ShowMetrics || settings.PixelValues);
    }

    [Fact]
    public void ItsSettingsAreSavedAndLoaded()
    {
        new Settings
        {
            CompareMode = CompareMode.Ssim, DifferenceKind = DifferenceKind.Relative, DifferenceMap = Colormap.Turbo, SignedMap = Colormap.MagentaCyan,
            DifferenceThreshold = 0.125f, DifferenceMask = true, ShowMetrics = true, PixelValues = true,
        }.Save(File);
        Settings loaded = Settings.Load(File);
        Assert.Equal((CompareMode.Ssim, DifferenceKind.Relative, Colormap.Turbo, Colormap.MagentaCyan), (loaded.CompareMode, loaded.DifferenceKind, loaded.DifferenceMap, loaded.SignedMap));
        Assert.Equal((0.125f, true, true, true), (loaded.DifferenceThreshold, loaded.DifferenceMask, loaded.ShowMetrics, loaded.PixelValues));
    }

    [Fact]
    public void NonsenseFallsBackToTheDefaults()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "differenceKind": "Cubed", "differenceMap": "99", "signedMap": "", "differenceThreshold": 7 }""");
        Settings loaded = Settings.Load(File);
        Assert.Equal((DifferenceKind.Absolute, Colormap.Gray, Colormap.Coolwarm, 1f), (loaded.DifferenceKind, loaded.DifferenceMap, loaded.SignedMap, loaded.DifferenceThreshold));
    }
}
