using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Fiv.Native;
using Fiv.Scheduling;
using Xunit;

namespace Fiv.Tests;

/// <summary>Small generated images for the tests.</summary>
static unsafe class TestImages
{
    /// <summary>A BGRA gradient: blue rises with x, green with y, red is constant.</summary>
    public static DecodedImage Gradient(int width, int height)
    {
        var image = new DecodedImage(width, height, PixelLayout.Bgra8, width, height);
        for (int y = 0; y < height; y++)
        {
            byte* row = image.Pixels + (long)y * image.Stride;
            for (int x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(x * 255 / Math.Max(1, width - 1));
                row[x * 4 + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                row[x * 4 + 2] = 128;
                row[x * 4 + 3] = 255;
            }
        }
        return image;
    }

    public static byte[] Jpeg(int width, int height, TurboJpeg.Subsampling subsampling = TurboJpeg.Subsampling.S420, bool progressive = false)
    {
        using DecodedImage image = Gradient(width, height);
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
        try
        {
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Quality, 90);
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Subsamp, (int)subsampling);
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Progressive, progressive ? 1 : 0);
            byte* jpeg = null;
            nuint size = 0;
            Assert.Equal(0, TurboJpeg.tj3Compress8(tj, image.Pixels, width, image.Stride, height, TurboJpeg.PixelFormat.Bgra, &jpeg, &size));
            byte[] result = new ReadOnlySpan<byte>(jpeg, (int)size).ToArray();
            TurboJpeg.tj3Free(jpeg);
            return result;
        }
        finally
        {
            TurboJpeg.tj3Destroy(tj);
        }
    }

    /// <summary>
    /// Puts an EXIF block after the SOI marker: the given orientation, optionally an embedded
    /// thumbnail (a JPEG) as cameras write it, and optionally padding segments that push the frame
    /// header far into the file.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, int orientation, int padding = 0, byte[]? thumbnail = null)
    {
        static byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        var tiff = new List<byte>();
        tiff.AddRange([(byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);                               // header, first directory at 8
        tiff.AddRange([0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0, 0, 0]); // one entry: orientation
        if (thumbnail == null)
        {
            tiff.AddRange(Le32(0));                                                // no second directory
        }
        else
        {
            tiff.AddRange(Le32(26));                                               // second directory at 26
            tiff.AddRange([0x02, 0x00]);
            tiff.AddRange([0x01, 0x02, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);       // JPEGInterchangeFormat
            tiff.AddRange(Le32(56));
            tiff.AddRange([0x02, 0x02, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);       // JPEGInterchangeFormatLength
            tiff.AddRange(Le32(thumbnail.Length));
            tiff.AddRange(Le32(0));
            tiff.AddRange(thumbnail);                                              // at 56
        }
        int segmentLength = 2 + 6 + tiff.Count;
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(segmentLength >> 8), (byte)segmentLength };
        app1.AddRange("Exif\0\0"u8);
        app1.AddRange(tiff);
        var result = new List<byte>(jpeg.Length + app1.Count + padding + 8);
        result.AddRange(jpeg.AsSpan(0, 2));
        result.AddRange(app1);
        while (padding > 0)
        {
            int chunk = Math.Min(padding, 60000);
            result.AddRange([0xFF, 0xE5, (byte)((chunk + 2) >> 8), (byte)(chunk + 2)]); // an application segment of filler
            result.AddRange(new byte[chunk]);
            padding -= chunk;
        }
        result.AddRange(jpeg.AsSpan(2));
        return result.ToArray();
    }
}

public unsafe class JpegDecoderTests
{
    [Theory]
    [InlineData(1, 640, 480)]
    [InlineData(2, 320, 240)]
    [InlineData(4, 160, 120)]
    [InlineData(8, 80, 60)]
    public void DecodesAtEveryReduction(int denominator, int width, int height)
    {
        byte[] jpeg = TestImages.Jpeg(640, 480);
        fixed (byte* p = jpeg)
        {
            using DecodedImage image = JpegDecoder.Decode(p, (nuint)jpeg.Length, denominator)!;
            Assert.Equal((width, height), (image.Width, image.Height));
            Assert.Equal((640, 480), (image.SourceWidth, image.SourceHeight));
            Assert.Equal(PixelLayout.Bgra8, image.Layout);
            // The gradient survives: blue low on the left and high on the right, alpha opaque.
            byte* middle = image.Pixels + (long)(height / 2) * image.Stride;
            Assert.InRange(middle[0], 0, 12);
            Assert.InRange(middle[(width - 1) * 4], 243, 255);
            Assert.Equal(255, middle[3]);
            Assert.InRange(middle[(width / 2) * 4 + 2], 120, 136); // red stays at 128
        }
    }

    [Fact]
    public void OddSizesRoundUpWhenReduced()
    {
        byte[] jpeg = TestImages.Jpeg(641, 479);
        fixed (byte* p = jpeg)
        {
            using DecodedImage image = JpegDecoder.Decode(p, (nuint)jpeg.Length, 4)!;
            Assert.Equal((161, 120), (image.Width, image.Height));
        }
    }

    [Fact]
    public void GrayscaleJpegDecodesToOneChannel()
    {
        byte[] jpeg = TestImages.Jpeg(200, 100, TurboJpeg.Subsampling.Gray);
        fixed (byte* p = jpeg)
        {
            using DecodedImage image = JpegDecoder.Decode(p, (nuint)jpeg.Length)!;
            Assert.Equal(PixelLayout.Gray8, image.Layout);
            Assert.Equal(200, image.Stride);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACancelledDecodeReturnsNothing(bool progressive)
    {
        byte[] jpeg = TestImages.Jpeg(2000, 1500, progressive: progressive);
        int cancel = 1;
        fixed (byte* p = jpeg) Assert.Null(JpegDecoder.Decode(p, (nuint)jpeg.Length, 1, JpegDecodeOptions.None, &cancel));
    }

    [Fact]
    public void TruncatedDataStillDecodesWhatIsThere()
    {
        byte[] jpeg = TestImages.Jpeg(640, 480);
        fixed (byte* p = jpeg)
        {
            using DecodedImage? image = JpegDecoder.Decode(p, (nuint)(jpeg.Length / 2));
            Assert.NotNull(image);
            Assert.Equal(640, image.Width);
        }
    }

    [Fact]
    public void GarbageIsRejected()
    {
        byte[] garbage = new byte[1000];
        new Random(1).NextBytes(garbage);
        fixed (byte* p = garbage)
        {
            byte* data = p;
            Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(data, (nuint)garbage.Length));
        }
    }

    [Theory]
    [InlineData(6000, 4000, 1600, 900, 4)]   // 1500x1000 covers the 1350x900 fit; 750x500 would not
    [InlineData(6000, 4000, 700, 400, 8)]
    [InlineData(1000, 800, 1600, 900, 1)]    // smaller than the viewport: no reduction
    [InlineData(4000, 3000, 2160, 1180, 2)]
    public void ChoosesTheLargestReductionThatCovers(int width, int height, int minWidth, int minHeight, int expected)
    {
        double fit = Math.Min(1.0, Math.Min((double)minWidth / width, (double)minHeight / height));
        Assert.Equal(expected, JpegDecoder.ChooseDenominator(width, height, (int)Math.Ceiling(width * fit), (int)Math.Ceiling(height * fit)));
    }
}

public unsafe class OrientationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void ReadsTheOrientationFromExif(int orientation)
    {
        byte[] jpeg = TestImages.WithExif(TestImages.Jpeg(64, 48), orientation);
        fixed (byte* p = jpeg) Assert.Equal((Orientation)orientation, Exif.ReadJpegOrientation(p, (nuint)jpeg.Length));
    }

    [Fact]
    public void NoExifMeansNormal()
    {
        byte[] jpeg = TestImages.Jpeg(64, 48);
        fixed (byte* p = jpeg) Assert.Equal(Orientation.Normal, Exif.ReadJpegOrientation(p, (nuint)jpeg.Length));
    }

    // Where the stored image's corners end up. Stored size 40x30; stored (0,0) is the first pixel of the file.
    [Theory]
    [InlineData(Orientation.Normal, 0, 0, 40, 30)]
    [InlineData(Orientation.MirrorHorizontal, 40, 0, 0, 30)]
    [InlineData(Orientation.Rotate180, 40, 30, 0, 0)]
    [InlineData(Orientation.MirrorVertical, 0, 30, 40, 0)]
    [InlineData(Orientation.Transpose, 0, 0, 30, 40)]
    [InlineData(Orientation.Rotate90, 30, 0, 0, 40)]     // the file's top-left is the upright image's top-right
    [InlineData(Orientation.Transverse, 30, 40, 0, 0)]
    [InlineData(Orientation.Rotate270, 0, 40, 30, 0)]    // the file's top-left is the upright image's bottom-left
    public void MapsStoredCornersToUprightOnes(Orientation orientation, double x0, double y0, double x1, double y1)
    {
        Affine2 m = orientation.ToUpright(40, 30);
        Assert.Equal((x0, y0), m.Apply(0, 0));
        Assert.Equal((x1, y1), m.Apply(40, 30));
        // Every orientation keeps the image inside the upright rectangle.
        (double w, double h) = orientation.SwapsAxes() ? (30.0, 40.0) : (40.0, 30.0);
        foreach ((double x, double y) in new[] { (0.0, 0.0), (40.0, 0.0), (0.0, 30.0), (40.0, 30.0) })
        {
            (double ux, double uy) = m.Apply(x, y);
            Assert.InRange(ux, 0, w);
            Assert.InRange(uy, 0, h);
        }
    }

    [Fact]
    public void InverseUndoesATransform()
    {
        var m = new Affine2(2, 0.5, -1, 3, 10, -4);
        (double x, double y) = m.Inverse().Apply(m.Apply(7, -3).X, m.Apply(7, -3).Y);
        Assert.Equal(7, x, 9);
        Assert.Equal(-3, y, 9);
    }

    [Fact]
    public void AfterComposesInOrder()
    {
        Affine2 scale = Affine2.ScaleTranslate(2, 2, 0, 0), move = Affine2.ScaleTranslate(1, 1, 5, 7);
        Assert.Equal((11.0, 13.0), move.After(scale).Apply(3, 3)); // scale first, then move
        Assert.Equal((16.0, 20.0), scale.After(move).Apply(3, 3)); // move first, then scale
    }
}

public unsafe class MipChainTests
{
    [Fact]
    public void LevelsHalveDownToOnePixel()
    {
        using DecodedImage image = TestImages.Gradient(100, 37);
        using var mips = new MipChain(image);
        Assert.Equal(7, mips.LevelCount); // 100, 50, 25, 12, 6, 3, 1
        Assert.Equal((50, 18), (mips.Width(1), mips.Height(1)));
        Assert.Equal((12, 4), (mips.Width(3), mips.Height(3)));
        Assert.Equal((1, 1), (mips.Width(6), mips.Height(6)));
        Assert.True(mips.Data(0) == image.Pixels);
    }

    [Fact]
    public void EachPixelIsTheAverageOfFour()
    {
        using var image = new DecodedImage(64, 64, PixelLayout.Bgra8, 64, 64);
        var random = new Random(7);
        new Span<byte>(image.Pixels, (int)image.ByteCount).Fill(0);
        for (int i = 0; i < image.ByteCount; i++) image.Pixels[i] = (byte)random.Next(256);
        using var mips = new MipChain(image);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    foreach ((int dx, int dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                        sum += image.Pixels[(2 * y + dy) * image.Stride + (2 * x + dx) * 4 + c];
                    int actual = mips.Data(1)[y * mips.Stride(1) + x * 4 + c];
                    // The vector path rounds twice and may be one above the exact rounded mean.
                    Assert.InRange(actual, (sum + 2) / 4 - 1, (sum + 2) / 4 + 1);
                }
            }
        }
    }

    [Fact]
    public void SixteenBitAndFloatAverageExactly()
    {
        using var gray = new DecodedImage(4, 2, PixelLayout.Gray16, 4, 2);
        ushort[] values = [0, 1000, 60000, 65535, 2, 998, 60002, 65535];
        values.CopyTo(new Span<ushort>(gray.Pixels, 8));
        using var grayMips = new MipChain(gray);
        Assert.Equal(500, ((ushort*)grayMips.Data(1))[0]);
        Assert.Equal(62768, ((ushort*)grayMips.Data(1))[1]);

        using var real = new DecodedImage(2, 2, PixelLayout.GrayFloat, 2, 2);
        new float[] { 1f, 2f, 3f, 6f }.CopyTo(new Span<float>(real.Pixels, 4));
        using var realMips = new MipChain(real);
        Assert.Equal(3f, ((float*)realMips.Data(1))[0]);
    }

    [Fact]
    public void ValueRangeOfHighBitDepthImages()
    {
        using var gray = new DecodedImage(3, 1, PixelLayout.Gray16, 3, 1);
        new ushort[] { 6553, 100, 32768 }.CopyTo(new Span<ushort>(gray.Pixels, 3));
        (float min, float max) = ImageStatistics.ValueRange(gray);
        Assert.Equal(100 / 65535f, min, 6);
        Assert.Equal(32768 / 65535f, max, 6);

        using var real = new DecodedImage(4, 1, PixelLayout.GrayFloat, 4, 1);
        new[] { float.NaN, -2.5f, float.PositiveInfinity, 7f }.CopyTo(new Span<float>(real.Pixels, 4));
        Assert.Equal((-2.5f, 7f), ImageStatistics.ValueRange(real)); // not-a-number and infinity are ignored
    }
}

public class ImageProbeTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("fivprobe").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static void AssertSize(string path, int width, int height, ImageFileFormat format)
    {
        Assert.True(ImageProbe.TryReadSize(path, out int w, out int h, out ImageFileFormat f));
        Assert.Equal((width, height, format), (w, h, f));
    }

    [Fact]
    public void Jpeg() => AssertSize(Write("a.jpg", TestImages.Jpeg(640, 480)), 640, 480, ImageFileFormat.Jpeg);

    [Fact]
    public void ProgressiveJpeg() => AssertSize(Write("p.jpg", TestImages.Jpeg(321, 123, progressive: true)), 321, 123, ImageFileFormat.Jpeg);

    [Fact]
    public void JpegWhoseFrameHeaderLiesFarBehindItsMetadata() =>
        AssertSize(Write("big-exif.jpg", TestImages.WithExif(TestImages.Jpeg(800, 600), 6, padding: 200_000)), 800, 600, ImageFileFormat.Jpeg);

    [Fact]
    public void Png()
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 6000);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 4000);
        AssertSize(Write("a.png", png), 6000, 4000, ImageFileFormat.Png);
    }

    [Fact]
    public void Gif()
    {
        byte[] gif = new byte[13];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 200);
        AssertSize(Write("a.gif", gif), 320, 200, ImageFileFormat.Gif);
    }

    [Fact]
    public void BmpIncludingTopDown()
    {
        byte[] bmp = new byte[54];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1024);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -768);
        AssertSize(Write("a.bmp", bmp), 1024, 768, ImageFileFormat.Bmp);
    }

    [Theory]
    [InlineData(true, 8)]        // directory right after the header
    [InlineData(false, 8)]
    [InlineData(true, 100_000)]  // directory at the end of the file, as many writers put it
    public void Tiff(bool littleEndian, int directoryOffset)
    {
        byte[] tiff = new byte[directoryOffset + 2 + 2 * 12 + 4];
        void U16(int at, ushort v) { if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), v); else BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), v); }
        void U32(int at, uint v) { if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), v); }
        (littleEndian ? "II"u8 : "MM"u8).CopyTo(tiff);
        U16(2, 42);
        U32(4, (uint)directoryOffset);
        U16(directoryOffset, 2);
        int entry = directoryOffset + 2;
        U16(entry, 256); U16(entry + 2, 4); U32(entry + 4, 1); U32(entry + 8, 70000);   // width as LONG
        entry += 12;
        U16(entry, 257); U16(entry + 2, 3); U32(entry + 4, 1); U16(entry + 8, 4321);    // height as SHORT
        AssertSize(Write("a.tif", tiff), 70000, 4321, ImageFileFormat.Tiff);
    }

    [Fact]
    public void WebPInItsThreeFlavours()
    {
        byte[] Riff(string chunk) { byte[] b = new byte[40]; "RIFF"u8.CopyTo(b); "WEBP"u8.CopyTo(b.AsSpan(8)); System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(b, 12); return b; }

        byte[] lossy = Riff("VP8 ");
        lossy[23] = 0x9D; lossy[24] = 0x01; lossy[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(lossy.AsSpan(26), 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(lossy.AsSpan(28), 1080);
        AssertSize(Write("lossy.webp", lossy), 1920, 1080, ImageFileFormat.WebP);

        byte[] lossless = Riff("VP8L");
        lossless[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(lossless.AsSpan(21), (uint)((800 - 1) | ((600 - 1) << 14)));
        AssertSize(Write("lossless.webp", lossless), 800, 600, ImageFileFormat.WebP);

        byte[] extended = Riff("VP8X");
        extended[24] = 0xFF; extended[25] = 0x0F; // width - 1 = 4095
        extended[27] = 0xFF; extended[28] = 0x07; // height - 1 = 2047
        AssertSize(Write("extended.webp", extended), 4096, 2048, ImageFileFormat.WebP);
    }

    [Fact]
    public void UnknownAndMissingFilesAreNotImages()
    {
        Assert.False(ImageProbe.TryReadSize(Write("notes.txt", "hello"u8.ToArray()), out _, out _, out ImageFileFormat format));
        Assert.Equal(ImageFileFormat.Unknown, format);
        Assert.False(ImageProbe.TryReadSize(Write("empty.jpg", []), out _, out _, out _));
        Assert.False(ImageProbe.TryReadSize(Path.Combine(_folder, "missing.jpg"), out _, out _, out _));
    }
}

public unsafe class ImageDecodingTests
{
    [Fact]
    public void ProbeRecognisesJpegByContentAndReadsItsOrientation()
    {
        string folder = Directory.CreateTempSubdirectory("fivdecode").FullName;
        try
        {
            // A JPEG with the wrong extension, and something that is not a JPEG with the right one.
            string jpeg = Path.Combine(folder, "really-a-jpeg.png");
            File.WriteAllBytes(jpeg, TestImages.WithExif(TestImages.Jpeg(800, 600), 6));
            FileBytes file = FileBytes.Read(jpeg);
            try
            {
                ImageInfo info = ImageDecoding.Probe(file);
                Assert.Equal(new ImageInfo(true, 800, 600, Orientation.Rotate90), info);
                // Rotated: a 600x800 picture in a 1000x400 viewport is 300x400; 1/1.5 of that is 267x200, so 1/2 (400x300) is the smallest that covers it.
                Assert.Equal(2, ImageDecoding.PreviewDenominator(info, 1000, 400));
                using DecodedImage preview = ImageDecoding.DecodePreview(file, info, 1000, 400, null)!;
                Assert.Equal((400, 300), (preview.Width, preview.Height));
                using DecodedImage full = ImageDecoding.DecodeFull(file, info, null)!;
                Assert.Equal((800, 600), (full.Width, full.Height));
                // An image no larger than the viewport has no preview.
                Assert.Equal(1, ImageDecoding.PreviewDenominator(info, 4000, 4000));
                Assert.Null(ImageDecoding.DecodePreview(file, info, 4000, 4000, null));
            }
            finally
            {
                file.Release();
            }

            string text = Path.Combine(folder, "not-a-jpeg.jpg");
            File.WriteAllText(text, "this is text");
            file = FileBytes.Read(text);
            try
            {
                Assert.False(ImageDecoding.Probe(file).IsJpeg);
            }
            finally
            {
                file.Release();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MissingFileNamesTheReason()
    {
        var error = Assert.Throws<IOException>(() => FileBytes.Read(Path.Combine(Path.GetTempPath(), "fiv-no-such-file.jpg")));
        Assert.Contains("fiv-no-such-file.jpg", error.Message);
    }
}

public class WorkSchedulerTests
{
    [Fact]
    public void RunsTheMostUrgentQueuedItemFirst()
    {
        var scheduler = new WorkScheduler(1, "test");
        var order = new List<int>();
        using var gate = new ManualResetEventSlim();
        using var done = new CountdownEvent(4);
        // The first item holds the only worker while the others queue up.
        scheduler.Enqueue(0, _ => { gate.Wait(); done.Signal(); });
        foreach (int priority in new[] { 30, 10, 20 })
            scheduler.Enqueue(priority, _ => { lock (order) order.Add(priority); done.Signal(); });
        gate.Set();
        Assert.True(done.Wait(5000));
        Assert.Equal(new[] { 10, 20, 30 }, order);
    }

    [Fact]
    public void EqualPrioritiesRunInTheOrderQueued()
    {
        var scheduler = new WorkScheduler(1, "test");
        var order = new List<int>();
        using var gate = new ManualResetEventSlim();
        using var done = new CountdownEvent(5);
        scheduler.Enqueue(0, _ => { gate.Wait(); done.Signal(); });
        for (int i = 0; i < 4; i++)
        {
            int n = i;
            scheduler.Enqueue(5, _ => { lock (order) order.Add(n); done.Signal(); });
        }
        gate.Set();
        Assert.True(done.Wait(5000));
        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public void PrioritiesCanChangeWhileQueued()
    {
        var scheduler = new WorkScheduler(1, "test");
        var order = new List<string>();
        using var gate = new ManualResetEventSlim();
        using var done = new CountdownEvent(3);
        scheduler.Enqueue(0, _ => { gate.Wait(); done.Signal(); });
        scheduler.Enqueue(10, _ => { lock (order) order.Add("a"); done.Signal(); });
        WorkItem late = scheduler.Enqueue(20, _ => { lock (order) order.Add("b"); done.Signal(); });
        scheduler.SetPriority(late, 1);
        gate.Set();
        Assert.True(done.Wait(5000));
        Assert.Equal(new[] { "b", "a" }, order);
    }

    [Fact]
    public void AQueuedItemThatIsCancelledNeverRuns()
    {
        var scheduler = new WorkScheduler(1, "test");
        using var gate = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        bool ran = false;
        scheduler.Enqueue(0, _ => gate.Wait());
        WorkItem cancelled = scheduler.Enqueue(1, _ => ran = true);
        Assert.True(scheduler.Cancel(cancelled));
        scheduler.Enqueue(2, _ => done.Set());
        gate.Set();
        Assert.True(done.Wait(5000));
        Assert.False(ran);
        Assert.True(cancelled.IsCancelled);
    }

    [Fact]
    public unsafe void ARunningItemSeesItsCancelFlag()
    {
        var scheduler = new WorkScheduler(1, "test");
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        bool sawFlag = false;
        WorkItem item = scheduler.Enqueue(0, self =>
        {
            started.Set();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (*self.CancelFlag == 0 && timeout.ElapsedMilliseconds < 5000) Thread.Yield();
            sawFlag = *self.CancelFlag != 0;
            finished.Set();
        });
        Assert.True(started.Wait(5000));
        Assert.False(scheduler.Cancel(item)); // false: it is running, so it will report itself
        Assert.True(finished.Wait(5000));
        Assert.True(sawFlag);
    }

    [Fact]
    public void UsesUpToItsNumberOfWorkers()
    {
        var scheduler = new WorkScheduler(3, "test");
        int running = 0, peak = 0;
        using var done = new CountdownEvent(12);
        for (int i = 0; i < 12; i++)
        {
            scheduler.Enqueue(0, _ =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                Thread.Sleep(30);
                Interlocked.Decrement(ref running);
                done.Signal();
            });
        }
        Assert.True(done.Wait(10000));
        Assert.Equal(3, peak);
    }

    [Fact]
    public void AnItemThatThrowsDoesNotStopTheWorker()
    {
        var scheduler = new WorkScheduler(1, "test");
        using var done = new ManualResetEventSlim();
        scheduler.Enqueue(0, _ => throw new InvalidOperationException("boom"));
        scheduler.Enqueue(1, _ => done.Set());
        Assert.True(done.Wait(5000));
    }
}
