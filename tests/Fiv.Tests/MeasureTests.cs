using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Measuring;
using Xunit;

namespace Fiv.Tests;

public class MeasureShapeTests
{
    const double Grid = 1000;

    [Fact]
    public void ALineHasLengthAndDirection()
    {
        MeasureShape line = MeasureShape.Line(10.5, 20.5, 13.5, 16.5);
        Assert.Equal((3, -4), (line.Dx, line.Dy));
        Assert.Equal(5, line.Length, 12);
        Assert.Equal(53.130102, line.Angle, 5); // up and to the right: anticlockwise from pointing right
        Assert.Equal(0, line.Area);
        Assert.False(line.HasArea);
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(0, -10, 90)]   // up on screen
    [InlineData(-10, 0, 180)]
    [InlineData(0, 10, -90)]   // down on screen
    [InlineData(10, 10, -45)]
    [InlineData(0, 0, 0)]
    public void TheAngleIsAsItLooksOnScreen(double dx, double dy, double degrees)
    {
        Assert.Equal(degrees, MeasureShape.Line(5, 5, 5 + dx, 5 + dy).Angle, 9);
    }

    [Fact]
    public void ARectangleHoldsWholePixelsWhicheverWayItWasDrawn()
    {
        MeasureShape rectangle = MeasureShape.Rectangle(10.4, 7.6, 2.2, 3.7);
        Assert.Equal(new MeasureShape(MeasureKind.Rectangle, 2, 4, 10, 8), rectangle);
        Assert.Equal((8, 4, 32, 24), (rectangle.Width, rectangle.Height, rectangle.Area, rectangle.Perimeter));
        Assert.Equal(new CanvasRect(2, 4, 8, 4), rectangle.PixelBounds);
        Assert.True(rectangle.HoldsPixel(2, 4) && rectangle.HoldsPixel(9, 7));
        Assert.False(rectangle.HoldsPixel(10, 7) || rectangle.HoldsPixel(1, 4) || rectangle.HoldsPixel(2, 8));
        rectangle.RowSpan(5, out int left, out int right);
        Assert.Equal((2, 10), (left, right));
        rectangle.RowSpan(8, out left, out right);
        Assert.Equal(left, right);
    }

    [Fact]
    public void ACircleHasRadiusAreaAndCircumference()
    {
        MeasureShape circle = MeasureShape.Circle(50, 40, 10);
        Assert.Equal((10, 20, 20, 20), (circle.Radius, circle.Diameter, circle.Width, circle.Height));
        Assert.Equal(Math.PI * 100, circle.Area, 9);
        Assert.Equal(Math.PI * 20, circle.Perimeter, 9);
        Assert.Equal(new CanvasRect(40, 30, 20, 20), circle.PixelBounds);
        Assert.Equal(0, MeasureShape.Circle(5, 5, -3).Radius); // no such thing as a negative radius
    }

    [Theory]
    [InlineData(50, 40, 10)]
    [InlineData(50.5, 40.5, 10)]
    [InlineData(17.3, 22.8, 6.45)]
    [InlineData(30, 30, 0.4)]   // smaller than a pixel, between four of them: holds none
    [InlineData(30.5, 30.5, 0.4)]
    [InlineData(12, 9, 25)]     // reaches beyond the top left of the grid
    public void TheRowsOfACircleAreThePixelsWhoseCentresAreInside(double centreX, double centreY, double radius)
    {
        MeasureShape circle = MeasureShape.Circle(centreX, centreY, radius);
        CanvasRect bounds = circle.PixelBounds;
        long inside = 0;
        for (int y = bounds.Y - 2; y < bounds.Bottom + 2; y++)
        {
            circle.RowSpan(y, out int left, out int right);
            for (int x = bounds.X - 2; x < bounds.Right + 2; x++)
            {
                double dx = x + 0.5 - centreX, dy = y + 0.5 - centreY;
                bool expected = dx * dx + dy * dy <= circle.Radius * circle.Radius;
                Assert.Equal(expected, x >= left && x < right);
                Assert.Equal(expected, circle.HoldsPixel(x, y));
                if (expected) inside++;
            }
        }
        if (radius >= 6) Assert.InRange(inside, circle.Area * 0.94, circle.Area * 1.06);
        if (radius < 0.5 && centreX == Math.Floor(centreX)) Assert.Equal(0, inside);
    }

    [Fact]
    public void ALineIsTakenHoldOfAtItsEndsOrAnywhereBetween()
    {
        MeasureShape line = MeasureShape.Line(10, 10, 110, 10);
        Assert.Equal(MeasureHandle.Start, line.HitTest(11, 12, 4));
        Assert.Equal(MeasureHandle.End, line.HitTest(108, 9, 4));
        Assert.Equal(MeasureHandle.Body, line.HitTest(60, 13, 4));
        Assert.Equal(MeasureHandle.None, line.HitTest(60, 15, 4));
        Assert.Equal(MeasureHandle.None, line.HitTest(120, 10, 4)); // past its end
        // Of a line shorter than the reach, the nearer end.
        MeasureShape tiny = MeasureShape.Line(10, 10, 12, 10);
        Assert.Equal(MeasureHandle.Start, tiny.HitTest(10.5, 10, 4));
        Assert.Equal(MeasureHandle.End, tiny.HitTest(11.5, 10, 4));
    }

    [Fact]
    public void ARectangleIsTakenHoldOfAtItsOutlineNotInside()
    {
        MeasureShape rectangle = MeasureShape.Rectangle(100, 100, 300, 200);
        Assert.Equal(MeasureHandle.TopLeft, rectangle.HitTest(101, 99, 5));
        Assert.Equal(MeasureHandle.TopRight, rectangle.HitTest(299, 102, 5));
        Assert.Equal(MeasureHandle.BottomRight, rectangle.HitTest(300, 200, 5));
        Assert.Equal(MeasureHandle.BottomLeft, rectangle.HitTest(98, 203, 5));
        Assert.Equal(MeasureHandle.Top, rectangle.HitTest(200, 101, 5));
        Assert.Equal(MeasureHandle.Right, rectangle.HitTest(302, 150, 5));
        Assert.Equal(MeasureHandle.Bottom, rectangle.HitTest(198, 200, 5));
        Assert.Equal(MeasureHandle.Left, rectangle.HitTest(100, 152, 5));
        Assert.Equal(MeasureHandle.Body, rectangle.HitTest(150, 103, 5));  // on the outline, away from the handles
        Assert.Equal(MeasureHandle.None, rectangle.HitTest(150, 150, 5));  // inside: the view's
        Assert.Equal(MeasureHandle.None, rectangle.HitTest(320, 150, 5));
    }

    [Fact]
    public void ACircleIsTakenHoldOfAtItsOutlineAndItsCentre()
    {
        MeasureShape circle = MeasureShape.Circle(200, 200, 50);
        Assert.Equal(MeasureHandle.Radius, circle.HitTest(250, 201, 4));
        Assert.Equal(MeasureHandle.Radius, circle.HitTest(200 + 36, 200 - 36, 4)); // 50.9 from the centre
        Assert.Equal(MeasureHandle.Body, circle.HitTest(202, 199, 4));
        Assert.Equal(MeasureHandle.None, circle.HitTest(225, 200, 4));
        Assert.Equal(MeasureHandle.None, circle.HitTest(260, 200, 4));
        // A circle smaller than the reach can still be moved by its centre.
        Assert.Equal(MeasureHandle.Body, MeasureShape.Circle(20, 20, 2).HitTest(20, 20, 4));
        Assert.Equal(MeasureHandle.Radius, MeasureShape.Circle(20, 20, 2).HitTest(22, 20, 4));
    }

    [Fact]
    public void ADragDrawsALineARectangleOrACircle()
    {
        Assert.Equal(MeasureShape.Line(10.5, 20.5, 40.5, 60.5), MeasureShape.Drawn(MeasureKind.Line, 10.5, 20.5, 40.5, 60.5, false, Grid, Grid));
        Assert.Equal(MeasureShape.Rectangle(10, 20, 40, 60), MeasureShape.Drawn(MeasureKind.Rectangle, 40.2, 59.8, 10.4, 20.3, false, Grid, Grid));
        MeasureShape circle = MeasureShape.Drawn(MeasureKind.Circle, 100, 100, 103, 104, false, Grid, Grid);
        Assert.Equal((100, 100, 5), (circle.X0, circle.Y0, circle.Radius));
    }

    [Theory]
    [InlineData(50, 8, 50, 0)]      // nearly level: level
    [InlineData(-6, 40, 0, 40)]     // nearly straight down: straight down
    [InlineData(30, -40, 35, -35)]  // nearer the diagonal: on it
    [InlineData(-20, -22, -21, -21)]
    public void AConstrainedLineIsLevelUprightOrDiagonal(double dx, double dy, double expectedDx, double expectedDy)
    {
        MeasureShape line = MeasureShape.Drawn(MeasureKind.Line, 200, 200, 200 + dx, 200 + dy, true, Grid, Grid);
        Assert.Equal((expectedDx, expectedDy), (line.Dx, line.Dy));
    }

    [Fact]
    public void AConstrainedRectangleIsASquare()
    {
        MeasureShape square = MeasureShape.Drawn(MeasureKind.Rectangle, 100, 100, 160, 130, true, Grid, Grid);
        Assert.Equal((100, 100, 60, 60), (square.X0, square.Y0, square.Width, square.Height));
        square = MeasureShape.Drawn(MeasureKind.Rectangle, 100, 100, 80, 50, true, Grid, Grid); // up and to the left
        Assert.Equal((50, 50, 50, 50), (square.X0, square.Y0, square.Width, square.Height));
        // Between the pixel edges, as the mouse is: still exactly as wide as it is high.
        square = MeasureShape.Drawn(MeasureKind.Rectangle, 305.37, 2137.2, 1159.1, 2393.6, true, Grid * 10, Grid * 10);
        Assert.Equal((305, 2137, 854, 854), (square.X0, square.Y0, square.Width, square.Height));
        square = MeasureShape.Rectangle(100, 100, 300, 200).Dragged(MeasureHandle.BottomRight, 300.4, 200.3, 380.9, 251.1, true, Grid, Grid);
        Assert.Equal((100, 100, 281, 281), (square.X0, square.Y0, square.Width, square.Height));
    }

    [Fact]
    public void WhatIsDrawnStaysOnTheGrid()
    {
        MeasureShape line = MeasureShape.Drawn(MeasureKind.Line, -5, 10, 150, 90, false, 100, 80);
        Assert.Equal(MeasureShape.Line(0, 10, 100, 80), line);
        MeasureShape rectangle = MeasureShape.Drawn(MeasureKind.Rectangle, 90, 70, 200, 200, false, 100, 80);
        Assert.Equal((90, 70, 100, 80), (rectangle.X0, rectangle.Y0, rectangle.X1, rectangle.Y1));
        Assert.Equal(MeasureShape.Line(0, 80, 100, 0), MeasureShape.Line(-10, 500, 300, -20).Within(100, 80));
        Assert.Equal(MeasureShape.Circle(100, 80, 30), MeasureShape.Circle(150, 90, 30).Within(100, 80));
    }

    [Fact]
    public void DraggingAnEndOfALineMovesThatEnd()
    {
        MeasureShape line = MeasureShape.Line(10, 10, 60, 10);
        // Taken hold of two pixels off the end: the end moves as far as the mouse does, and does not jump to it.
        Assert.Equal(MeasureShape.Line(10, 10, 70, 35), line.Dragged(MeasureHandle.End, 62, 11, 72, 36, false, Grid, Grid));
        Assert.Equal(MeasureShape.Line(5, 0, 60, 10), line.Dragged(MeasureHandle.Start, 10, 10, 5, -30, false, Grid, Grid)); // kept on the grid
        Assert.Equal(MeasureShape.Line(10, 10, 10, 80), line.Dragged(MeasureHandle.End, 60, 10, 13, 80, true, Grid, Grid));  // constrained: upright
    }

    [Fact]
    public void DraggingTheBodyMovesTheWholeAsFarAsTheGridLets()
    {
        MeasureShape line = MeasureShape.Line(10, 10, 60, 30);
        Assert.Equal(MeasureShape.Line(15.5, 7, 65.5, 27), line.Dragged(MeasureHandle.Body, 30, 20, 35.5, 17, false, 100, 100));
        Assert.Equal(MeasureShape.Line(50, 0, 100, 20), line.Dragged(MeasureHandle.Body, 30, 20, 500, -500, false, 100, 100));

        MeasureShape rectangle = MeasureShape.Rectangle(10, 10, 30, 20);
        MeasureShape moved = rectangle.Dragged(MeasureHandle.Body, 0, 0, 4.4, 7.6, false, 100, 100);
        Assert.Equal((14, 18, 34, 28), (moved.X0, moved.Y0, moved.X1, moved.Y1)); // by whole pixels, its size as it was
        moved = rectangle.Dragged(MeasureHandle.Body, 0, 0, -50, 500, false, 100, 100);
        Assert.Equal((0, 90, 20, 100), (moved.X0, moved.Y0, moved.X1, moved.Y1));

        MeasureShape circle = MeasureShape.Circle(50, 50, 20).Dragged(MeasureHandle.Body, 50, 50, 300, 40, false, 100, 100);
        Assert.Equal((100, 40, 20), (circle.X0, circle.Y0, circle.Radius)); // the centre stays on the grid; the circle may reach beyond it
    }

    [Fact]
    public void DraggingACornerOrASideChangesTheRectangle()
    {
        MeasureShape rectangle = MeasureShape.Rectangle(100, 100, 300, 200);
        MeasureShape dragged = rectangle.Dragged(MeasureHandle.BottomRight, 300, 200, 350, 260, false, Grid, Grid);
        Assert.Equal((100, 100, 350, 260), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
        dragged = rectangle.Dragged(MeasureHandle.Left, 100, 150, 140.4, 400, false, Grid, Grid); // a side moves along one axis only
        Assert.Equal((140, 100, 300, 200), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
        dragged = rectangle.Dragged(MeasureHandle.Top, 200, 100, 0, 70, false, Grid, Grid);
        Assert.Equal((100, 70, 300, 200), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
        // Dragged past the opposite corner, it turns over and is still a rectangle.
        dragged = rectangle.Dragged(MeasureHandle.TopLeft, 100, 100, 400, 260, false, Grid, Grid);
        Assert.Equal((300, 200, 400, 260), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
        // Constrained: a square from the corner that stays, as large as the longer side asked for ...
        dragged = MeasureShape.Rectangle(100, 400, 300, 500).Dragged(MeasureHandle.TopRight, 300, 400, 340, 380, true, Grid, Grid);
        Assert.Equal((100, 260, 340, 500), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
        // ... and no larger than the grid has room for.
        dragged = rectangle.Dragged(MeasureHandle.TopRight, 300, 100, 340, 0, true, Grid, Grid);
        Assert.Equal((100, 0, 300, 200), (dragged.X0, dragged.Y0, dragged.X1, dragged.Y1));
    }

    [Fact]
    public void AConstrainedDiagonalEndsWhereTheGridDoes()
    {
        MeasureShape line = MeasureShape.Drawn(MeasureKind.Line, 80, 50, 140, 115, true, 100, 200);
        Assert.Equal(MeasureShape.Line(80, 50, 100, 70), line); // still a diagonal, not bent at the edge
    }

    [Fact]
    public void DraggingACircleByItsOutlineChangesItsRadius()
    {
        MeasureShape circle = MeasureShape.Circle(200, 200, 50);
        // Taken hold of 2 pixels outside the outline and dragged 30 further out.
        Assert.Equal(80, circle.Dragged(MeasureHandle.Radius, 252, 200, 282, 200, false, Grid, Grid).Radius, 9);
        Assert.Equal(0, circle.Dragged(MeasureHandle.Radius, 250, 200, 200, 200, false, Grid, Grid).Radius, 9);  // to the centre: nothing left
        Assert.Equal(10, circle.Dragged(MeasureHandle.Radius, 250, 200, 190, 200, false, Grid, Grid).Radius, 9); // and past it: it passes through the mouse again
    }
}

public unsafe class LineProfileTests
{
    static float[] Values(LineProfile profile, string channel) => profile.Find(channel)!.Values;

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10, 11)]
    [InlineData(10.4, 11)]
    [InlineData(10.6, 12)]
    public void ThereIsASampleForEveryPixelOfLength(double length, int samples)
    {
        Assert.Equal(samples, LineProfile.SamplesFor(length));
    }

    [Fact]
    public void AlongARowThroughThePixelCentresItIsTheStoredValues()
    {
        using DecodedImage image = Pixels.Gray8(20, 10, (x, y) => (byte)(x * 10 + y));
        var source = new CompareSource(image, Affine2.Identity);
        LineProfile profile = LineProfile.Sample(source, MeasureShape.Line(2.5, 4.5, 12.5, 4.5));
        Assert.Equal((11, 1.0, 11, false), (profile.Count, profile.Step, profile.Covered, profile.IsColor));
        Assert.Equal([24f, 34, 44, 54, 64, 74, 84, 94, 104, 114, 124], Values(profile, ChannelMetrics.Value));
        Assert.Equal((7.5, 4.5), profile.PointAt(5));
        Assert.Equal(5, profile.DistanceAt(5));

        // Down a column, and backwards.
        profile = LineProfile.Sample(source, MeasureShape.Line(7.5, 8.5, 7.5, 5.5));
        Assert.Equal([78f, 77, 76, 75], Values(profile, ChannelMetrics.Value));
    }

    [Fact]
    public void AColourImageGivesItsChannelsAndLuma()
    {
        using DecodedImage image = Pixels.Bgra(8, 4, (x, y) => ((byte)(x * 20), (byte)(100 + y), 7, (byte)(255 - x)));
        LineProfile profile = LineProfile.Sample(new CompareSource(image, Affine2.Identity), MeasureShape.Line(1.5, 2.5, 4.5, 2.5));
        Assert.True(profile.IsColor);
        Assert.Equal([ChannelMetrics.Red, ChannelMetrics.Green, ChannelMetrics.Blue, ChannelMetrics.Alpha, ChannelMetrics.Luma], profile.Channels.Select(c => c.Name).ToArray());
        Assert.Equal([20f, 40, 60, 80], Values(profile, ChannelMetrics.Red));
        Assert.Equal([102f, 102, 102, 102], Values(profile, ChannelMetrics.Green));
        Assert.Equal([7f, 7, 7, 7], Values(profile, ChannelMetrics.Blue));
        Assert.Equal([254f, 253, 252, 251], Values(profile, ChannelMetrics.Alpha));
        Assert.Equal(40 * 0.2126f + 102 * 0.7152f + 7 * 0.0722f, Values(profile, ChannelMetrics.Luma)[1], 3);
    }

    [Fact]
    public void ASlantedLineBlendsBetweenPixels()
    {
        // A slope: blending four neighbours gives the slope's own value at any point.
        using DecodedImage image = Pixels.Float(40, 30, (x, y) => 3 * x + 2 * y);
        LineProfile profile = LineProfile.Sample(new CompareSource(image, Affine2.Identity), MeasureShape.Line(2.5, 2.5, 26.5, 20.5));
        Assert.Equal(31, profile.Count); // 30 pixels long
        Assert.Equal(1, profile.Step, 12);
        float[] values = Values(profile, ChannelMetrics.Value);
        for (int i = 0; i < profile.Count; i++)
        {
            (double x, double y) = profile.PointAt(i);
            Assert.Equal(3 * (x - 0.5) + 2 * (y - 0.5), values[i], 3);
        }
        Assert.Equal((2.5, 2.5), profile.PointAt(0));
        Assert.Equal((26.5, 20.5), profile.PointAt(30));
    }

    [Fact]
    public void BetweenTwoPixelsItIsHalfOfEach()
    {
        using DecodedImage image = Pixels.Gray16(6, 3, (x, _) => (ushort)(x * 1000));
        // The line runs along the edge between two rows, and from a pixel's edge to another's.
        LineProfile profile = LineProfile.Sample(new CompareSource(image, Affine2.Identity), MeasureShape.Line(1, 1, 4, 1));
        Assert.Equal([500f, 1500, 2500, 3500], Values(profile, ChannelMetrics.Value));
    }

    [Fact]
    public void WhereTheImageHasNoPixelThereIsNoValue()
    {
        using DecodedImage image = Pixels.Gray8(10, 10, (x, _) => (byte)(x + 1));
        LineProfile profile = LineProfile.Sample(new CompareSource(image, Affine2.Identity), MeasureShape.Line(-3.5, 5.5, 12.5, 5.5));
        float[] values = Values(profile, ChannelMetrics.Value);
        Assert.Equal(17, profile.Count);
        Assert.Equal(10, profile.Covered);
        Assert.True(float.IsNaN(values[0]) && float.IsNaN(values[3]) && float.IsNaN(values[14]) && float.IsNaN(values[16]));
        Assert.Equal((1f, 10f), (values[4], values[13]));
        // The summary is of the values that are there.
        Assert.Equal(new ValueSummary(10, 1, 10, 5.5, Math.Sqrt(8.25)), profile.Channels[0].Summary);

        LineProfile off = LineProfile.Sample(new CompareSource(image, Affine2.Identity), MeasureShape.Line(20, 20, 30, 20));
        Assert.Equal(0, off.Covered);
        Assert.Equal(ValueSummary.None, off.Channels[0].Summary);
    }

    [Theory]
    [InlineData(Orientation.Rotate90)]
    [InlineData(Orientation.Rotate180)]
    [InlineData(Orientation.MirrorHorizontal)]
    [InlineData(Orientation.Transverse)]
    public void ATurnedImageIsSampledUpright(Orientation orientation)
    {
        using DecodedImage image = Pixels.Gray8(7, 4, (x, y) => (byte)(y * 7 + x + 1));
        var source = new CompareSource(image, orientation.ToUpright(7, 4));
        int width = source.Bounds.Width, height = source.Bounds.Height;
        float* row = stackalloc float[4 * width];
        for (int y = 0; y < height; y++)
        {
            // The whole row of the upright image, through the centres of its pixels.
            LineProfile profile = LineProfile.Sample(source, MeasureShape.Line(0.5, y + 0.5, width - 0.5, y + 0.5));
            source.ReadRow(y, 0, width, row, row + width, row + 2 * width, row + 3 * width, 1);
            Assert.Equal(new ReadOnlySpan<float>(row, width).ToArray(), Values(profile, ChannelMetrics.Value));
        }
    }

    [Fact]
    public void AnImageMovedByHalfAPixelIsBlendedAndTheScaleIsApplied()
    {
        using DecodedImage image = Pixels.Gray8(8, 2, (x, _) => (byte)(x * 10));
        var source = new CompareSource(image, Affine2.ScaleTranslate(1, 1, 0.5, 0));
        LineProfile profile = LineProfile.Sample(source, MeasureShape.Line(2.5, 0.5, 5.5, 0.5), 1f / 255);
        float[] values = Values(profile, ChannelMetrics.Value);
        Assert.Equal(15f / 255, values[0], 6); // half of pixel 1 and half of pixel 2
        Assert.Equal(45f / 255, values[3], 6);
    }
}

public unsafe class AreaStatisticsTests
{
    /// <summary>The numbers of a set of values, worked out the slow way.</summary>
    static (long Count, double Min, double Max, double Mean, double StdDev, double Median) Plain(List<double> values)
    {
        values.Sort();
        double mean = values.Average();
        double spread = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
        return (values.Count, values[0], values[^1], mean, spread, values[(values.Count - 1) / 2]);
    }

    static void AssertMatches((long Count, double Min, double Max, double Mean, double StdDev, double Median) expected, AreaStatistics area, ChannelStatistics channel, double tolerance = 1e-6)
    {
        Assert.Equal(expected.Count, area.Pixels);
        Assert.Equal(expected.Min, channel.Min, tolerance);
        Assert.Equal(expected.Max, channel.Max, tolerance);
        Assert.Equal(expected.Mean, channel.Mean, tolerance);
        Assert.Equal(expected.StdDev, channel.StdDev, tolerance);
        Assert.Equal(expected.Count, channel.Histogram.Total);
    }

    [Fact]
    public void ARectangleOfAGreyImage()
    {
        using DecodedImage image = Pixels.Gray8(40, 30, Pixels.Texture);
        MeasureShape rectangle = MeasureShape.Rectangle(3, 2, 11, 9);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), rectangle)!;
        var values = new List<double>();
        var counts = new long[256];
        for (int y = 2; y < 9; y++)
            for (int x = 3; x < 11; x++)
            {
                values.Add(Pixels.Texture(x, y));
                counts[Pixels.Texture(x, y)]++;
            }
        var expected = Plain(values);
        ChannelStatistics channel = Assert.Single(area.Channels);
        Assert.Equal(ChannelMetrics.Value, channel.Name);
        AssertMatches(expected, area, channel);
        Assert.Equal(56, area.Pixels);
        Assert.Equal(expected.Median, channel.Median);
        Assert.Equal(new CanvasRect(3, 2, 8, 7), area.Bounds);
        Assert.True(area.Whole && area.PixelAligned && channel.Histogram.Whole);
        Assert.Equal((255.0, 0L), (area.FullValue, area.Ignored));
        Assert.Equal(counts, channel.Histogram.Counts);
        Assert.Equal((0.0, 1.0), (channel.Histogram.Lowest, channel.Histogram.BinWidth));
    }

    [Theory]
    [InlineData(20, 15, 9)]
    [InlineData(20.5, 15.5, 9.2)]
    [InlineData(3, 4, 8)]        // partly off the image
    [InlineData(39.5, 29.5, 0.6)] // one pixel
    public void ACircleIsThePixelsWhoseCentresAreInside(double centreX, double centreY, double radius)
    {
        using DecodedImage image = Pixels.Gray8(40, 30, Pixels.Texture);
        MeasureShape circle = MeasureShape.Circle(centreX, centreY, radius);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), circle)!;
        var values = new List<double>();
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                if (circle.HoldsPixel(x, y)) values.Add(Pixels.Texture(x, y));
        var expected = Plain(values);
        AssertMatches(expected, area, area.Channels[0]);
        Assert.Equal(expected.Median, area.Channels[0].Median);
    }

    [Fact]
    public void AColourImageHasItsChannelsAndLumaAndAlphaOnlyIfNotOpaque()
    {
        using DecodedImage opaque = Pixels.Bgra(30, 20, (x, y) => ((byte)(x * 8), (byte)(y * 12), (byte)(x + y), 255));
        MeasureShape rectangle = MeasureShape.Rectangle(4, 3, 20, 15);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(opaque, Affine2.Identity), rectangle)!;
        Assert.Equal([ChannelMetrics.Red, ChannelMetrics.Green, ChannelMetrics.Blue, ChannelMetrics.Luma], area.Channels.Select(c => c.Name).ToArray());

        List<double> red = [], green = [], luma = [];
        for (int y = 3; y < 15; y++)
            for (int x = 4; x < 20; x++)
            {
                red.Add(x * 8);
                green.Add(y * 12);
                luma.Add(x * 8 * 0.2126f + y * 12 * 0.7152f + (x + y) * 0.0722f);
            }
        AssertMatches(Plain(red), area, area.Find(ChannelMetrics.Red)!);
        AssertMatches(Plain(green), area, area.Find(ChannelMetrics.Green)!);
        AssertMatches(Plain(luma), area, area.Find(ChannelMetrics.Luma)!, 1e-3);
        Assert.Equal(Plain(red).Median, area.Find(ChannelMetrics.Red)!.Median);

        using DecodedImage holed = Pixels.Bgra(30, 20, (x, y) => (10, 20, 30, (byte)(x == 7 && y == 7 ? 0 : 255)));
        area = AreaStatistics.Compute(new CompareSource(holed, Affine2.Identity), rectangle)!;
        ChannelStatistics alpha = area.Find(ChannelMetrics.Alpha)!;
        Assert.Equal((0, 255), (alpha.Min, alpha.Max));
        Assert.Equal(1, alpha.Histogram.Counts[0]);
        Assert.Equal(191, alpha.Histogram.Counts[255]);
    }

    [Fact]
    public void SixteenBitValuesAreCountedOneByOne()
    {
        using DecodedImage image = Pixels.Gray16(50, 40, (x, y) => (ushort)(x == 0 ? 65535 : 1000 + 13 * x + 7 * y));
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), MeasureShape.Rectangle(0, 0, 50, 40))!;
        var values = new List<double>();
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 50; x++) values.Add(x == 0 ? 65535 : 1000 + 13 * x + 7 * y);
        ChannelStatistics channel = area.Channels[0];
        var expected = Plain(values);
        AssertMatches(expected, area, channel, 1e-6);
        Assert.Equal(expected.Median, channel.Median);
        Assert.Equal(65536, channel.Histogram.Counts.Length);
        Assert.Equal(40, channel.Histogram.Counts[65535]);
        Assert.Equal((65535.0, true), (area.FullValue, area.Whole));
    }

    [Fact]
    public void FloatValuesGoIntoBinsAndWhatIsNotANumberIsLeftOut()
    {
        using DecodedImage image = Pixels.Float(40, 30, (x, y) => x == 5 && y == 5 ? float.NaN : x == 6 && y == 5 ? float.PositiveInfinity : -2.5f + 0.01f * x + 0.03f * y);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), MeasureShape.Rectangle(0, 0, 40, 30))!;
        var values = new List<double>();
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                if (!(y == 5 && x is 5 or 6)) values.Add(-2.5f + 0.01f * x + 0.03f * y);
        ChannelStatistics channel = area.Channels[0];
        var expected = Plain(values);
        AssertMatches(expected, area, channel, 1e-5);
        Assert.Equal((1198L, 2L), (area.Pixels, area.Ignored));
        Assert.False(area.Whole || channel.Histogram.Whole);
        Assert.Equal(AreaStatistics.FloatBins, channel.Histogram.Counts.Length);
        Assert.Equal(expected.Min, channel.Histogram.Lowest, 6);
        Assert.Equal(expected.Max, channel.Histogram.ValueAt(AreaStatistics.FloatBins - 1), 4);
        Assert.InRange(channel.Median, expected.Median - channel.Histogram.BinWidth, expected.Median + channel.Histogram.BinWidth);
    }

    [Fact]
    public void AFlatAreaHasNoSpread()
    {
        using DecodedImage image = Pixels.Float(10, 10, (_, _) => 0.75f);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), MeasureShape.Rectangle(2, 2, 6, 6))!;
        ChannelStatistics channel = area.Channels[0];
        Assert.Equal((0.75, 0.75, 0.75, 0.0, 0.75), (channel.Min, channel.Max, channel.Mean, channel.StdDev, channel.Median));
        Assert.Equal(16, channel.Histogram.Counts[0]);
    }

    [Fact]
    public void OnlyThePixelsTheImageHasAreCounted()
    {
        using DecodedImage image = Pixels.Gray8(20, 10, (x, _) => (byte)x);
        var source = new CompareSource(image, Affine2.ScaleTranslate(1, 1, 5, 3)); // placed at 5, 3 on a larger grid
        AreaStatistics area = AreaStatistics.Compute(source, MeasureShape.Rectangle(0, 0, 10, 8))!;
        Assert.Equal(new CanvasRect(5, 3, 5, 5), area.Bounds);
        Assert.Equal(25, area.Pixels);
        Assert.Equal((0, 4, 2), (area.Channels[0].Min, area.Channels[0].Max, area.Channels[0].Mean));

        area = AreaStatistics.Compute(source, MeasureShape.Rectangle(40, 40, 50, 50))!;
        Assert.Equal(0, area.Pixels);
        Assert.Empty(area.Channels);
        Assert.Empty(AreaStatistics.Compute(source, MeasureShape.Line(0, 0, 20, 10))!.Channels); // a line has no inside
    }

    [Fact]
    public void ValuesScaledForAnotherDepthAreNoLongerWhole()
    {
        using DecodedImage image = Pixels.Gray8(16, 16, (x, y) => (byte)(x * 16 + y));
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), MeasureShape.Rectangle(0, 0, 16, 16), 1f / 255)!;
        ChannelStatistics channel = area.Channels[0];
        Assert.False(area.Whole);
        Assert.Equal(1, area.FullValue, 6);
        Assert.Equal((0, 1), (channel.Min, channel.Max), (a, b) => Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6);
        Assert.Equal(0.5, channel.Mean, 6);
        Assert.Equal(256, channel.Histogram.Total);
    }

    [Fact]
    public void ATurnedImageIsBlendedAndCountedWhereItIs()
    {
        using DecodedImage image = Pixels.Gray8(60, 40, Pixels.Texture);
        double angle = 30 * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        var source = new CompareSource(image, new Affine2(cos, sin, -sin, cos, 40, 5));
        MeasureShape rectangle = MeasureShape.Rectangle(0, 0, 100, 100);
        AreaStatistics area = AreaStatistics.Compute(source, rectangle)!;
        Assert.False(area.PixelAligned);
        // As many pixels as the image has, give or take those its slanted edges cut.
        Assert.InRange(area.Pixels, 60 * 40 - 220, 60 * 40 + 220);
        long counted = 0;
        for (int y = 0; y < 100; y++)
        {
            source.RowSpan(y, out int left, out int right);
            counted += Math.Max(0, Math.Min(right, 100) - Math.Max(left, 0));
        }
        Assert.Equal(counted, area.Pixels);
        Assert.InRange(area.Channels[0].Mean, 90, 130);
    }

    [Fact]
    public void ALargeAreaWorkedOnByManyThreadsAddsUpTheSame()
    {
        using DecodedImage image = Pixels.Bgra(700, 500, (x, y) => ((byte)(x * 7 + y), (byte)(x ^ y), (byte)(x * y), 255));
        MeasureShape circle = MeasureShape.Circle(340.5, 251, 240);
        AreaStatistics area = AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), circle)!;
        List<double> red = [], blue = [];
        for (int y = 0; y < 500; y++)
            for (int x = 0; x < 700; x++)
            {
                if (!circle.HoldsPixel(x, y)) continue;
                red.Add((byte)(x * 7 + y));
                blue.Add((byte)(x * y));
            }
        AssertMatches(Plain(red), area, area.Find(ChannelMetrics.Red)!);
        AssertMatches(Plain(blue), area, area.Find(ChannelMetrics.Blue)!);
        Assert.Equal(Plain(blue).Median, area.Find(ChannelMetrics.Blue)!.Median);
    }

    [Fact]
    public void ACancelledRunGivesNothing()
    {
        using DecodedImage image = Pixels.Gray8(64, 64, Pixels.Texture);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Null(AreaStatistics.Compute(new CompareSource(image, Affine2.Identity), MeasureShape.Rectangle(0, 0, 64, 64), 1, cancel.Token));
    }
}

public class MeasureUnitsTests
{
    [Fact]
    public void ImagesOfOneDepthKeepTheirValues()
    {
        float[] scales = new float[3];
        Assert.Equal(ValueUnit.Bits8, MeasureUnits.Common([PixelLayout.Gray8, PixelLayout.Bgra8, PixelLayout.Gray8], scales));
        Assert.Equal([1f, 1f, 1f], scales);
        Assert.Equal(ValueUnit.Bits16, MeasureUnits.Common([PixelLayout.Gray16, PixelLayout.Rgba16], scales));
        Assert.Equal(ValueUnit.Float, MeasureUnits.Common([PixelLayout.GrayFloat], scales));
        Assert.Equal(1f, scales[0]);
    }

    [Fact]
    public void ImagesOfDifferentDepthsAreReadFromNoughtToOne()
    {
        float[] scales = new float[4];
        Assert.Equal(ValueUnit.Normalised, MeasureUnits.Common([PixelLayout.Gray8, PixelLayout.Gray16, PixelLayout.GrayFloat, PixelLayout.Bgra8], scales));
        Assert.Equal([1f / 255, 1f / 65535, 1f, 1f / 255], scales);
        // The same units two compared images are given in.
        (ValueUnit unit, float first, float second) = CompareMetrics.Units(PixelLayout.Gray8, PixelLayout.Gray16);
        Assert.Equal((ValueUnit.Normalised, scales[0], scales[1]), (unit, first, second));
    }
}

public class MeasureReportTests
{
    static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TheListHasARowPerMeasurement()
    {
        MeasureShape[] shapes = [MeasureShape.Line(10.5, 20.5, 13.5, 16.5), MeasureShape.Rectangle(2, 4, 10, 8), MeasureShape.Circle(50, 40, 10)];
        string[] lines = Lines(MeasureReport.ListCsv(shapes));
        Assert.Equal("number,kind,x0,y0,x1,y1,length,dx,dy,angle_deg,width,height,radius,area,perimeter", lines[0]);
        Assert.Equal("1,line,10.5,20.5,13.5,16.5,5,3,-4,53.1301024,,,,,", lines[1]);
        Assert.Equal("2,rectangle,2,4,10,8,,,,,8,4,,32,24", lines[2]);
        Assert.Equal("3,circle,50,40,,,,,,,20,20,10,314.159265,62.8318531", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void WithAScaleTheSizesFollowInItsUnit()
    {
        string[] lines = Lines(MeasureReport.ListCsv([MeasureShape.Line(0, 0, 30, 40), MeasureShape.Rectangle(0, 0, 10, 20)], 0.5, "mm"));
        Assert.EndsWith(",unit,unit_per_pixel,length_in_unit,width_in_unit,height_in_unit,radius_in_unit,area_in_unit2,perimeter_in_unit", lines[0]);
        Assert.EndsWith(",mm,0.5,25,,,,,", lines[1]);
        Assert.EndsWith(",mm,0.5,,5,10,,50,30", lines[2]);
    }

    [Fact]
    public void AProfileHasARowPerSample()
    {
        using DecodedImage image = Pixels.Gray8(10, 4, (x, _) => (byte)(x * 3));
        var source = new CompareSource(image, Affine2.Identity);
        LineProfile profile = LineProfile.Sample(source, MeasureShape.Line(7.5, 1.5, 11.5, 1.5));
        string[] lines = Lines(MeasureReport.ProfileCsv([profile]));
        Assert.Equal("index,distance_px,x,y,value", lines[0]);
        Assert.Equal("0,0,7.5,1.5,21", lines[1]);
        Assert.Equal("2,2,9.5,1.5,27", lines[3]);
        Assert.Equal("4,4,11.5,1.5,", lines[5]); // off the image: no value
        Assert.Equal(6, lines.Length);

        lines = Lines(MeasureReport.ProfileCsv([profile], pixelSize: 0.25, unit: "mm"));
        Assert.Equal("index,distance_px,distance_mm,x,y,value", lines[0]);
        Assert.Equal("2,2,0.5,9.5,1.5,27", lines[3]);

        // One image of a comparison, the others switched off: its columns still say which it is.
        Assert.Equal("index,distance_px,x,y,value_3", Lines(MeasureReport.ProfileCsv([profile], [3]))[0]);
    }

    [Fact]
    public void TheProfilesOfSeveralImagesShareTheRows()
    {
        using DecodedImage grey = Pixels.Gray8(10, 4, (x, _) => (byte)x);
        using DecodedImage colour = Pixels.Bgra(10, 4, (x, _) => ((byte)(x * 2), 5, 6, 255));
        using DecodedImage deep = Pixels.Gray16(10, 4, (x, _) => (ushort)(x * 1000));
        MeasureShape line = MeasureShape.Line(1.5, 1.5, 3.5, 1.5);
        LineProfile first = LineProfile.Sample(new CompareSource(grey, Affine2.Identity), line), second = LineProfile.Sample(new CompareSource(colour, Affine2.Identity), line);
        LineProfile third = LineProfile.Sample(new CompareSource(deep, Affine2.Identity), line);
        string[] lines = Lines(MeasureReport.ProfileCsv([first, second], [1, 2]));
        Assert.Equal("index,distance_px,x,y,value_1,red_2,green_2,blue_2,alpha_2,luma_2", lines[0]);
        Assert.StartsWith("1,1,2.5,1.5,2,4,5,6,255,", lines[2]);
        Assert.Equal(4, lines.Length);

        // Numbered as on screen: here the second of four images is switched off.
        lines = Lines(MeasureReport.ProfileCsv([first, second, third], [1, 3, 4]));
        Assert.Equal("index,distance_px,x,y,value_1,red_3,green_3,blue_3,alpha_3,luma_3,value_4", lines[0]);
        Assert.EndsWith(",3000", lines[3]);
        // Without numbers, in the order given.
        Assert.Equal("index,distance_px,x,y,value_1,value_2", Lines(MeasureReport.ProfileCsv([first, third]))[0]);
    }

    [Fact]
    public void TheNumbersAndTheHistogramOfAnArea()
    {
        using DecodedImage image = Pixels.Gray8(10, 10, (x, _) => (byte)(x < 5 ? 10 : 30));
        var source = new CompareSource(image, Affine2.Identity);
        AreaStatistics area = AreaStatistics.Compute(source, MeasureShape.Rectangle(2, 0, 8, 10))!;
        string[] lines = Lines(MeasureReport.StatisticsCsv([area], [@"C:\pictures\a, b.png"]));
        Assert.Equal("image,kind,x0,y0,x1,y1,radius,pixels,channel,min,max,mean,std_dev,median", lines[0]);
        Assert.Equal("\"C:\\pictures\\a, b.png\",rectangle,2,0,8,10,,60,value,10,30,20,10,10", lines[1]);

        lines = Lines(MeasureReport.HistogramCsv([area, area], ["a.png", "b.png"]));
        Assert.Equal(["image,channel,value,count", "a.png,value,10,30", "a.png,value,30,30", "b.png,value,10,30", "b.png,value,30,30"], lines);

        AreaStatistics circle = AreaStatistics.Compute(source, MeasureShape.Circle(5, 5, 3))!;
        Assert.StartsWith("a.png,circle,5,5,,,3,", Lines(MeasureReport.StatisticsCsv([circle], ["a.png"]))[1]);

        // Three images: a block of rows each, in the order given.
        lines = Lines(MeasureReport.StatisticsCsv([area, circle, area], ["a.png", "b.png", "c.png"]));
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("b.png,circle,", lines[2]);
        Assert.StartsWith("c.png,rectangle,", lines[3]);
    }
}
