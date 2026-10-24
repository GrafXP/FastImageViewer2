using System.Numerics;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Surface;
using Xunit;

namespace Fiv.Tests;

public unsafe class HeightFieldTests
{
    static float At(DecodedImage grid, int x, int y) => Pixels.FloatAt(grid, x, y);

    [Fact]
    public void AnImageWithinTheSizeIsTakenValueForValue()
    {
        using DecodedImage image = Pixels.Gray8(5, 3, (x, y) => (byte)(x * 50 + y));
        using DecodedImage grid = HeightField.Build(image, HeightSource.Brightness, smoothing: 0)!;
        Assert.Equal((5, 3, PixelLayout.GrayFloat, 5, 3), (grid.Width, grid.Height, grid.Layout, grid.SourceWidth, grid.SourceHeight));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++) Assert.Equal((x * 50 + y) / 255f, At(grid, x, y), 6);
    }

    [Theory]
    [InlineData(HeightSource.Brightness, 0.2126 * 200 + 0.7152 * 100 + 0.0722 * 50)]
    [InlineData(HeightSource.Red, 200)]
    [InlineData(HeightSource.Green, 100)]
    [InlineData(HeightSource.Blue, 50)]
    [InlineData(HeightSource.Alpha, 25)]
    public void AColourPixelGivesTheValueAskedFor(HeightSource source, double expected)
    {
        using DecodedImage image = Pixels.Bgra(2, 2, (_, _) => (200, 100, 50, 25));
        using DecodedImage grid = HeightField.Build(image, source, smoothing: 0)!;
        Assert.Equal(expected / 255, At(grid, 1, 1), 5);
    }

    [Fact]
    public void SixteenBitColourIsInTheUnitsTheGpuSamples()
    {
        using var image = new DecodedImage(1, 1, PixelLayout.Rgba16, 1, 1);
        ushort* p = (ushort*)image.Pixels;
        (p[0], p[1], p[2], p[3]) = (65535, 0, 0, 65535);
        using DecodedImage grid = HeightField.Build(image, HeightSource.Red, smoothing: 0)!;
        Assert.Equal(1f, At(grid, 0, 0), 6);
        using DecodedImage gray = Pixels.Gray16(1, 1, (_, _) => 32768);
        using DecodedImage grayGrid = HeightField.Build(gray, HeightSource.Brightness, smoothing: 0)!;
        Assert.Equal(32768 / 65535f, At(grayGrid, 0, 0), 6);
    }

    [Fact]
    public void AGreyImageIsOpaque()
    {
        using DecodedImage image = Pixels.Gray8(2, 1, (_, _) => 10);
        using DecodedImage grid = HeightField.Build(image, HeightSource.Alpha, smoothing: 0)!;
        Assert.Equal(1f, At(grid, 0, 0));
    }

    [Fact]
    public void ALargeImageIsAveragedDownAndKeepsItsSize()
    {
        // 8 x 4 into at most 4 on the long side: each cell is a block of 2 x 2.
        using DecodedImage image = Pixels.Gray8(8, 4, (x, y) => (byte)(x + 10 * y));
        using DecodedImage grid = HeightField.Build(image, HeightSource.Brightness, smoothing: 0, maxSize: 4)!;
        Assert.Equal((4, 2, 8, 4), (grid.Width, grid.Height, grid.SourceWidth, grid.SourceHeight));
        Assert.Equal((0 + 1 + 10 + 11) / 4f / 255, At(grid, 0, 0), 6);
        Assert.Equal((26 + 27 + 36 + 37) / 4f / 255, At(grid, 3, 1), 6);
    }

    [Fact]
    public void APreviewLiesOverTheWholeImage()
    {
        using var preview = new DecodedImage(4, 2, PixelLayout.Gray8, 4000, 2000);
        new Span<byte>(preview.Pixels, 8).Fill(128);
        using DecodedImage grid = HeightField.Build(preview, HeightSource.Brightness, smoothing: 0)!;
        Assert.Equal((4, 2, 4000, 2000), (grid.Width, grid.Height, grid.SourceWidth, grid.SourceHeight));
    }

    [Fact]
    public void FloatValuesStayAsTheyAreAndMissingOnesAreLeftOut()
    {
        // The second cell has one finite value of two; the third none, and gets the smallest there is.
        float[] values = [1000.5f, 1002.5f, float.NaN, 999f, float.NaN, float.PositiveInfinity];
        using DecodedImage image = Pixels.Float(6, 1, (x, _) => values[x]);
        using DecodedImage grid = HeightField.Build(image, HeightSource.Brightness, smoothing: 0, maxSize: 3)!;
        Assert.Equal(1001.5f, At(grid, 0, 0), 3);
        Assert.Equal(999f, At(grid, 1, 0), 3);
        Assert.Equal(999f, At(grid, 2, 0), 3);
    }

    [Fact]
    public void SmoothingKeepsAFlatImageFlatAndALevelOnAverage()
    {
        using DecodedImage flat = Pixels.Gray8(40, 30, (_, _) => 77);
        using DecodedImage flatGrid = HeightField.Build(flat, HeightSource.Brightness, smoothing: 5)!;
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++) Assert.Equal(77 / 255f, At(flatGrid, x, y), 5);

        // A single raised pixel spreads out evenly, and what it adds is kept (it is far from the edges).
        using DecodedImage spike = Pixels.Gray8(41, 41, (x, y) => x == 20 && y == 20 ? (byte)255 : (byte)0);
        using DecodedImage grid = HeightField.Build(spike, HeightSource.Brightness, smoothing: 3)!;
        double sum = 0;
        for (int y = 0; y < 41; y++)
            for (int x = 0; x < 41; x++) sum += At(grid, x, y);
        Assert.Equal(1, sum, 4);
        Assert.Equal(At(grid, 17, 20), At(grid, 23, 20), 6);
        Assert.Equal(At(grid, 20, 17), At(grid, 17, 20), 6);
        Assert.True(At(grid, 20, 20) < 0.1f && At(grid, 20, 20) > At(grid, 22, 20));
        Assert.Equal(0, At(grid, 30, 20)); // three blurs of radius 3 reach 9 cells, no further
    }

    [Fact]
    public void ACancelledBuildGivesNothing()
    {
        using DecodedImage image = Pixels.Gray8(64, 64, (x, y) => (byte)(x ^ y));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Null(HeightField.Build(image, HeightSource.Brightness, smoothing: 2, cancel: cancel.Token));
    }
}

public class SurfaceCameraTests
{
    const float ViewWidth = 1600, ViewHeight = 900;

    static void Near(Vector2 expected, Vector2? actual, float tolerance = 0.05f)
    {
        Assert.NotNull(actual);
        Assert.True(Vector2.Distance(expected, actual.Value) < tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void StraightDownTheFlatImageLooksAsInThe2DView()
    {
        // Zoom 0.4 with the image point (1000, 700) at the centre of the view.
        const double zoom = 0.4;
        SurfaceCamera camera = SurfaceCamera.TopDown(1000, 700, zoom, ViewHeight);
        foreach ((float x, float z) in new[] { (1000f, 700f), (0f, 0f), (3000f, 2000f), (250f, 1900f) })
        {
            var expected = new Vector2((float)(ViewWidth / 2 + (x - 1000) * zoom), (float)(ViewHeight / 2 + (z - 700) * zoom));
            Near(expected, camera.Project(new Vector3(x, 0, z), ViewWidth, ViewHeight));
        }
        Assert.True(Vector3.Distance(new Vector3(1, 0, 0), camera.Right) < 1e-6f);
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), camera.Up) < 1e-6f); // the image's upper edge is up
    }

    [Fact]
    public void TheDepthIsReversed()
    {
        SurfaceCamera camera = SurfaceCamera.TopDown(0, 0, 1, ViewHeight);
        Matrix4x4 toClip = camera.WorldToClip(ViewWidth / ViewHeight, 1);
        float Depth(Vector3 p)
        {
            Vector4 clip = Vector4.Transform(new Vector4(p, 1), toClip);
            return clip.Z / clip.W;
        }
        Assert.Equal(1, Depth(camera.Position + camera.Forward), 5); // at the near plane
        Assert.True(Depth(new Vector3(0, 10, 0)) > Depth(Vector3.Zero)); // what stands up is nearer
        Assert.True(Depth(Vector3.Zero) > 0);
    }

    [Fact]
    public void OrbitingTurnsWithTheMouseAndStaysAboveTheGround()
    {
        SurfaceCamera camera = SurfaceCamera.TopDown(0, 0, 1, ViewHeight);
        camera.Orbit(100, 0);
        Assert.True(camera.Yaw < 0); // to the right: the camera goes round to the left
        camera.Orbit(0, -100000);
        Assert.Equal(SurfaceCamera.MinPitch, camera.Pitch);
        camera.Orbit(0, 100000);
        Assert.Equal(SurfaceCamera.MaxPitch, camera.Pitch);
    }

    [Fact]
    public void PanningMovesTheGroundWithTheMouse()
    {
        var camera = new SurfaceCamera { Target = new Vector3(500, 0, 400), Yaw = 0.3f, Pitch = 0.9f, Distance = 2000 };
        Vector3 before = camera.Target;
        camera.Pan(40, 0, ViewHeight);
        Near(new Vector2(ViewWidth / 2 + 40, ViewHeight / 2), camera.Project(before, ViewWidth, ViewHeight));

        SurfaceCamera down = SurfaceCamera.TopDown(500, 400, 0.5, ViewHeight);
        down.Pan(0, 30, ViewHeight);
        Near(new Vector2(ViewWidth / 2, ViewHeight / 2 + 30), down.Project(new Vector3(500, 0, 400), ViewWidth, ViewHeight));
    }

    [Fact]
    public void ZoomingKeepsTheGroundUnderTheMouseWhereItIs()
    {
        var camera = new SurfaceCamera { Target = new Vector3(800, 0, 600), Yaw = 0.35f, Pitch = 0.87f, Distance = 3000 };
        var mouse = new Vector2(1100, 300);
        float ndcX = 2 * mouse.X / ViewWidth - 1, ndcY = 1 - 2 * mouse.Y / ViewHeight;

        // The ground point under the mouse, found by projecting points along the ray.
        float t = MathF.Tan(SurfaceCamera.FieldOfView / 2);
        Vector3 ray = camera.Forward + camera.Right * (ndcX * t * ViewWidth / ViewHeight) + camera.Up * (ndcY * t);
        Vector3 ground = camera.Position + ray * (-camera.Position.Y / ray.Y);
        Near(mouse, camera.Project(ground, ViewWidth, ViewHeight), 0.1f);

        camera.ZoomAt(0.5f, ndcX, ndcY, ViewWidth / ViewHeight);
        Assert.Equal(1500, camera.Distance, 2);
        Near(mouse, camera.Project(ground, ViewWidth, ViewHeight), 0.1f);
    }

    [Fact]
    public void BetweenTwoCamerasTheDistanceChangesByEqualRatios()
    {
        var a = new SurfaceCamera { Target = new Vector3(0, 0, 0), Yaw = 0, Pitch = 1.5f, Distance = 100 };
        var b = new SurfaceCamera { Target = new Vector3(10, 0, 20), Yaw = 1, Pitch = 0.5f, Distance = 400 };
        Assert.Equal(a, SurfaceCamera.Lerp(a, b, 0));
        SurfaceCamera end = SurfaceCamera.Lerp(a, b, 1);
        Assert.Equal((b.Target, b.Yaw, b.Pitch), (end.Target, end.Yaw, end.Pitch));
        Assert.Equal(400, end.Distance, 3);
        SurfaceCamera middle = SurfaceCamera.Lerp(a, b, 0.5f);
        Assert.Equal(200, middle.Distance, 3);
        Assert.Equal(new Vector3(5, 0, 10), middle.Target);
    }

    [Fact]
    public void ATurnRoundAndRoundComesBackTheShortWay()
    {
        var camera = new SurfaceCamera { Yaw = 7f, Pitch = 0.8f, Distance = 10 };
        SurfaceCamera wrapped = camera.WithTurnWrapped();
        Assert.InRange(wrapped.Yaw, -MathF.PI, MathF.PI);
        Assert.True(Vector3.Distance(camera.Forward, wrapped.Forward) < 1e-5f);
    }
}

public class SurfaceSettingsTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "fiv_surface_" + Guid.NewGuid().ToString("N"));

    public SurfaceSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheOptionsOfThe3DViewAreSavedAndLoaded()
    {
        string file = Path.Combine(_folder, "settings.json");
        new Settings
        {
            SurfaceRelief = 0.4f, SurfaceShading = 0.25f, SurfaceSmoothing = 7, SurfaceMesh = 512,
            SurfaceInvert = true, SurfaceWireframe = true, SurfaceByHeight = true, SurfaceMap = Colormap.Turbo,
        }.Save(file);
        Settings back = Settings.Load(file);
        Assert.Equal((0.4f, 0.25f, 7, 512), (back.SurfaceRelief, back.SurfaceShading, back.SurfaceSmoothing, back.SurfaceMesh));
        Assert.Equal((true, true, true, Colormap.Turbo), (back.SurfaceInvert, back.SurfaceWireframe, back.SurfaceByHeight, back.SurfaceMap));

        File.WriteAllText(file, """{ "surfaceRelief": 9, "surfaceSmoothing": -3, "surfaceMesh": 100000, "surfaceMap": "Plasma" }""");
        back = Settings.Load(file);
        Assert.Equal((1f, 0, 2048, Colormap.Viridis), (back.SurfaceRelief, back.SurfaceSmoothing, back.SurfaceMesh, back.SurfaceMap));
    }
}
