using Fiv.Imaging;
using Fiv.Thumbnails;
using Xunit;

namespace Fiv.Tests;

public unsafe class ResamplerTests
{
    [Theory]
    [InlineData(6000, 4000, 256, 256, 171)]
    [InlineData(4000, 6000, 256, 171, 256)]
    [InlineData(100, 50, 256, 100, 50)]     // never enlarged
    [InlineData(20000, 100, 256, 256, 1)]   // extreme shapes keep at least one pixel
    public void FitsWithinASquare(int width, int height, int max, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), Resampler.FitWithin(width, height, max));

    [Fact]
    public void HalvingAveragesTwoByTwo()
    {
        using var image = new DecodedImage(4, 2, PixelLayout.Bgra8, 4, 2);
        // Blue channel: 0 100 | 200 40 over 50 150 | 220 60; the other channels constant.
        byte[] blue = [0, 100, 200, 40, 50, 150, 220, 60];
        for (int i = 0; i < 8; i++)
        {
            image.Pixels[i * 4] = blue[i];
            image.Pixels[i * 4 + 1] = 10;
            image.Pixels[i * 4 + 2] = 20;
            image.Pixels[i * 4 + 3] = 255;
        }
        using DecodedImage half = Resampler.ToBgra(image, 2, 1);
        Assert.Equal(75, half.Pixels[0]);   // (0 + 100 + 50 + 150) / 4
        Assert.Equal(130, half.Pixels[4]);  // (200 + 40 + 220 + 60) / 4
        Assert.Equal((10, 20, 255), ((int)half.Pixels[1], (int)half.Pixels[2], (int)half.Pixels[3]));
    }

    [Fact]
    public void UnevenRatiosWeightPartlyCoveredPixels()
    {
        // Three pixels onto two: each output covers one whole pixel and half of the middle one.
        using var image = new DecodedImage(3, 1, PixelLayout.Gray8, 3, 1);
        image.Pixels[0] = 0;
        image.Pixels[1] = 90;
        image.Pixels[2] = 240;
        using DecodedImage reduced = Resampler.ToBgra(image, 2, 1);
        Assert.Equal(30, reduced.Pixels[0]);   // (0 * 1 + 90 * 0.5) / 1.5
        Assert.Equal(190, reduced.Pixels[4]);  // (90 * 0.5 + 240 * 1) / 1.5
        Assert.Equal(reduced.Pixels[0], reduced.Pixels[1]); // grey: all three colour channels alike
        Assert.Equal(255, reduced.Pixels[3]);
    }

    [Fact]
    public void AFlatImageStaysFlatAtAnySize()
    {
        using var image = new DecodedImage(97, 61, PixelLayout.Bgra8, 97, 61);
        new Span<byte>(image.Pixels, (int)image.ByteCount).Fill(123);
        using DecodedImage reduced = Resampler.ToBgra(image, 13, 7);
        Assert.All(new Span<byte>(reduced.Pixels, (int)reduced.ByteCount).ToArray(), value => Assert.Equal(123, value));
    }

    [Fact]
    public void SixteenBitGreyIsMappedThroughItsWindow()
    {
        using var image = new DecodedImage(2, 1, PixelLayout.Gray16, 2, 1);
        ((ushort*)image.Pixels)[0] = 1000;
        ((ushort*)image.Pixels)[1] = 3000;
        // Window from 1000 to 3000 of 65535: the first pixel black, the second white.
        using DecodedImage mapped = Resampler.ToBgra(image, 2, 1, 1000 / 65535f, 3000 / 65535f);
        Assert.Equal((0, 255), ((int)mapped.Pixels[0], (int)mapped.Pixels[4]));
        // Over the full range both are nearly black.
        using DecodedImage plain = Resampler.ToBgra(image, 2, 1);
        Assert.InRange(plain.Pixels[4], 11, 12);
    }

    [Fact]
    public void CropTakesOnlyTheGivenRectangle()
    {
        using DecodedImage image = TestImages.Gradient(100, 40); // blue rises left to right
        using DecodedImage right = Resampler.ToBgra(image, 10, 10, 0, 1, cropX: 80, cropY: 0, cropWidth: 20, cropHeight: 40);
        Assert.InRange(right.Pixels[0], 200, 215);  // blue at x = 80..81 of 0..99
        Assert.InRange(right.Pixels[9 * 4], 245, 255);
    }

    [Theory]
    [InlineData(Orientation.Rotate90, 2, 3, 0, 0, 4)]    // the stored bottom-left corner ends up top-left
    [InlineData(Orientation.Rotate180, 3, 2, 0, 0, 6)]
    [InlineData(Orientation.MirrorHorizontal, 3, 2, 0, 0, 3)]
    [InlineData(Orientation.Rotate270, 2, 3, 0, 0, 3)]   // the stored top-right corner ends up top-left
    public void UprightRearrangesPixels(Orientation orientation, int width, int height, int x, int y, int expected)
    {
        // Stored 3x2, numbered   1 2 3
        //                        4 5 6
        using var image = new DecodedImage(3, 2, PixelLayout.Bgra8, 3, 2);
        for (int i = 0; i < 6; i++) ((uint*)image.Pixels)[i] = (uint)(i + 1);
        using DecodedImage upright = Resampler.Upright(image, orientation);
        Assert.Equal((width, height), (upright.Width, upright.Height));
        Assert.Equal((uint)expected, ((uint*)upright.Pixels)[y * upright.Width + x]);
    }

    [Fact]
    public void UprightOfRotate90PutsTheStoredTopRowOnTheRight()
    {
        using var image = new DecodedImage(3, 2, PixelLayout.Bgra8, 3, 2);
        for (int i = 0; i < 6; i++) ((uint*)image.Pixels)[i] = (uint)(i + 1);
        using DecodedImage upright = Resampler.Upright(image, Orientation.Rotate90);
        // 1 2 3      4 1
        // 4 5 6  ->  5 2
        //            6 3
        Assert.Equal(new uint[] { 4, 1, 5, 2, 6, 3 }, new Span<uint>(upright.Pixels, 6).ToArray());
    }
}

public unsafe class ThumbnailGeneratorTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("fivthumb").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    FileBytes Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return FileBytes.Read(path);
    }

    [Fact]
    public void JpegThumbnailFitsTheMasterSizeAndKeepsTheSourceSize()
    {
        FileBytes file = Write("a.jpg", TestImages.Jpeg(2000, 1000));
        try
        {
            using Thumbnail thumbnail = ThumbnailGenerator.FromImage(file, ImageDecoding.Probe(file))!;
            Assert.Equal((256, 128), (thumbnail.Pixels.Width, thumbnail.Pixels.Height));
            Assert.Equal((2000, 1000), (thumbnail.Width, thumbnail.Height));
            Assert.False(thumbnail.Embedded);
            // Still the gradient: blue low on the left, high on the right.
            byte* row = thumbnail.Pixels.Pixels + 64L * thumbnail.Pixels.Stride;
            Assert.InRange(row[0], 0, 12);
            Assert.InRange(row[255 * 4], 243, 255);
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void RotatedJpegGivesAnUprightThumbnail()
    {
        FileBytes file = Write("rotated.jpg", TestImages.WithExif(TestImages.Jpeg(2000, 1000), 6));
        try
        {
            using Thumbnail thumbnail = ThumbnailGenerator.FromImage(file, ImageDecoding.Probe(file))!;
            Assert.Equal((128, 256), (thumbnail.Pixels.Width, thumbnail.Pixels.Height));
            Assert.Equal(Orientation.Rotate90, thumbnail.Orientation);
            Assert.Equal((2000, 1000), (thumbnail.Width, thumbnail.Height)); // as stored
            // Rotated 90 degrees clockwise, the left-to-right blue gradient runs top to bottom.
            Assert.InRange(thumbnail.Pixels.Pixels[64 * 4], 0, 12);
            Assert.InRange(thumbnail.Pixels.Pixels[255L * thumbnail.Pixels.Stride + 64 * 4], 243, 255);
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void SmallImagesAreNotEnlarged()
    {
        FileBytes file = Write("small.jpg", TestImages.Jpeg(120, 80));
        try
        {
            using Thumbnail thumbnail = ThumbnailGenerator.FromImage(file, ImageDecoding.Probe(file))!;
            Assert.Equal((120, 80), (thumbnail.Pixels.Width, thumbnail.Pixels.Height));
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void EmbeddedThumbnailIsFoundCroppedToTheImageShapeAndTurned()
    {
        // A 3:2 image carrying the 4:3 thumbnail cameras write, i.e. with bars above and below.
        byte[] embedded = TestImages.Jpeg(160, 120);
        byte[] jpeg = TestImages.WithExif(TestImages.Jpeg(1500, 1000), 6, thumbnail: embedded);
        FileBytes file = Write("camera.jpg", jpeg);
        try
        {
            ImageInfo info = ImageDecoding.Probe(file);
            Assert.Equal(new ImageInfo(true, 1500, 1000, Orientation.Rotate90), info);
            Assert.True(Exif.TryFindJpegThumbnail(file.Data, file.Length, out int offset, out int size));
            Assert.Equal(embedded.Length, size);
            Assert.True(new ReadOnlySpan<byte>(file.Data + offset, size).SequenceEqual(embedded));

            using Thumbnail thumbnail = ThumbnailGenerator.FromEmbedded(file, info)!;
            Assert.True(thumbnail.Embedded);
            // 160x120 cropped to 3:2 is 160x107, then turned upright.
            Assert.Equal((107, 160), (thumbnail.Pixels.Width, thumbnail.Pixels.Height));
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void NoEmbeddedThumbnailMeansNull()
    {
        FileBytes file = Write("plain.jpg", TestImages.WithExif(TestImages.Jpeg(640, 480), 1));
        try
        {
            Assert.Null(ThumbnailGenerator.FromEmbedded(file, ImageDecoding.Probe(file)));
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void CancelledGenerationReturnsNull()
    {
        FileBytes file = Write("big.jpg", TestImages.Jpeg(3000, 2000));
        try
        {
            int cancel = 1;
            Assert.Null(ThumbnailGenerator.FromImage(file, ImageDecoding.Probe(file), &cancel));
        }
        finally
        {
            file.Release();
        }
    }

    [Fact]
    public void FitReducesAMasterAndLeavesSmallOnesAlone()
    {
        using DecodedImage master = TestImages.Gradient(256, 171);
        using DecodedImage small = ThumbnailGenerator.Fit(master, 96);
        Assert.Equal((96, 64), (small.Width, small.Height));
        using DecodedImage same = ThumbnailGenerator.Fit(master, 256);
        Assert.Same(master, same);
    }

    [Fact]
    public void EncodedThumbnailsDecodeBackToTheSamePicture()
    {
        using DecodedImage master = TestImages.Gradient(256, 171);
        byte[] jpeg = JpegEncoder.Encode(master, 88);
        Assert.InRange(jpeg.Length, 500, 20000);
        fixed (byte* p = jpeg)
        {
            using DecodedImage back = JpegDecoder.Decode(p, (nuint)jpeg.Length)!;
            Assert.Equal((256, 171), (back.Width, back.Height));
            Assert.InRange(back.Pixels[100L * back.Stride + 128 * 4], 120, 136); // blue at mid-width
        }
    }
}

public sealed class ThumbnailPackTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("fivpack").FullName;
    const string Folder = @"C:\Photos\Holiday";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static byte[] Blob(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void StoresAndReturnsEntriesAndThumbnails()
    {
        using ThumbnailPack pack = ThumbnailPack.Open(_root, Folder);
        Assert.False(pack.ReadOnly);
        Assert.False(pack.TryGet("IMG_1.jpg", 1000, 55, out _));

        pack.Add("IMG_1.jpg", 1000, 55, 6000, 4000, Orientation.Rotate90, 171, 256, Blob(300, 1));
        pack.Add("IMG_2.jpg", 2000, 66, 4000, 3000, Orientation.Normal, 256, 192, Blob(500, 7));

        Assert.True(pack.TryGet("img_1.JPG", 1000, 55, out PackEntry entry)); // names are matched without regard to case
        Assert.Equal((6000, 4000, Orientation.Rotate90, 171, 256, 300), (entry.Width, entry.Height, entry.Orientation, entry.ThumbnailWidth, entry.ThumbnailHeight, entry.BlobLength));
        byte[] buffer = new byte[1024];
        Assert.True(pack.TryReadThumbnail(entry, buffer));
        Assert.Equal(Blob(300, 1), buffer[..300]);

        Assert.True(pack.TryGet("IMG_2.jpg", 2000, 66, out entry));
        Assert.True(pack.TryReadThumbnail(entry, buffer));
        Assert.Equal(Blob(500, 7), buffer[..500]);
    }

    [Fact]
    public void AnEntryCountsOnlyForTheFileAsItWas()
    {
        using ThumbnailPack pack = ThumbnailPack.Open(_root, Folder);
        pack.Add("a.jpg", 1000, 55, 100, 100, Orientation.Normal, 100, 100, Blob(10, 0));
        Assert.False(pack.TryGet("a.jpg", 1001, 55, out _)); // size changed
        Assert.False(pack.TryGet("a.jpg", 1000, 56, out _)); // time changed
        // The new version replaces the old entry.
        pack.Add("a.jpg", 1001, 56, 200, 200, Orientation.Normal, 200, 200, Blob(20, 5));
        Assert.True(pack.TryGet("a.jpg", 1001, 56, out PackEntry entry));
        Assert.Equal(200, entry.Width);
        Assert.Equal(1, pack.Count);
    }

    [Fact]
    public void SurvivesClosingAndReopening()
    {
        using (ThumbnailPack pack = ThumbnailPack.Open(_root, Folder))
        {
            for (int i = 0; i < 500; i++) pack.Add($"IMG_{i}.jpg", 1000 + i, i, 4000, 3000, Orientation.Normal, 256, 192, Blob(100 + i % 50, (byte)i));
        }
        // A differently written path to the same folder finds the same pack.
        using ThumbnailPack again = ThumbnailPack.Open(_root, @"c:\photos\holiday\");
        Assert.Equal(500, again.Count);
        Assert.True(again.TryGet("IMG_321.jpg", 1321, 321, out PackEntry entry));
        byte[] buffer = new byte[256];
        Assert.True(again.TryReadThumbnail(entry, buffer));
        Assert.Equal(Blob(100 + 321 % 50, 321 % 256), buffer[..entry.BlobLength]);
        // And more can be added.
        again.Add("new.jpg", 5, 5, 10, 10, Orientation.Normal, 10, 10, Blob(8, 9));
        Assert.Equal(501, again.Count);
    }

    [Fact]
    public void DimensionsCanBeStoredWithoutAThumbnail()
    {
        using ThumbnailPack pack = ThumbnailPack.Open(_root, Folder);
        pack.Add("probed.png", 10, 20, 1920, 1080, Orientation.Normal, 0, 0, default);
        Assert.True(pack.TryGet("probed.png", 10, 20, out PackEntry entry));
        Assert.False(entry.HasThumbnail);
        Assert.Equal((1920, 1080), (entry.Width, entry.Height));
        Assert.False(pack.TryReadThumbnail(entry, new byte[16]));
    }

    [Fact]
    public void DifferentFoldersHaveDifferentPacks()
    {
        using ThumbnailPack a = ThumbnailPack.Open(_root, @"C:\Photos\A");
        using ThumbnailPack b = ThumbnailPack.Open(_root, @"C:\Photos\B");
        a.Add("x.jpg", 1, 1, 10, 10, Orientation.Normal, 10, 10, Blob(4, 1));
        Assert.False(b.TryGet("x.jpg", 1, 1, out _));
    }

    [Fact]
    public void ADamagedIndexStartsEmptyInsteadOfFailing()
    {
        using (ThumbnailPack pack = ThumbnailPack.Open(_root, Folder))
            pack.Add("a.jpg", 1, 1, 10, 10, Orientation.Normal, 10, 10, Blob(4, 1));
        string index = Directory.GetFiles(_root, "*.idx").Single();
        File.WriteAllBytes(index, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        using ThumbnailPack again = ThumbnailPack.Open(_root, Folder);
        Assert.Equal(0, again.Count);
        again.Add("b.jpg", 2, 2, 10, 10, Orientation.Normal, 10, 10, Blob(4, 2));
        Assert.True(again.TryGet("b.jpg", 2, 2, out _));
    }

    [Fact]
    public void ARecordCutShortIsIgnoredAndTheRestKept()
    {
        using (ThumbnailPack pack = ThumbnailPack.Open(_root, Folder))
        {
            pack.Add("a.jpg", 1, 1, 10, 10, Orientation.Normal, 10, 10, Blob(4, 1));
            pack.Add("b.jpg", 2, 2, 10, 10, Orientation.Normal, 10, 10, Blob(4, 2));
        }
        string index = Directory.GetFiles(_root, "*.idx").Single();
        byte[] bytes = File.ReadAllBytes(index);
        File.WriteAllBytes(index, bytes[..^5]); // as after a crash in the middle of the last record
        using ThumbnailPack again = ThumbnailPack.Open(_root, Folder);
        Assert.True(again.TryGet("a.jpg", 1, 1, out _));
        Assert.False(again.TryGet("b.jpg", 2, 2, out _));
    }

    [Fact]
    public void ASecondOpenWhileInUseReadsButDoesNotWrite()
    {
        using ThumbnailPack writer = ThumbnailPack.Open(_root, Folder);
        writer.Add("a.jpg", 1, 1, 10, 10, Orientation.Normal, 10, 10, Blob(4, 1));
        writer.Flush();
        using ThumbnailPack reader = ThumbnailPack.Open(_root, Folder);
        Assert.True(reader.ReadOnly);
        Assert.True(reader.TryGet("a.jpg", 1, 1, out PackEntry entry));
        Assert.True(reader.TryReadThumbnail(entry, new byte[16]));
        reader.Add("b.jpg", 2, 2, 10, 10, Orientation.Normal, 10, 10, Blob(4, 2)); // silently not stored
        Assert.False(reader.TryGet("b.jpg", 2, 2, out _));
    }

    [Fact]
    public void TrimDeletesThePacksUsedLongestAgo()
    {
        string[] folders = [@"C:\A", @"C:\B", @"C:\C"];
        for (int i = 0; i < folders.Length; i++)
        {
            using (ThumbnailPack pack = ThumbnailPack.Open(_root, folders[i]))
                pack.Add("x.jpg", 1, 1, 10, 10, Orientation.Normal, 10, 10, Blob(10_000, 1));
            // Used at different times: A longest ago.
            foreach (string index in Directory.GetFiles(_root, "*.idx"))
                if (File.GetLastWriteTimeUtc(index) > DateTime.UtcNow.AddMinutes(-1))
                    File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddHours(-(folders.Length - i)));
        }
        ThumbnailPack.Trim(_root, 21_000); // room for two packs of about 10 KB
        Assert.Equal(2, Directory.GetFiles(_root, "*.idx").Length);
        using ThumbnailPack oldest = ThumbnailPack.Open(_root, folders[0]);
        Assert.Equal(0, oldest.Count);
    }
}
