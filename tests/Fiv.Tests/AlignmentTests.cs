using Fiv.Comparing;
using Fiv.Imaging;
using Xunit;

namespace Fiv.Tests;

/// <summary>
/// A picture defined at every point, not only at pixels: a few hundred soft blobs of all sizes.
/// Rendered under any transform it is exact, which is what makes it a yardstick for alignment:
/// nothing has been resampled, so what the registration finds can be held against the truth.
/// </summary>
sealed unsafe class Scene
{
    readonly (double X, double Y, double Sigma, double Amplitude)[] _blobs;

    public Scene(int seed, double width, double height, int count = 700)
    {
        var random = new Random(seed);
        _blobs = new (double, double, double, double)[count];
        for (int i = 0; i < count; i++)
        {
            // Mostly small blobs, some large: detail at every scale. None so small that pixels could not hold it.
            double sigma = 1.6 * Math.Pow(14, random.NextDouble() * random.NextDouble());
            _blobs[i] = (random.NextDouble() * (width + 200) - 100, random.NextDouble() * (height + 200) - 100, sigma, (random.NextDouble() * 2 - 1) * 55);
        }
    }

    /// <summary>The scene's value at each pixel centre of an image whose pixel position q shows the scene at <paramref name="imageToScene"/>(q).</summary>
    public float[] Values(int width, int height, Affine2 imageToScene)
    {
        var values = new float[width * height];
        Affine2 sceneToImage = imageToScene.Inverse();
        double magnification = Math.Sqrt(Math.Abs(sceneToImage.A * sceneToImage.D - sceneToImage.B * sceneToImage.C));
        foreach ((double bx, double by, double sigma, double amplitude) in _blobs)
        {
            (double cx, double cy) = sceneToImage.Apply(bx, by);
            double reach = 4 * sigma * magnification;
            int left = (int)Math.Max(0, Math.Floor(cx - reach)), right = (int)Math.Min(width - 1, Math.Ceiling(cx + reach));
            int top = (int)Math.Max(0, Math.Floor(cy - reach)), bottom = (int)Math.Min(height - 1, Math.Ceiling(cy + reach));
            double scale = -0.5 / (sigma * sigma);
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    (double sx, double sy) = imageToScene.Apply(x + 0.5, y + 0.5);
                    double dx = sx - bx, dy = sy - by;
                    values[y * width + x] += (float)(amplitude * Math.Exp(scale * (dx * dx + dy * dy)));
                }
            }
        }
        return values;
    }

    public DecodedImage Render(int width, int height, Affine2 imageToScene, PixelLayout layout = PixelLayout.Gray8, double gain = 1, double offset = 0, int noiseSeed = 0, double noise = 0)
    {
        float[] values = Values(width, height, imageToScene);
        Random? random = noise > 0 ? new Random(noiseSeed) : null;
        var image = new DecodedImage(width, height, layout, width, height);
        for (int y = 0; y < height; y++)
        {
            byte* row = image.Pixels + (long)y * image.Stride;
            for (int x = 0; x < width; x++)
            {
                double value = 128 + values[y * width + x] * gain + offset;
                if (random != null) value += (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 2 * noise;
                switch (layout)
                {
                    case PixelLayout.Gray8:
                        row[x] = (byte)Math.Clamp(value + 0.5, 0, 255);
                        break;
                    case PixelLayout.Gray16:
                        ((ushort*)row)[x] = (ushort)Math.Clamp(value * 257 + 0.5, 0, 65535);
                        break;
                    case PixelLayout.GrayFloat:
                        ((float*)row)[x] = (float)(value / 255);
                        break;
                    case PixelLayout.Bgra8:
                        row[x * 4] = (byte)Math.Clamp(value * 0.9 + 0.5, 0, 255);
                        row[x * 4 + 1] = (byte)Math.Clamp(value + 0.5, 0, 255);
                        row[x * 4 + 2] = (byte)Math.Clamp(value * 1.05 + 0.5, 0, 255);
                        row[x * 4 + 3] = 255;
                        break;
                    default:
                    {
                        ushort* q = (ushort*)row + x * 4;
                        q[0] = q[1] = q[2] = (ushort)Math.Clamp(value * 257 + 0.5, 0, 65535);
                        q[3] = 65535;
                        break;
                    }
                }
            }
        }
        return image;
    }
}

public unsafe class AlignPoseTests
{
    [Fact]
    public void TheDefaultPoseChangesNothing()
    {
        AlignPose pose = default;
        Assert.True(pose.IsIdentity);
        Assert.Equal(1, pose.Scale);
        Assert.Equal(Affine2.Identity, pose.About(300, 200));
        Assert.Equal(AlignPose.Identity, pose);
    }

    [Fact]
    public void AMoveAloneIsExact()
    {
        Affine2 m = new AlignPose(3, -2).About(1234.5, 777.25);
        Assert.Equal(Affine2.ScaleTranslate(1, 1, 3, -2), m);
        Assert.True(new AlignPose(3, -2).IsWholePixels);
        Assert.False(new AlignPose(3.5, -2).IsWholePixels);
        Assert.False(new AlignPose(3, -2, 0.1).IsWholePixels);
    }

    [Fact]
    public void TurningAndScalingLeaveTheCentreWhereTheMovePutsIt()
    {
        Affine2 m = new AlignPose(10, 20, 30, 1.5).About(100, 50);
        (double x, double y) = m.Apply(100, 50);
        Assert.Equal(110, x, 9);
        Assert.Equal(70, y, 9);
        // A point to the right of the centre goes, turned clockwise on screen by 30 degrees, right and down, 1.5 times as far.
        (x, y) = m.Apply(102, 50);
        Assert.Equal(110 + 3 * Math.Cos(Math.PI / 6), x, 9);
        Assert.Equal(70 + 3 * Math.Sin(Math.PI / 6), y, 9);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(12.5, -7.25, 0, 1)]
    [InlineData(3, 4, 17.5, 1)]
    [InlineData(-3, 4, -120, 0.8)]
    [InlineData(0.1, 0.2, 0.03, 1.002)]
    public void APoseComesBackFromItsTransform(double dx, double dy, double rotation, double scale)
    {
        var pose = new AlignPose(dx, dy, rotation, scale);
        AlignPose back = AlignPose.FromSimilarity(pose.About(640, 360), 640, 360);
        Assert.Equal(dx, back.Dx, 9);
        Assert.Equal(dy, back.Dy, 9);
        Assert.Equal(rotation, back.Rotation, 9);
        Assert.Equal(scale, back.Scale, 9);
    }
}

public unsafe class FftTests
{
    [Fact]
    public void ASinglePointHasAFlatSpectrum()
    {
        const int width = 16, height = 8;
        float* re = stackalloc float[width * height], im = stackalloc float[width * height];
        new Span<float>(re, width * height).Clear();
        new Span<float>(im, width * height).Clear();
        re[0] = 1;
        Fft2D.For(width, height).Forward(re, im);
        for (int i = 0; i < width * height; i++)
        {
            Assert.Equal(1, re[i], 5);
            Assert.Equal(0, im[i], 5);
        }
    }

    [Fact]
    public void AWaveIsOnePairOfTerms()
    {
        // cos(2 pi (3 x / 32 + 5 y / 16)) is half the term (3, 5) plus half the term (-3, -5).
        const int width = 32, height = 16;
        float[] re = new float[width * height], im = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) re[y * width + x] = (float)Math.Cos(2 * Math.PI * (3.0 * x / width + 5.0 * y / height));
        fixed (float* r = re, i = im) Fft2D.For(width, height).Forward(r, i);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool term = (x == 3 && y == 5) || (x == width - 3 && y == height - 5);
                Assert.Equal(term ? width * height / 2 : 0, re[y * width + x], 2);
                Assert.Equal(0, im[y * width + x], 2);
            }
        }
    }

    [Theory]
    [InlineData(64, 64, false)]
    [InlineData(128, 32, false)]
    [InlineData(4, 256, false)]
    [InlineData(256, 256, true)]
    [InlineData(512, 128, true)]
    public void ThereAndBackIsTheSame(int width, int height, bool parallel)
    {
        var random = new Random(width * 31 + height);
        int count = width * height;
        float[] re = new float[count], im = new float[count], wasRe = new float[count], wasIm = new float[count];
        for (int i = 0; i < count; i++)
        {
            wasRe[i] = re[i] = random.NextSingle() * 2 - 1;
            wasIm[i] = im[i] = random.NextSingle() * 2 - 1;
        }
        Fft2D fft = Fft2D.For(width, height);
        fixed (float* r = re, i = im)
        {
            fft.Forward(r, i, parallel);
            fft.Inverse(r, i, parallel);
        }
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(wasRe[i], re[i], 1e-5f);
            Assert.Equal(wasIm[i], im[i], 1e-5f);
        }
    }

    [Fact]
    public void ItIsTheTransformByItsDefinition()
    {
        const int width = 8, height = 4;
        var random = new Random(5);
        float[] re = new float[width * height], im = new float[width * height];
        double[] wasRe = new double[width * height], wasIm = new double[width * height];
        for (int i = 0; i < re.Length; i++)
        {
            wasRe[i] = re[i] = random.NextSingle();
            wasIm[i] = im[i] = random.NextSingle();
        }
        fixed (float* r = re, i = im) Fft2D.For(width, height).Forward(r, i);
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                double sumRe = 0, sumIm = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double angle = -2 * Math.PI * ((double)u * x / width + (double)v * y / height), c = Math.Cos(angle), s = Math.Sin(angle);
                        sumRe += wasRe[y * width + x] * c - wasIm[y * width + x] * s;
                        sumIm += wasRe[y * width + x] * s + wasIm[y * width + x] * c;
                    }
                }
                Assert.Equal(sumRe, re[v * width + u], 4);
                Assert.Equal(sumIm, im[v * width + u], 4);
            }
        }
    }

    [Fact]
    public void SizesHaveToBePowersOfTwo()
    {
        Assert.Throws<ArgumentException>(() => Fft2D.For(100, 64));
        Assert.Equal(64, Fft2D.PowerOfTwoBelow(100));
        Assert.Equal(128, Fft2D.PowerOfTwoBelow(128));
        Assert.Equal(0, Fft2D.PowerOfTwoBelow(0));
    }
}

public unsafe class PhaseCorrelationTests
{
    /// <summary>The scene on an n x n grid, moved by (dx, dy), faded out towards the edges as the registration does it.</summary>
    static float[] Patch(Scene scene, int n, double dx, double dy)
    {
        float[] values = scene.Values(n, n, Affine2.ScaleTranslate(1, 1, 100 - dx, 100 - dy));
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
                values[y * n + x] *= (float)((0.5 - 0.5 * Math.Cos(2 * Math.PI * (x + 0.5) / n)) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (y + 0.5) / n)));
        }
        return values;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(0.5, 0.25)]
    [InlineData(-0.83, 0.61)]
    [InlineData(0.07, -0.42)]
    public void ASmallShiftIsFoundToAFewHundredthsOfAPixel(double dx, double dy)
    {
        var scene = new Scene(11, 400, 400);
        float[] a = Patch(scene, 128, 0, 0), b = Patch(scene, 128, dx, dy);
        fixed (float* pa = a, pb = b)
        {
            PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(pa, pb, 128, 128);
            Assert.InRange(peak.Dx, dx - 0.03, dx + 0.03);
            Assert.InRange(peak.Dy, dy - 0.03, dy + 0.03);
            Assert.True(peak.Height > 0.5, $"a clear peak, not {peak.Height}");
        }
    }

    /// <summary>
    /// The patch stays where it is while the picture moves under it, so the two patches hold less
    /// and less of the same picture, and what is found falls a little short. The registration
    /// therefore measures again after taking off what it found.
    /// </summary>
    [Theory]
    [InlineData(5, -3)]
    [InlineData(-12, 9)]
    [InlineData(-2.37, 21.81)]
    public void ALargeShiftIsFoundToWithinAFewPerCent(double dx, double dy)
    {
        var scene = new Scene(11, 400, 400);
        float[] a = Patch(scene, 128, 0, 0), b = Patch(scene, 128, dx, dy);
        fixed (float* pa = a, pb = b)
        {
            PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(pa, pb, 128, 128);
            Assert.InRange(peak.Dx, dx - 0.03 - 0.03 * Math.Abs(dx), dx + 0.03 + 0.03 * Math.Abs(dx));
            Assert.InRange(peak.Dy, dy - 0.03 - 0.03 * Math.Abs(dy), dy + 0.03 + 0.03 * Math.Abs(dy));
        }
    }

    [Fact]
    public void UnrelatedPicturesHaveNoPeakToSpeakOf()
    {
        double highest = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            float[] a = Patch(new Scene(seed, 400, 400), 128, 0, 0), b = Patch(new Scene(seed + 100, 400, 400), 128, 0, 0);
            fixed (float* pa = a, pb = b) highest = Math.Max(highest, PhaseCorrelation.Shift(pa, pb, 128, 128).Height);
        }
        // Not zero: of the thousands of places the peak could be, one always stands out a little.
        Assert.True(highest < 0.4, $"highest peak of unrelated patches: {highest}");
    }

    [Fact]
    public void AFlatPictureGivesNoShift()
    {
        float[] a = new float[64 * 64], b = Patch(new Scene(3, 400, 400), 64, 0, 0);
        fixed (float* pa = a, pb = b)
        {
            PhaseCorrelation.Peak peak = PhaseCorrelation.Shift(pa, pb, 64, 64);
            Assert.Equal(0, peak.Height);
            Assert.Equal(0, peak.Dx);
        }
    }
}

public unsafe class LumaPyramidTests
{
    [Fact]
    public void EachSizeIsTheAverageOfTwoByTwoOfTheOneBefore()
    {
        using DecodedImage image = Pixels.Gray8(256, 128, (x, y) => (byte)((x * 3 + y * 5) % 251));
        using var pyramid = new LumaPyramid(image);
        float* got = stackalloc float[4];
        // A grid step of two stored pixels reads the half size: sample (i, j) is the mean of the pixels (2i..2i+1, 2j..2j+1).
        pyramid.Sample(got, 2, 2, Affine2.ScaleTranslate(2, 2, 40, 20));
        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < 2; i++)
            {
                int x = 40 + 2 * i, y = 20 + 2 * j;
                double mean = ((x * 3 + y * 5) % 251 + ((x + 1) * 3 + y * 5) % 251 + (x * 3 + (y + 1) * 5) % 251 + ((x + 1) * 3 + (y + 1) * 5) % 251) / 4.0;
                Assert.Equal(mean, got[j * 2 + i], 3);
            }
        }
        // And a step of four the quarter size: the mean of four by four.
        pyramid.Sample(got, 1, 1, Affine2.ScaleTranslate(4, 4, 64, 32));
        double sum = 0;
        for (int y = 32; y < 36; y++)
            for (int x = 64; x < 68; x++) sum += (x * 3 + y * 5) % 251;
        Assert.Equal(sum / 16, got[0], 3);
    }

    [Fact]
    public void AtFullSizeItIsThePixelsAndBetweenThemTheirBlend()
    {
        using DecodedImage image = Pixels.Gray16(64, 64, (x, y) => (ushort)(x * 100 + y));
        using var pyramid = new LumaPyramid(image);
        float* got = stackalloc float[3];
        pyramid.Sample(got, 3, 1, Affine2.ScaleTranslate(1, 1, 10, 7));
        Assert.Equal(1007, got[0], 3);
        Assert.Equal(1107, got[1], 3);
        Assert.Equal(1207, got[2], 3);
        pyramid.Sample(got, 3, 1, Affine2.ScaleTranslate(1, 1, 10.5, 7.25));
        Assert.Equal(1057.25, got[0], 2);
    }

    [Fact]
    public void OutsideTheImageThereIsNothing()
    {
        using DecodedImage image = Pixels.Gray8(64, 64, (_, _) => 9);
        using var pyramid = new LumaPyramid(image);
        float* got = stackalloc float[4];
        pyramid.Sample(got, 4, 1, Affine2.ScaleTranslate(1, 1, -2, 10));
        Assert.True(float.IsNaN(got[0]));
        Assert.True(float.IsNaN(got[1]));
        Assert.Equal(9, got[2], 4);
        Assert.Equal(9, got[3], 4);
        pyramid.Sample(got, 2, 2, Affine2.ScaleTranslate(1, 1, 500, 500));
        Assert.True(float.IsNaN(got[3]));
    }

    [Fact]
    public void ColourIsItsLuma()
    {
        using DecodedImage image = Pixels.Solid(64, 64, 200, 100, 50);
        using var pyramid = new LumaPyramid(image);
        float value;
        pyramid.Sample(&value, 1, 1, Affine2.ScaleTranslate(1, 1, 5, 5));
        Assert.Equal(0.2126 * 200 + 0.7152 * 100 + 0.0722 * 50, value, 3);
        pyramid.Sample(&value, 1, 1, Affine2.ScaleTranslate(2, 2, 8, 8));
        Assert.Equal(0.2126 * 200 + 0.7152 * 100 + 0.0722 * 50, value, 3);
    }

    [Fact]
    public void ItKeepsThePixelsAliveUntilDisposed()
    {
        DecodedImage image = Pixels.Gray8(128, 128, (x, _) => (byte)x);
        var pyramid = new LumaPyramid(image);
        image.Dispose();
        float value;
        pyramid.Sample(&value, 1, 1, Affine2.ScaleTranslate(1, 1, 77, 5));
        Assert.Equal(77, value, 4);
        pyramid.Dispose();
        Assert.True(image.Pixels == null);
    }
}

/// <summary>
/// The exit criterion of the alignment: known moves, turns and scalings of an exact picture come
/// back to within a tenth of a pixel anywhere in the image and a twentieth of a degree.
/// </summary>
public unsafe class RegistrationTests
{
    const int Width = 640, Height = 480;
    const double PixelTolerance = 0.1, DegreeTolerance = 0.05;

    /// <summary>
    /// Aligns a copy of the scene that needs <paramref name="truth"/> to lie on the reference,
    /// and says how far from it the pose found puts the corners of the image, in pixels.
    /// </summary>
    static (AlignResult Result, double CornerError) Run(AlignPose truth, AlignModel model = AlignModel.ShiftRotationScale, bool fine = false,
        AlignPose start = default, PixelLayout referenceLayout = PixelLayout.Gray8, PixelLayout movingLayout = PixelLayout.Gray8,
        int movingWidth = Width, int movingHeight = Height, double placeX = 0, double placeY = 0, double gain = 1, double offset = 0, double noise = 0)
    {
        var scene = new Scene(7, Width, Height);
        using DecodedImage reference = scene.Render(Width, Height, Affine2.Identity, referenceLayout, noiseSeed: 1, noise: noise);
        // The moved image lies on the canvas at its placement, then under the pose: so its pixel q shows the scene at pose(placement(q)).
        Affine2 placement = Affine2.ScaleTranslate(1, 1, placeX, placeY);
        double pivotX = placeX + movingWidth / 2.0, pivotY = placeY + movingHeight / 2.0;
        Affine2 toScene = truth.About(pivotX, pivotY).After(placement);
        using DecodedImage moving = scene.Render(movingWidth, movingHeight, toScene, movingLayout, gain, offset, noiseSeed: 2, noise: noise);
        using var referenceLuma = new LumaPyramid(reference);
        using var movingLuma = new LumaPyramid(moving);

        AlignResult result = Registration.Align(new AlignInput(referenceLuma, Affine2.Identity), new AlignInput(movingLuma, placement), start, model, fine)!;
        Affine2 found = result.Pose.About(pivotX, pivotY), wanted = truth.About(pivotX, pivotY);
        double error = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            double x = placeX + (corner & 1) * movingWidth, y = placeY + (corner >> 1) * movingHeight;
            (double fx, double fy) = found.Apply(x, y);
            (double wx, double wy) = wanted.Apply(x, y);
            error = Math.Max(error, Math.Sqrt((fx - wx) * (fx - wx) + (fy - wy) * (fy - wy)));
        }
        return (result, error);
    }

    static void AssertFound(AlignPose truth, AlignResult result, double cornerError)
    {
        string found = $"found {result.Pose}, wanted {truth}; corners off by {cornerError:0.000} px; {result.Patches} patches, confidence {result.Confidence:0.00}";
        Assert.True(result.Found, found);
        Assert.True(cornerError < PixelTolerance, found);
        Assert.True(Math.Abs(result.Pose.Rotation - truth.Rotation) < DegreeTolerance, found);
        Assert.True(result.ScoreAfter > 0.99, $"score after {result.ScoreAfter:0.0000}; {found}");
        Assert.True(result.ScoreAfter >= result.ScoreBefore, found);
    }

    [Theory]
    [InlineData(7, -4, 0, 1)]
    [InlineData(3.4, -2.75, 0, 1)]
    [InlineData(-41.3, 26.6, 0, 1)]
    [InlineData(5.2, 3.1, 1.3, 1)]
    [InlineData(0, 0, -0.4, 1)]
    [InlineData(-8.5, 12.25, 12, 1)]
    [InlineData(4, -6, -3, 1.04)]
    [InlineData(2.5, 1.5, 0, 0.97)]
    [InlineData(20, -15, 33, 0.9)]
    [InlineData(-10, 5, -58, 1.12)]
    public void AKnownMoveTurnAndScalingIsRecovered(double dx, double dy, double rotation, double scale)
    {
        var truth = new AlignPose(dx, dy, rotation, scale);
        (AlignResult result, double error) = Run(truth);
        AssertFound(truth, result, error);
    }

    [Fact]
    public void WholePixelsComeBackAsWholePixels()
    {
        (AlignResult result, _) = Run(new AlignPose(7, -4));
        Assert.Equal(new AlignPose(7, -4), result.Pose); // exactly: such a pair is then compared pixel for pixel
        Assert.True(result.ScoreBefore < 0.9);
        Assert.True(result.ScoreAfter > 0.9999);
    }

    [Fact]
    public void APoseAHairFromWholePixelsIsTakenToBeWholePixels()
    {
        // Within the tenth of a pixel the alignment is good for; and whole pixels are worth having.
        (AlignResult result, double error) = Run(new AlignPose(3.04, -2.03));
        Assert.Equal(new AlignPose(3, -2), result.Pose);
        Assert.True(error < PixelTolerance);
    }

    [Fact]
    public void AFractionOfAPixelStaysAFraction()
    {
        (AlignResult result, double error) = Run(new AlignPose(3.2, -2));
        Assert.InRange(result.Pose.Dx, 3.17, 3.23);
        Assert.InRange(result.Pose.Dy, -2.03, -1.97);
        Assert.True(error < PixelTolerance);
    }

    [Fact]
    public void ImagesThatAlreadyLieOnEachOtherStayWhereTheyAre()
    {
        (AlignResult result, _) = Run(AlignPose.Identity);
        Assert.Equal(AlignPose.Identity, result.Pose);
        Assert.Equal(1, result.ScoreAfter, 6);
    }

    [Fact]
    public void HalfATurnIsFound()
    {
        var truth = new AlignPose(6, -3, 180, 1);
        (AlignResult result, double error) = Run(truth);
        Assert.True(error < PixelTolerance, $"found {result.Pose}");
        Assert.True(Math.Abs(Math.Abs(result.Pose.Rotation) - 180) < DegreeTolerance);
    }

    [Fact]
    public void AModelThatOnlyMovesDoesNotTurnOrScale()
    {
        var truth = new AlignPose(9.3, -5.6);
        (AlignResult result, double error) = Run(truth, AlignModel.Shift);
        AssertFound(truth, result, error);
        Assert.Equal(0, result.Pose.Rotation);
        Assert.Equal(1, result.Pose.Scale);
    }

    [Fact]
    public void AModelThatMovesAndTurnsDoesNotScale()
    {
        var truth = new AlignPose(-4.2, 7.7, 2.4);
        (AlignResult result, double error) = Run(truth, AlignModel.ShiftRotation);
        AssertFound(truth, result, error);
        Assert.Equal(1, result.Pose.Scale);
    }

    [Fact]
    public void FineAlignmentRefinesAPoseSetByHand()
    {
        var truth = new AlignPose(26.3, -14.2, 0.8, 1.01);
        (AlignResult result, double error) = Run(truth, fine: true, start: new AlignPose(22, -11, 0.5));
        AssertFound(truth, result, error);
    }

    [Theory]
    [InlineData(32, 0, 0)]       // a twentieth of the width
    [InlineData(-22, 24, 0)]
    [InlineData(10, -8, 2.5)]    // and a turn that moves the corners by 17 pixels
    public void FineAlignmentReachesAboutATwentiethOfTheImage(double offX, double offY, double offTurn)
    {
        var truth = new AlignPose(6.5, -3.25, 1, 1);
        (AlignResult result, double error) = Run(truth, fine: true, start: new AlignPose(truth.Dx + offX, truth.Dy + offY, truth.Rotation + offTurn));
        AssertFound(truth, result, error);
    }

    [Fact]
    public void FineAlignmentKeepsATurnTheModelDoesNotLetItChange()
    {
        // Turned by hand, exactly right; only the move is left to find.
        var truth = new AlignPose(11.4, 6.8, 5);
        (AlignResult result, double error) = Run(truth, AlignModel.Shift, fine: true, start: new AlignPose(8, 9, 5));
        AssertFound(truth, result, error);
        Assert.Equal(5, result.Pose.Rotation);
    }

    [Theory]
    [InlineData(PixelLayout.GrayFloat, PixelLayout.GrayFloat)]
    [InlineData(PixelLayout.Bgra8, PixelLayout.Bgra8)]
    [InlineData(PixelLayout.Gray16, PixelLayout.Bgra8)]
    [InlineData(PixelLayout.Rgba16, PixelLayout.Gray8)]
    public void AnyLayoutAlignsWithAnyOther(PixelLayout referenceLayout, PixelLayout movingLayout)
    {
        var truth = new AlignPose(-6.4, 3.9, 1.7, 1.02);
        (AlignResult result, double error) = Run(truth, referenceLayout: referenceLayout, movingLayout: movingLayout);
        AssertFound(truth, result, error);
    }

    [Fact]
    public void ASmallerImagePlacedInTheMiddleIsAligned()
    {
        var truth = new AlignPose(5.5, -3.25, -2, 1);
        (AlignResult result, double error) = Run(truth, movingWidth: 400, movingHeight: 320, placeX: 120, placeY: 80);
        AssertFound(truth, result, error);
    }

    [Fact]
    public void BrighterWithLessContrastAndNoisyStillAligns()
    {
        var truth = new AlignPose(4.6, 2.2, 0.9, 1);
        (AlignResult result, double error) = Run(truth, gain: 0.7, offset: 25, noise: 4);
        Assert.True(result.Found);
        Assert.True(error < PixelTolerance, $"found {result.Pose}; corners off by {error:0.000} px");
        Assert.True(Math.Abs(result.Pose.Rotation - truth.Rotation) < DegreeTolerance);
        Assert.True(result.ScoreAfter > result.ScoreBefore);
    }

    /// <summary>
    /// One image in which two things moved differently: left of the middle it shows the scene as
    /// one pose needs it, right of the middle as another does. No pose is right for all of it.
    /// </summary>
    static DecodedImage TwoHalves(Scene scene, AlignPose left, AlignPose right)
    {
        DecodedImage image = scene.Render(Width, Height, left.About(Width / 2.0, Height / 2.0));
        using DecodedImage other = scene.Render(Width, Height, right.About(Width / 2.0, Height / 2.0));
        for (int y = 0; y < Height; y++)
            Buffer.MemoryCopy(other.Pixels + (long)y * other.Stride + Width / 2, image.Pixels + (long)y * image.Stride + Width / 2, Width / 2, Width / 2);
        return image;
    }

    /// <summary>How far the pose found puts the corners of a region from where the true pose puts them.</summary>
    static double RegionError(AlignPose found, AlignPose truth, CanvasRect region)
    {
        Affine2 a = found.About(Width / 2.0, Height / 2.0), b = truth.About(Width / 2.0, Height / 2.0);
        double error = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            double x = region.X + (corner & 1) * region.Width, y = region.Y + (corner >> 1) * region.Height;
            (double ax, double ay) = a.Apply(x, y);
            (double bx, double by) = b.Apply(x, y);
            error = Math.Max(error, Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
        }
        return error;
    }

    static readonly CanvasRect LeftPart = new(20, 20, 270, 440), RightPart = new(350, 20, 270, 440);

    [Theory]
    [InlineData(AlignModel.Shift, 6.4, -3.2, 0, 1, -9.6, 5.5, 0, 1)]
    [InlineData(AlignModel.ShiftRotationScale, 6.4, -3.2, 0, 1, -9.6, 5.5, 0, 1)]
    [InlineData(AlignModel.ShiftRotationScale, 5.5, 2.25, 1.5, 1.01, -4, -6.5, -2, 0.985)]
    public void ARegionDecidesWhichPartOfTheImageIsGoneBy(AlignModel model, double lx, double ly, double lr, double ls, double rx, double ry, double rr, double rs)
    {
        var scene = new Scene(7, Width, Height);
        AlignPose left = new(lx, ly, lr, ls), right = new(rx, ry, rr, rs);
        using DecodedImage reference = scene.Render(Width, Height, Affine2.Identity);
        using DecodedImage moving = TwoHalves(scene, left, right);
        using var referenceLuma = new LumaPyramid(reference);
        using var movingLuma = new LumaPyramid(moving);
        var a = new AlignInput(referenceLuma, Affine2.Identity);
        var b = new AlignInput(movingLuma, Affine2.Identity);

        AlignResult byLeft = Registration.Align(a, b, default, model, fine: false, region: LeftPart)!;
        AlignResult byRight = Registration.Align(a, b, default, model, fine: false, region: RightPart)!;
        Assert.True(byLeft.Found && byRight.Found);
        Assert.True(RegionError(byLeft.Pose, left, LeftPart) < PixelTolerance, $"by the left part: {byLeft.Pose}, wanted {left}");
        Assert.True(RegionError(byRight.Pose, right, RightPart) < PixelTolerance, $"by the right part: {byRight.Pose}, wanted {right}");
        // The scores are of the region: each alignment is right where it looked, and says so.
        Assert.True(byLeft.ScoreAfter > 0.99 && byRight.ScoreAfter > 0.99, $"{byLeft.ScoreAfter}, {byRight.ScoreAfter}");
        Assert.True(byLeft.ScoreAfter > byLeft.ScoreBefore && byRight.ScoreAfter > byRight.ScoreBefore);

        // Under the left part's pose the left part is the same picture and the right part is not.
        var underLeft = b with { ToCanvas = byLeft.Pose.About(Width / 2.0, Height / 2.0) };
        Assert.True(Registration.Score(a, underLeft, LeftPart) > 0.99);
        Assert.True(Registration.Score(a, underLeft, RightPart) < 0.95);
        Assert.True(Registration.Score(a, underLeft) < Registration.Score(a, underLeft, LeftPart));
    }

    [Theory]
    [InlineData(60, 0)]      // a fifth of the region's width
    [InlineData(-35, 45)]
    public void ARegionMakesUpForAboutAQuarterOfItsSize(double dx, double dy)
    {
        var truth = new AlignPose(dx, dy);
        var scene = new Scene(7, Width, Height);
        using DecodedImage reference = scene.Render(Width, Height, Affine2.Identity);
        using DecodedImage moving = scene.Render(Width, Height, truth.About(Width / 2.0, Height / 2.0));
        using var referenceLuma = new LumaPyramid(reference);
        using var movingLuma = new LumaPyramid(moving);
        var region = new CanvasRect(170, 100, 300, 280);
        AlignResult result = Registration.Align(new AlignInput(referenceLuma, Affine2.Identity), new AlignInput(movingLuma, Affine2.Identity), default,
            AlignModel.ShiftRotationScale, fine: false, region: region)!;
        Assert.True(result.Found);
        Assert.True(RegionError(result.Pose, truth, region) < PixelTolerance, $"found {result.Pose}, wanted {truth}");
    }

    [Fact]
    public void FineAlignmentByARegionRefinesThePoseThere()
    {
        var scene = new Scene(7, Width, Height);
        AlignPose left = new(7.3, -4.6, 0.8), right = new(-12, 9, -3);
        using DecodedImage reference = scene.Render(Width, Height, Affine2.Identity);
        using DecodedImage moving = TwoHalves(scene, left, right);
        using var referenceLuma = new LumaPyramid(reference);
        using var movingLuma = new LumaPyramid(moving);
        AlignResult result = Registration.Align(new AlignInput(referenceLuma, Affine2.Identity), new AlignInput(movingLuma, Affine2.Identity),
            new AlignPose(5, -3, 0.5), AlignModel.ShiftRotation, fine: true, region: LeftPart)!;
        Assert.True(RegionError(result.Pose, left, LeftPart) < PixelTolerance, $"found {result.Pose}, wanted {left}");
        Assert.Equal(1, result.Pose.Scale);
    }

    [Fact]
    public void ARegionTooSmallOrBesideTheImagesFindsNothing()
    {
        var scene = new Scene(7, Width, Height);
        using DecodedImage reference = scene.Render(Width, Height, Affine2.Identity);
        using DecodedImage moving = scene.Render(Width, Height, new AlignPose(4, 2).About(Width / 2.0, Height / 2.0));
        using var referenceLuma = new LumaPyramid(reference);
        using var movingLuma = new LumaPyramid(moving);
        var a = new AlignInput(referenceLuma, Affine2.Identity);
        var b = new AlignInput(movingLuma, Affine2.Identity);

        AlignResult beside = Registration.Align(a, b, default, AlignModel.Shift, fine: false, region: new CanvasRect(2000, 2000, 300, 300))!;
        Assert.False(beside.Found);
        Assert.True(double.IsNaN(beside.ScoreAfter));
        Assert.Equal(AlignPose.Identity, beside.Pose);

        AlignResult tiny = Registration.Align(a, b, new AlignPose(1, 1), AlignModel.Shift, fine: true, region: new CanvasRect(300, 200, 9, 9))!;
        Assert.Equal(0, tiny.Patches); // nine pixels hold no patch: the pose stays what it was
        Assert.Equal(new AlignPose(1, 1), tiny.Pose);
    }

    [Fact]
    public void UnrelatedImagesDoNotScoreBetterForBeingAligned()
    {
        using DecodedImage a = new Scene(1, Width, Height).Render(Width, Height, Affine2.Identity);
        using DecodedImage b = new Scene(2, Width, Height).Render(Width, Height, Affine2.Identity);
        using var la = new LumaPyramid(a);
        using var lb = new LumaPyramid(b);
        AlignResult result = Registration.Align(new AlignInput(la, Affine2.Identity), new AlignInput(lb, Affine2.Identity), default, AlignModel.ShiftRotationScale, fine: false)!;
        Assert.True(double.IsNaN(result.ScoreAfter) || result.ScoreAfter < 0.5, $"score {result.ScoreAfter}");
    }

    [Fact]
    public void TheScoreIsOneForTheSamePictureAndIgnoresBrightnessAndContrast()
    {
        var scene = new Scene(7, Width, Height);
        using DecodedImage a = scene.Render(Width, Height, Affine2.Identity, PixelLayout.GrayFloat);
        using DecodedImage b = scene.Render(Width, Height, Affine2.Identity, PixelLayout.GrayFloat, gain: 0.5, offset: 40);
        using DecodedImage other = new Scene(8, Width, Height).Render(Width, Height, Affine2.Identity);
        using var la = new LumaPyramid(a);
        using var lb = new LumaPyramid(b);
        using var lo = new LumaPyramid(other);
        Assert.Equal(1, Registration.Score(new AlignInput(la, Affine2.Identity), new AlignInput(la, Affine2.Identity)), 9);
        Assert.Equal(1, Registration.Score(new AlignInput(la, Affine2.Identity), new AlignInput(lb, Affine2.Identity)), 5);
        Assert.InRange(Registration.Score(new AlignInput(la, Affine2.Identity), new AlignInput(lo, Affine2.Identity)), -0.2, 0.2);
        // Moved away it is less alike; moved off the other image altogether there is nothing to compare.
        Assert.True(Registration.Score(new AlignInput(la, Affine2.Identity), new AlignInput(la, Affine2.ScaleTranslate(1, 1, 9, 0))) < 0.9);
        Assert.True(double.IsNaN(Registration.Score(new AlignInput(la, Affine2.Identity), new AlignInput(la, Affine2.ScaleTranslate(1, 1, 5000, 0)))));
    }

    [Fact]
    public void AFlatImageHasNoScoreAndNothingIsFound()
    {
        using DecodedImage flat = Pixels.Gray8(Width, Height, (_, _) => 128);
        using DecodedImage picture = new Scene(7, Width, Height).Render(Width, Height, Affine2.Identity);
        using var lf = new LumaPyramid(flat);
        using var lp = new LumaPyramid(picture);
        Assert.True(double.IsNaN(Registration.Score(new AlignInput(lp, Affine2.Identity), new AlignInput(lf, Affine2.Identity))));
        AlignResult result = Registration.Align(new AlignInput(lp, Affine2.Identity), new AlignInput(lf, Affine2.Identity), default, AlignModel.ShiftRotationScale, fine: false)!;
        Assert.False(result.Found);
        Assert.Equal(AlignPose.Identity, result.Pose);
    }

    [Fact]
    public void ItCanBeCancelled()
    {
        using DecodedImage picture = new Scene(7, Width, Height).Render(Width, Height, Affine2.Identity);
        using var luma = new LumaPyramid(picture);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Null(Registration.Align(new AlignInput(luma, Affine2.Identity), new AlignInput(luma, Affine2.Identity), default, AlignModel.Shift, fine: false, cancel.Token));
    }
}

/// <summary>What the numbers and the saved files make of an image that has been moved or turned on the canvas.</summary>
public unsafe class AlignedCompareTests
{
    /// <summary>The pixels of a row whose centres an image covers, found the slow way: each one asked in turn.</summary>
    static (int Left, int Right) SlowSpan(DecodedImage image, Affine2 toCanvas, CanvasRect bounds, int y)
    {
        Affine2 toStored = toCanvas.Inverse();
        int left = int.MaxValue, right = int.MinValue;
        for (int x = bounds.X - 2; x < bounds.Right + 2; x++)
        {
            (double sx, double sy) = toStored.Apply(x + 0.5, y + 0.5);
            if (!(sx >= 0 && sx < image.Width && sy >= 0 && sy < image.Height)) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x + 1);
        }
        return left == int.MaxValue ? (0, 0) : (left, right);
    }

    [Theory]
    [InlineData(0, 0, 45, 1)]
    [InlineData(12.5, -7.25, 10, 1.3)]
    [InlineData(-3, 4, -33, 0.7)]
    [InlineData(0.4, 0.3, 0.5, 1)]
    [InlineData(5, 5, 170, 1)]
    public void ATurnedImageCoversOneRunOfPixelsInEachRow(double dx, double dy, double rotation, double scale)
    {
        using DecodedImage image = Pixels.Gray8(60, 40, (_, _) => 1);
        Affine2 toCanvas = new AlignPose(dx, dy, rotation, scale).About(30, 20);
        var source = new CompareSource(image, toCanvas);
        Assert.False(source.Upright);
        Assert.False(source.PixelAligned);
        long covered = 0;
        for (int y = source.Bounds.Y - 3; y < source.Bounds.Bottom + 3; y++)
        {
            source.RowSpan(y, out int left, out int right);
            (int wantLeft, int wantRight) = SlowSpan(image, toCanvas, source.Bounds, y);
            if (wantRight == wantLeft) Assert.Equal(left, right);
            else Assert.Equal((wantLeft, wantRight), (left, right));
            covered += right - left;
            Assert.True(right == left || (left >= source.Bounds.X && right <= source.Bounds.Right), "inside its bounds");
        }
        // As many pixels as the image has, as far as counting pixel centres can tell.
        Assert.InRange(covered, 60 * 40 * scale * scale * 0.97, 60 * 40 * scale * scale * 1.03);
    }

    [Fact]
    public void AMoveByWholePixelsAndAQuarterTurnKeepPixelsWhole()
    {
        using DecodedImage image = Pixels.Gray8(60, 40, (x, y) => (byte)(x + 2 * y));
        var moved = new CompareSource(image, new AlignPose(3, -2).About(30, 20));
        Assert.True(moved.Upright && moved.PixelAligned);
        Assert.Equal(new CanvasRect(3, -2, 60, 40), moved.Bounds);
        moved.RowSpan(0, out int left, out int right);
        Assert.Equal((3, 63), (left, right));
        moved.RowSpan(38, out left, out right);
        Assert.Equal(left, right); // below the image

        var turned = new CompareSource(image, new AlignPose(0, 0, 90).About(30, 20));
        Assert.True(turned.Upright && turned.PixelAligned);
        Assert.Equal(new CanvasRect(10, -10, 40, 60), turned.Bounds);
        float* r = stackalloc float[4], g = stackalloc float[4], b = stackalloc float[4], a = stackalloc float[4];
        // Turned clockwise, the image's left edge is on top and its bottom edge on the left: canvas (10, -10) is its pixel (0, 39).
        turned.ReadRow(-10, 10, 1, r, g, b, a, 1);
        Assert.Equal(0 + 2 * 39, r[0]);
        turned.ReadRow(-9, 49, 1, r, g, b, a, 1);
        Assert.Equal(1 + 2 * 0, r[0]);
    }

    [Fact]
    public void BetweenPixelsTheFourNearestAreBlended()
    {
        // A plane: the blend of four neighbours of a plane is the plane itself, at any position and angle.
        using DecodedImage image = Pixels.Float(80, 60, (x, y) => 3 * x + 7 * y);
        Affine2 toCanvas = new AlignPose(2.3, -1.7, 12, 1.1).About(40, 30);
        var source = new CompareSource(image, toCanvas);
        Affine2 toStored = toCanvas.Inverse();
        float* r = stackalloc float[16], g = stackalloc float[16], b = stackalloc float[16], a = stackalloc float[16];
        foreach (int y in new[] { 10, 30, 47 })
        {
            source.ReadRow(y, 25, 16, r, g, b, a, 1);
            for (int i = 0; i < 16; i++)
            {
                (double sx, double sy) = toStored.Apply(25 + i + 0.5, y + 0.5);
                Assert.Equal(3 * (sx - 0.5) + 7 * (sy - 0.5), r[i], 2); // the value of pixel k is at k + 0.5
                Assert.Equal(1f, a[i], 1e-5f);
            }
        }
    }

    [Fact]
    public void AMovedCopyComparedUnderItsMoveIsTheSameImage()
    {
        // The second image is the first one seen 3 to the right and 2 up: laid back by (3, -2) they agree in every pixel.
        using DecodedImage first = Pixels.Bgra(90, 70, (x, y) => (Pixels.Texture(x, y), Pixels.Texture(y, x), (byte)(x + y), 255));
        using DecodedImage second = Pixels.Bgra(90, 70, (x, y) => (Pixels.Texture(x + 3, y - 2), Pixels.Texture(y - 2, x + 3), (byte)(x + 3 + y - 2), 255));
        var a = new CompareSource(first, Affine2.Identity);
        MetricsResult apart = CompareMetrics.Compute(a, new CompareSource(second, Affine2.Identity), null, true, false)!;
        MetricsResult aligned = CompareMetrics.Compute(a, new CompareSource(second, new AlignPose(3, -2).About(45, 35)), null, true, false)!;
        Assert.True(apart.Main!.MaxError > 20);
        Assert.Equal(0, aligned.Main!.MaxError);
        Assert.True(double.IsPositiveInfinity(aligned.Main.Psnr));
        Assert.Equal(1, aligned.Main.Ssim, 9);
        Assert.True(aligned.PixelAligned);
        Assert.True(aligned.Main.Histogram.Exact);
        Assert.Equal(new CanvasRect(3, 0, 87, 68), aligned.Area);
        Assert.Equal(87 * 68, aligned.Pixels);
    }

    [Fact]
    public void TheNumbersOfATurnedPairAreOfThePixelsBothCover()
    {
        using DecodedImage first = Pixels.Gray8(100, 100, (_, _) => 100), second = Pixels.Gray8(100, 100, (_, _) => 110);
        Affine2 turn = new AlignPose(0, 0, 45).About(50, 50);
        var a = new CompareSource(first, Affine2.Identity);
        var b = new CompareSource(second, turn);
        MetricsResult result = CompareMetrics.Compute(a, b, null, false, false)!;
        long expected = 0;
        for (int y = 0; y < 100; y++)
        {
            (int left, int right) = SlowSpan(second, turn, b.Bounds, y);
            expected += Math.Max(0, Math.Min(right, 100) - Math.Max(left, 0));
        }
        Assert.Equal(expected, result.Pixels);
        Assert.InRange(result.Pixels, 8200, 8400); // a square of 100 less the four corners a diamond leaves: 10000 - 4 * 20.7^2 / 2 * 2
        Assert.Equal(new CanvasRect(0, 0, 100, 100), result.Area);
        Assert.Equal(10, result.Main!.Mae, 3); // not to the last digit: the turned image's pixels are blends, in single precision
        Assert.Equal(10, result.Main.MaxError, 3);
        Assert.False(result.PixelAligned);
    }

    [Fact]
    public void TurnedApartTheyHaveNothingInCommon()
    {
        // Two thin strips crossing far from each other: their rectangles overlap, they do not.
        using DecodedImage first = Pixels.Gray8(200, 10, (_, _) => 1), second = Pixels.Gray8(200, 10, (_, _) => 2);
        var a = new CompareSource(first, new AlignPose(0, 0, 45).About(100, 5));
        var b = new CompareSource(second, new AlignPose(0, 60, 45).About(100, 5));
        Assert.False(a.Bounds.Intersect(b.Bounds).IsEmpty);
        MetricsResult result = CompareMetrics.Compute(a, b, null, true, false)!;
        Assert.Equal(0, result.Pixels);
        Assert.Empty(result.Channels);
        Assert.Null(result.Main);
    }

    [Fact]
    public void SimilarityOfATurnedPairIsTakenWhereItsWindowFitsAndTheMapIsBlankElsewhere()
    {
        var scene = new Scene(21, 240, 200);
        Affine2 pose = new AlignPose(4.5, -3.25, 14, 1).About(120, 100);
        using DecodedImage first = scene.Render(240, 200, Affine2.Identity, PixelLayout.GrayFloat);
        using DecodedImage second = scene.Render(240, 200, pose, PixelLayout.GrayFloat);
        var a = new CompareSource(first, Affine2.Identity);
        MetricsResult apart = CompareMetrics.Compute(a, new CompareSource(second, Affine2.Identity), null, true, false)!;
        var b = new CompareSource(second, pose);
        MetricsResult aligned = CompareMetrics.Compute(a, b, null, true, ssimMap: true)!;
        using DecodedImage map = aligned.SsimMap!;
        Assert.True(apart.Main!.Ssim < 0.6, $"apart {apart.Main.Ssim}");
        Assert.True(aligned.Main!.Ssim > 0.985, $"aligned {aligned.Main.Ssim}"); // not 1: the turned image is blended from four pixels
        Assert.True(aligned.Pixels < aligned.Area.Area);
        Assert.Equal(aligned.Area, aligned.SsimArea);

        // The top left corner of the first image is one the turned second does not reach.
        b.RowSpan(aligned.Area.Y + 2, out int left, out _);
        Assert.True(left > aligned.Area.X + 5);
        Assert.Equal(1f, Pixels.FloatAt(map, 2, 2));
        Assert.Equal(1f, Pixels.FloatAt(map, map.Width - 3, map.Height - 3));
        Assert.InRange(Pixels.FloatAt(map, map.Width / 2, map.Height / 2), 0.9f, 1f);
    }

    [Fact]
    public void TheAlignedImageSavedByWholePixelsIsItsPixelsMoved()
    {
        using DecodedImage image = Pixels.Bgra(20, 12, (x, y) => ((byte)(x * 10), (byte)(y * 20), (byte)(x + y), 255));
        using DecodedImage aligned = CompareExport.Resampled(new CompareSource(image, new AlignPose(3, -2).About(10, 6)), new CanvasRect(0, 0, 20, 12), out bool complete);
        Assert.False(complete);
        Assert.Equal((20, 12, PixelLayout.Bgra8), (aligned.Width, aligned.Height, aligned.Layout));
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool inside = x >= 3 && y < 10;
                Assert.Equal(inside ? Pixels.At(image, x - 3, y + 2) : ((byte)0, (byte)0, (byte)0, (byte)0), Pixels.At(aligned, x, y));
            }
        }
        using DecodedImage same = CompareExport.Resampled(new CompareSource(image, Affine2.Identity), new CanvasRect(0, 0, 20, 12), out complete);
        Assert.True(complete);
        Assert.True(new ReadOnlySpan<byte>(image.Pixels, (int)image.ByteCount).SequenceEqual(new ReadOnlySpan<byte>(same.Pixels, (int)same.ByteCount)));
    }

    [Fact]
    public void TheAlignedImageKeepsItsDepth()
    {
        using DecodedImage deep = Pixels.Gray16(30, 20, (x, y) => (ushort)(1000 * x + y));
        using DecodedImage half = CompareExport.Resampled(new CompareSource(deep, new AlignPose(0.5, 0).About(15, 10)), new CanvasRect(0, 0, 30, 20), out _);
        Assert.Equal(PixelLayout.Gray16, half.Layout);
        Assert.Equal(1000 * 9 + 500 + 7, ((ushort*)(half.Pixels + 7 * half.Stride))[10]); // half way between its pixels 9 and 10
        Assert.Equal(0, ((ushort*)half.Pixels)[0]); // only half covered: the image's edge is half a pixel in

        using DecodedImage floats = Pixels.Float(30, 20, (x, y) => x * 0.25f - y);
        Affine2 turn = new AlignPose(0, 0, 30).About(15, 10);
        using DecodedImage turned = CompareExport.Resampled(new CompareSource(floats, turn), new CanvasRect(0, 0, 30, 20), out bool complete);
        Assert.Equal(PixelLayout.GrayFloat, turned.Layout);
        Assert.False(complete);
        Assert.True(float.IsNaN(Pixels.FloatAt(turned, 0, 0)));
        // The values are a plane, of which any blend is the plane again: at the place the pixel's centre comes from.
        (double sx, double sy) = turn.Inverse().Apply(15.5, 10.5);
        Assert.Equal((sx - 0.5) * 0.25 - (sy - 0.5), Pixels.FloatAt(turned, 15, 10), 3);

        using var colour = new DecodedImage(8, 8, PixelLayout.Rgba16, 8, 8);
        for (int i = 0; i < 8 * 8 * 4; i++) ((ushort*)colour.Pixels)[i] = (ushort)(i * 257);
        using DecodedImage moved = CompareExport.Resampled(new CompareSource(colour, new AlignPose(1, 1).About(4, 4)), new CanvasRect(0, 0, 8, 8), out _);
        Assert.Equal(PixelLayout.Rgba16, moved.Layout);
        Assert.Equal(0, ((ushort*)moved.Pixels)[3]); // no pixel: transparent
        for (int c = 0; c < 4; c++) Assert.Equal(((ushort*)colour.Pixels)[c], ((ushort*)(moved.Pixels + moved.Stride))[4 + c]);
    }

    [Fact]
    public void SavedViewsOfATurnedPairAreEmptyWhereOneImageIsNot()
    {
        using DecodedImage first = Pixels.Gray8(60, 60, (_, _) => 100), second = Pixels.Gray8(60, 60, (_, _) => 130);
        var a = new CompareSource(first, Affine2.Identity);
        var b = new CompareSource(second, new AlignPose(0, 0, 45).About(30, 30));
        using DecodedImage values = CompareExport.DifferenceValues(a, b, 60, 60, DifferenceMath.Colour);
        Assert.True(float.IsNaN(Pixels.FloatAt(values, 1, 1)));
        Assert.Equal(30, Pixels.FloatAt(values, 30, 30), 3);
        Assert.Equal(30, Pixels.FloatAt(values, 30, 1), 3); // the diamond's tip reaches past the top edge

        var view = new DifferenceView { Kind = DifferenceKind.Absolute, Map = Colormap.Gray, Gain = 1, FullScale = 1 };
        using DecodedImage picture = CompareExport.Difference(a, b, 60, 60, view, ToneSettings.Default);
        Assert.Equal(0, Pixels.At(picture, 1, 1).A);
        Assert.Equal(255, Pixels.At(picture, 30, 30).A);
        Assert.Equal(30, Pixels.At(picture, 30, 30).R);
    }
}

public unsafe class AlignWriterTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "fiv_align_" + Guid.NewGuid().ToString("N"));

    public AlignWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASixteenBitColourPngReadsBackAsItWasWritten(bool alpha)
    {
        using var image = new DecodedImage(11, 7, PixelLayout.Rgba16, 11, 7);
        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 11; x++)
            {
                ushort* q = (ushort*)(image.Pixels + (long)y * image.Stride) + x * 4;
                q[0] = (ushort)(x * 5000 + 3);
                q[1] = (ushort)(y * 9000 + 1);
                q[2] = (ushort)(x * y * 700);
                q[3] = (ushort)(alpha ? 65535 - x * 1000 : 65535);
            }
        }
        string path = Path.Combine(_folder, "c.png");
        ImageWriter.WritePng(path, image, alpha);
        byte[] bytes = File.ReadAllBytes(path);
        DecodedImage back;
        fixed (byte* data = bytes) back = WicDecoder.Decode(data, (nuint)bytes.Length);
        using (back)
        {
            Assert.Equal((11, 7, PixelLayout.Rgba16), (back.Width, back.Height, back.Layout));
            Assert.True(new ReadOnlySpan<byte>(image.Pixels, (int)image.ByteCount).SequenceEqual(new ReadOnlySpan<byte>(back.Pixels, (int)back.ByteCount)));
        }
    }

    [Fact]
    public void WhatTheAlignmentMayDoIsSavedAndLoaded()
    {
        string file = Path.Combine(_folder, "settings.json");
        Assert.Equal(AlignModel.ShiftRotationScale, Settings.Load(file).AlignModel);
        new Settings { AlignModel = AlignModel.Shift }.Save(file);
        Assert.Equal(AlignModel.Shift, Settings.Load(file).AlignModel);
        File.WriteAllText(file, """{ "alignModel": "Warp" }""");
        Assert.Equal(AlignModel.ShiftRotationScale, Settings.Load(file).AlignModel);
    }
}
