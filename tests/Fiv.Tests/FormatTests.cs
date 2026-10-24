using System.Text;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Native;
using Fiv.Thumbnails;
using Xunit;

namespace Fiv.Tests;

/// <summary>
/// The sample files of bench\tools\format-samples.txt, fetched into corpus\formats by
/// bench\tools\get-format-samples.ps1: every format the viewer opens, at the bit depths and in the
/// variants that tend to break decoders. Tests that need them are skipped when they are not there.
/// </summary>
static class FormatSamples
{
    public static readonly string Root = Path.Combine(FindRepoRoot(), "corpus", "formats");
    public static bool Present => Directory.Exists(Root);

    /// <summary>Files no decoder the viewer has can read: a BMP type only Windows CE wrote, a camera newer than LibRaw.</summary>
    public static readonly string[] Unsupported = ["bmp/rgba32abf.bmp", "raw/sony-a7v.arw"];

    public static IEnumerable<object[]> All()
    {
        string list = Path.Combine(FindRepoRoot(), "bench", "tools", "format-samples.txt");
        foreach (string line in File.ReadLines(list))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            yield return [line[..line.IndexOf(' ')]];
        }
    }

    public static string PathOf(string sample) => Path.Combine(Root, sample.Replace('/', Path.DirectorySeparatorChar));

    static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PLAN.md"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}

public sealed class SamplesFactAttribute : FactAttribute
{
    public SamplesFactAttribute()
    {
        if (!FormatSamples.Present) Skip = @"no samples; run bench\tools\get-format-samples.ps1";
    }
}

public sealed class SamplesTheoryAttribute : TheoryAttribute
{
    public SamplesTheoryAttribute()
    {
        if (!FormatSamples.Present) Skip = @"no samples; run bench\tools\get-format-samples.ps1";
    }
}

public sealed unsafe class FormatTests
{
    // ---------------------------------------------------------------- without the samples

    [Theory]
    [InlineData("photo.HEIC", true, false)]
    [InlineData("camera.hif", true, false)]
    [InlineData("a.avif", true, false)]
    [InlineData("a.jxl", true, false)]
    [InlineData("IMG_0001.CR3", true, true)]
    [InlineData("DSC0001.ARW", true, true)]
    [InlineData("_DSF0001.RAF", true, true)]
    [InlineData("scan.tif", true, false)]
    [InlineData("a.jpg", true, false)]
    [InlineData("a.raw", false, false)]
    public void RecognisesTheNewEndings(string name, bool image, bool raw)
    {
        Assert.Equal(image, ImageFormats.IsImageFileName(name));
        Assert.Equal(raw, ImageFormats.IsRawFileName(name));
    }

    [Theory]
    [InlineData(ImageCodec.Heif, new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0, (byte)'m', (byte)'i', (byte)'f', (byte)'1', (byte)'h', (byte)'e', (byte)'i', (byte)'c' })]
    [InlineData(ImageCodec.Heif, new byte[] { 0, 0, 0, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0, (byte)'a', (byte)'v', (byte)'i', (byte)'f' })]
    [InlineData(ImageCodec.WebP, new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)' ' })]
    [InlineData(ImageCodec.JpegXl, new byte[] { 0xFF, 0x0A, 0xFA, 0x7F, 0x01, 0x90, 0x08, 0x06, 0x01, 0x00, 0x48, 0x01, 0, 0, 0, 0 })]
    [InlineData(ImageCodec.Wic, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R' })]
    public void ProbeChoosesTheDecoderBySignature(ImageCodec codec, byte[] head)
    {
        // The probe goes by the first bytes; the file need not be a whole image. (Not so for a raw,
        // which only counts as one once LibRaw has read it: those are in the tests with the samples.)
        string path = Path.Combine(Path.GetTempPath(), $"fiv-probe-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [.. head, .. new byte[256]]);
        try
        {
            FileBytes file = FileBytes.Read(path);
            try
            {
                Assert.Equal(codec, ImageDecoding.Probe(file).Codec);
            }
            finally
            {
                file.Release();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(FivCodecs.Format.Heif)]
    [InlineData(FivCodecs.Format.WebP)]
    [InlineData(FivCodecs.Format.JpegXl)]
    [InlineData(FivCodecs.Format.Raw)]
    [InlineData(FivCodecs.Format.Tiff)]
    public void GarbageIsAnErrorNotACrash(FivCodecs.Format format)
    {
        byte[] garbage = Encoding.ASCII.GetBytes(new string('x', 4096));
        string? message = null;
        fixed (byte* data = garbage)
        {
            try
            {
                NativeDecoder.Decode(data, (nuint)garbage.Length, format)?.Dispose();
            }
            catch (InvalidDataException e)
            {
                message = e.Message;
            }
        }
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ---------------------------------------------------------------- with the samples

    [SamplesTheory]
    [MemberData(nameof(FormatSamples.All), MemberType = typeof(FormatSamples))]
    public void EverySampleOpensAsTheViewerOpensIt(string sample)
    {
        bool unsupported = FormatSamples.Unsupported.Contains(sample);
        WithFile(sample, file =>
        {
            ImageInfo info = ImageDecoding.Probe(file);
            if (unsupported)
            {
                Assert.ThrowsAny<Exception>(() => ImageDecoding.DecodeFull(file, info, null));
                return;
            }
            Assert.Equal(ExpectedCodec(sample), info.Codec);
            using DecodedImage image = ImageDecoding.DecodeFull(file, info, null)!;
            Assert.True(image.Width > 0 && image.Height > 0);
            Assert.Equal(image.Width, image.SourceWidth);
            Assert.True(Variation(image) > 0.01, "a flat image: nothing or garbage was decoded");

            using Thumbnail thumbnail = ThumbnailGenerator.FromImage(file, info)!;
            Assert.Equal(PixelLayout.Bgra8, thumbnail.Pixels.Layout);
            Assert.InRange(Math.Max(thumbnail.Pixels.Width, thumbnail.Pixels.Height), 1, ThumbnailGenerator.MasterSize);
        });
    }

    static ImageCodec ExpectedCodec(string sample)
    {
        string extension = Path.GetExtension(sample).TrimStart('.');
        return extension switch
        {
            "heic" or "heif" or "hif" or "avif" => ImageCodec.Heif,
            "webp" => ImageCodec.WebP,
            "jxl" => ImageCodec.JpegXl,
            "jpg" => ImageCodec.Jpeg,
            "tif" or "tiff" => sample is "tiff/rgb-10.tif" or "tiff/rgb-12.tif" or "tiff/rgb-14.tif" or "tiff/rgb-24.tif" or "tiff/rgb-32.tif"
                or "tiff/gray-float16.tif" or "tiff/gray-float64.tif" or "tiff/logluv.tif" or "tiff/ycbcr.tif" ? ImageCodec.Tiff : ImageCodec.Wic,
            _ when ImageFormats.IsRawFileName(sample) => ImageCodec.Raw,
            _ => ImageCodec.Wic,
        };
    }

    [SamplesTheory]
    [InlineData("heif/rgb-10bit.heif", 128, 128, PixelLayout.Rgba16)]
    [InlineData("heif/rgb-12bit.heif", 128, 128, PixelLayout.Rgba16)]
    [InlineData("heif/rgba-12bit-odd.heif", 29, 100, PixelLayout.Rgba16)]
    [InlineData("heif/gray-10bit.heif", 128, 128, PixelLayout.Gray16)]
    [InlineData("heif/camera-10bit.hif", 4240, 2832, PixelLayout.Rgba16)]
    [InlineData("heif/camera-rotated.hif", 4672, 7008, PixelLayout.Rgba16)]
    [InlineData("heif/phone.heic", 4032, 3024, PixelLayout.Bgra8)]
    [InlineData("heif/rotate-crop.heic", 300, 300, PixelLayout.Bgra8)]
    [InlineData("heif/sequence-only.heic", 1280, 720, PixelLayout.Bgra8)]
    [InlineData("avif/10bit-420.avif", 1204, 800, PixelLayout.Rgba16)]
    [InlineData("avif/12bit-444.avif", 1204, 800, PixelLayout.Rgba16)]
    [InlineData("avif/10bit-mono.avif", 1204, 800, PixelLayout.Gray16)]
    [InlineData("avif/8bit-444-odd.avif", 1203, 799, PixelLayout.Bgra8)]
    [InlineData("avif/rotate90.avif", 722, 1024, PixelLayout.Bgra8)]
    [InlineData("avif/grid.avif", 6400, 2880, PixelLayout.Bgra8)]
    [InlineData("avif/sequence-alpha.avif", 640, 480, PixelLayout.Bgra8)]
    [InlineData("jxl/float32.jxl", 500, 500, PixelLayout.Rgba16)]
    [InlineData("jxl/10bit-alpha.jxl", 924, 1386, PixelLayout.Rgba16)]
    [InlineData("jxl/16bit-spot.jxl", 600, 400, PixelLayout.Rgba16)]
    [InlineData("jxl/gray.jxl", 200, 200, PixelLayout.Gray8)]
    [InlineData("jxl/lossy.jxl", 1024, 631, PixelLayout.Bgra8)]
    [InlineData("jpeg/12bit.jpg", 149, 227, PixelLayout.Rgba16)]
    [InlineData("jpeg/gray-u2.jpg", 31, 32, PixelLayout.Gray16)]
    [InlineData("tiff/rgb-10.tif", 73, 43, PixelLayout.Rgba16)]
    [InlineData("tiff/rgb-32.tif", 73, 43, PixelLayout.Rgba16)]
    [InlineData("tiff/gray-float16.tif", 111, 83, PixelLayout.GrayFloat)]
    [InlineData("tiff/gray-float64.tif", 111, 83, PixelLayout.GrayFloat)]
    [InlineData("webp/animated.webp", 300, 225, PixelLayout.Bgra8)]
    [InlineData("raw/canon.cr3", 4319, 2879, PixelLayout.Rgba16)]
    [InlineData("raw/fujifilm.raf", 6264, 4176, PixelLayout.Rgba16)]
    [InlineData("raw/sigma.x3f", 2651, 1767, PixelLayout.Rgba16)]
    public void DecodesToTheSizeAndDepthOfTheFile(string sample, int width, int height, PixelLayout layout)
    {
        using DecodedImage image = Decode(sample);
        Assert.Equal((width, height, layout), (image.Width, image.Height, image.Layout));
    }

    [SamplesTheory]
    [InlineData("heif/rgb-10bit.heif", 10)]
    [InlineData("heif/rgba-10bit.heif", 10)]
    [InlineData("heif/rgb-12bit.heif", 12)]
    [InlineData("heif/gray-10bit.heif", 10)]
    [InlineData("heif/camera-10bit.hif", 10)]
    [InlineData("avif/10bit-420.avif", 10)]
    [InlineData("avif/12bit-444.avif", 12)]
    [InlineData("avif/10bit-mono.avif", 10)]
    [InlineData("jpeg/12bit.jpg", 12)]
    [InlineData("tiff/rgb-10.tif", 10)]
    [InlineData("tiff/rgb-14.tif", 14)]
    public void SamplesOfFewerThan16BitsSpanTheFullRange(string sample, int bits)
    {
        // v << (16 - bits) | v >> (2 * bits - 16): the lowest bits repeat the highest, so that the
        // largest value of the file is 65535 and the smallest 0.
        using DecodedImage image = Decode(sample);
        var samples = new ReadOnlySpan<ushort>(image.Pixels, (int)(image.ByteCount / 2));
        int spread = 16 - bits;
        ushort low = (ushort)((1 << spread) - 1);
        foreach (ushort s in samples) Assert.Equal(s >> (16 - spread), s & low);
    }

    [SamplesTheory]
    [InlineData("tiff/rgb-8.tif")]
    [InlineData("tiff/rgb-10.tif")]
    [InlineData("tiff/rgb-12.tif")]
    [InlineData("tiff/rgb-14.tif")]
    [InlineData("tiff/rgb-24.tif")]
    [InlineData("tiff/rgb-32.tif")]
    [InlineData("tiff/rgb-planar-16.tif")]
    public void EveryDepthOfOneTiffIsThePicture(string sample)
    {
        // The same flower at 8 to 32 bits per sample; WIC fails on some of these and gets 32 wrong.
        using DecodedImage reference = Decode("tiff/rgb-16.tif");
        using DecodedImage image = Decode(sample);
        Assert.InRange(MeanDifference(image, reference, (x, y) => (x, y)), 0, 0.005);
    }

    [SamplesFact]
    public void FloatTiffsOfEveryWidthGiveTheSameValues()
    {
        // WIC reads 16-bit float as integers and does not read 64-bit float at all.
        using DecodedImage half = Decode("tiff/gray-float16.tif");
        using DecodedImage single = Decode("tiff/gray-float32.tif");
        using DecodedImage @double = Decode("tiff/gray-float64.tif");
        for (int i = 0; i < single.Width * single.Height; i++)
        {
            float s = ((float*)single.Pixels)[i];
            Assert.Equal(s, ((float*)@double.Pixels)[i], 1e-5f);
            Assert.Equal(s, ((float*)half.Pixels)[i], Math.Abs(s) / 500 + 1e-4f);
        }
    }

    [SamplesFact]
    public void TwelveBitJpegIsThePictureOfItsSixteenBitPng()
    {
        using DecodedImage jpeg = Decode("jpeg/12bit.jpg");
        using DecodedImage png = Decode("png/monkey-16bit.png");
        Assert.InRange(MeanDifference(jpeg, png, (x, y) => (x, y)), 0, 0.01);
    }

    [SamplesFact]
    public void AvifTransformsAreApplied()
    {
        // The PNGs hold the pictures as stored; the files say how to turn them back to the original.
        using DecodedImage rotated = Decode("avif/rotate90.avif");
        using DecodedImage rotatedStored = Decode("avif/rotate90.png");
        int storedHeight = rotatedStored.Height;
        Assert.InRange(MeanDifference(rotated, rotatedStored, (x, y) => (y, storedHeight - 1 - x)), 0, 0.02); // turned 90 degrees clockwise

        using DecodedImage mirrored = Decode("avif/mirror-rotate.avif");
        using DecodedImage mirroredStored = Decode("avif/mirror-rotate.png");
        Assert.InRange(MeanDifference(mirrored, mirroredStored, (x, y) => (y, x)), 0, 0.02); // turned back and mirrored: transposed

        // The reference of the crop has other colours (libheif and the Windows codec agree on the
        // file's), so it is the region that is compared: it lies where nothing shifted lies as well.
        using DecodedImage cropped = Decode("avif/crop.avif");
        using DecodedImage croppedReference = Decode("avif/crop.png");
        Assert.Equal((croppedReference.Width, croppedReference.Height), (cropped.Width, cropped.Height));
        double aligned = Correlation(cropped, croppedReference, 0, 0);
        Assert.True(aligned > 0.98, $"correlation {aligned:F3}");
        Assert.True(aligned > Correlation(cropped, croppedReference, 6, 0) + 0.02);
        Assert.True(aligned > Correlation(cropped, croppedReference, 0, 6) + 0.02);
    }

    [SamplesTheory]
    [InlineData("raw/canon.cr3")]
    [InlineData("raw/nikon.nef")]
    [InlineData("raw/sony.arw")]
    [InlineData("raw/fujifilm.raf")]
    [InlineData("raw/olympus.orf")]
    [InlineData("raw/panasonic.rw2")]
    [InlineData("raw/pentax.dng")]
    public void RawsHaveTheirEmbeddedJpegAsPreview(string sample)
    {
        WithFile(sample, file =>
        {
            ImageInfo info = ImageDecoding.Probe(file);
            Assert.True(ImageDecoding.HasPreview(info, 1920, 1080));
            using DecodedImage preview = ImageDecoding.DecodePreview(file, info, 1920, 1080, null)!;
            // Drawn in place of the raw: at its size, which it must match in shape.
            Assert.Equal((info.Width, info.Height), (preview.SourceWidth, preview.SourceHeight));
            Assert.InRange((double)preview.Width / preview.Height / ((double)info.Width / info.Height), 0.97, 1.03);
        });
    }

    [SamplesTheory]
    [InlineData("raw/nikon.nef")]
    [InlineData("heif/camera-rotated.hif")]
    [InlineData("tiff/rgb-24.tif")]
    public void ADecodeAlreadyCancelledReturnsNothing(string sample)
    {
        WithFile(sample, file =>
        {
            ImageInfo info = ImageDecoding.Probe(file);
            int cancel = 1;
            Assert.Null(NativeDecoder.Decode(file.Data, file.Length, ImageDecoding.NativeFormat(info.Codec)!.Value, 0, &cancel));
        });
    }

    // ---------------------------------------------------------------- helpers

    static DecodedImage Decode(string sample)
    {
        DecodedImage? result = null;
        WithFile(sample, file => result = ImageDecoding.DecodeFull(file, ImageDecoding.Probe(file), null));
        return result!;
    }

    static void WithFile(string sample, Action<FileBytes> use)
    {
        FileBytes file = FileBytes.Read(FormatSamples.PathOf(sample));
        try
        {
            use(file);
        }
        finally
        {
            file.Release();
        }
    }

    /// <summary>Red, green, blue (or grey) of a pixel over 0..1.</summary>
    static (double R, double G, double B) Rgb(DecodedImage image, int x, int y)
    {
        byte* row = image.Pixels + (long)y * image.Stride;
        return image.Layout switch
        {
            PixelLayout.Bgra8 => (row[x * 4 + 2] / 255.0, row[x * 4 + 1] / 255.0, row[x * 4] / 255.0),
            PixelLayout.Rgba16 => (((ushort*)row)[x * 4] / 65535.0, ((ushort*)row)[x * 4 + 1] / 65535.0, ((ushort*)row)[x * 4 + 2] / 65535.0),
            PixelLayout.Gray8 => (row[x] / 255.0, row[x] / 255.0, row[x] / 255.0),
            PixelLayout.Gray16 => (((ushort*)row)[x] / 65535.0, ((ushort*)row)[x] / 65535.0, ((ushort*)row)[x] / 65535.0),
            _ => (((float*)row)[x], ((float*)row)[x], ((float*)row)[x]),
        };
    }

    /// <summary>The mean difference of red, green and blue, with <paramref name="map"/> taking a pixel of <paramref name="a"/> to one of <paramref name="b"/>.</summary>
    static double MeanDifference(DecodedImage a, DecodedImage b, Func<int, int, (int X, int Y)> map)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                (int bx, int by) = map(x, y);
                Assert.InRange(bx, 0, b.Width - 1);
                Assert.InRange(by, 0, b.Height - 1);
                var p = Rgb(a, x, y);
                var q = Rgb(b, bx, by);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        }
        return sum / (3.0 * a.Width * a.Height);
    }

    /// <summary>The correlation of the brightness of <paramref name="a"/> with that of <paramref name="b"/> shifted by (dx, dy), away from the borders.</summary>
    static double Correlation(DecodedImage a, DecodedImage b, int dx, int dy)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int n = 0;
        for (int y = 12; y < a.Height - 12; y++)
        {
            for (int x = 12; x < a.Width - 12; x++, n++)
            {
                var p = Rgb(a, x, y);
                var q = Rgb(b, x + dx, y + dy);
                double u = p.R + p.G + p.B, v = q.R + q.G + q.B;
                sa += u;
                sb += v;
                saa += u * u;
                sbb += v * v;
                sab += u * v;
            }
        }
        double covariance = sab / n - sa / n * (sb / n);
        return covariance / Math.Sqrt((saa / n - sa / n * (sa / n)) * (sbb / n - sb / n * (sb / n)));
    }

    /// <summary>The spread of the red, green and blue values: near 0 for a black, white or one-colour result.</summary>
    static double Variation(DecodedImage image)
    {
        double sum = 0, squares = 0;
        int count = 0, step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / 40_000));
        for (int y = 0; y < image.Height; y += step)
        {
            for (int x = 0; x < image.Width; x += step, count++)
            {
                var p = Rgb(image, x, y);
                double v = (p.R + p.G + p.B) / 3;
                sum += v;
                squares += v * v;
            }
        }
        double mean = sum / count;
        return Math.Sqrt(Math.Max(0, squares / count - mean * mean)) / Math.Max(1e-6, Math.Abs(mean) + 1e-3);
    }
}
