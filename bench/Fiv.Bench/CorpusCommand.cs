using System.Diagnostics;
using Fiv.Native;

namespace Fiv.Bench;

/// <summary>
/// Generates the test corpus. Existing files are kept, so the command can be re-run.
///
///   single\   one file per format and size, for open and decode timing
///   series\   distinct 12 MP JPEGs, for navigation timing
///   folders\  1k / 10k / 100k small files with camera-like names, for index timing
/// </summary>
public static unsafe class CorpusCommand
{
    public static int Run(string root, bool skipFolders)
    {
        var total = Stopwatch.StartNew();
        string single = Directory.CreateDirectory(Path.Combine(root, "single")).FullName;

        Jpeg(single, "jpeg_12mp.jpg", 4000, 3000, 1);
        Jpeg(single, "jpeg_24mp.jpg", 6000, 4000, 2);
        Jpeg(single, "jpeg_50mp.jpg", 8688, 5792, 3);
        Jpeg(single, "jpeg_24mp_422.jpg", 6000, 4000, 2, TurboJpeg.Subsampling.S422);
        Jpeg(single, "jpeg_24mp_444.jpg", 6000, 4000, 2, TurboJpeg.Subsampling.S444);
        Jpeg(single, "jpeg_24mp_progressive.jpg", 6000, 4000, 2, progressive: true);
        Jpeg(single, "jpeg_wide_20000x4000.jpg", 20000, 4000, 4);
        foreach (int orientation in new[] { 3, 6, 8 })
        {
            string rotated = Path.Combine(single, $"jpeg_12mp_exif{orientation}.jpg");
            if (!File.Exists(rotated)) File.WriteAllBytes(rotated, ImageWriters.WithExif(File.ReadAllBytes(Path.Combine(single, "jpeg_12mp.jpg")), orientation));
        }

        if (Missing(single, "png8_24mp.png", "png16_24mp.png", "tiff16_24mp.tif", "tiff_float_24mp.tif", "png8_2mp.png"))
        {
            using var image = SyntheticImage.Create(6000, 4000, 2, withGray16: true);
            Write(single, "png8_24mp.png", p => ImageWriters.WritePng(p, image.Bgra, image.Stride, image.Width, image.Height, ImageWriters.PngKind.Rgb8));
            Write(single, "png16_24mp.png", p => ImageWriters.WritePng(p, (byte*)image.Gray16, image.Width * 2, image.Width, image.Height, ImageWriters.PngKind.Gray16));
            Write(single, "tiff16_24mp.tif", p => ImageWriters.WriteTiffGray(p, image.Gray16, image.Width, image.Height, isFloat: false));
            Write(single, "tiff_float_24mp.tif", p =>
            {
                float* samples = ImageWriters.ToFloat(image.Gray16, image.Width, image.Height);
                ImageWriters.WriteTiffGray(p, samples, image.Width, image.Height, isFloat: true);
                System.Runtime.InteropServices.NativeMemory.Free(samples);
            });
            using var small = SyntheticImage.Create(1920, 1080, 5);
            Write(single, "png8_2mp.png", p => ImageWriters.WritePng(p, small.Bgra, small.Stride, small.Width, small.Height, ImageWriters.PngKind.Rgb8));
        }

        string series = Directory.CreateDirectory(Path.Combine(root, "series", "jpeg_12mp")).FullName;
        for (int i = 0; i < 40; i++) Jpeg(series, $"IMG_{i + 1:D4}.jpg", 4000, 3000, 100 + i, quiet: true);
        Console.WriteLine($"series\\jpeg_12mp: 40 files, {FolderSize(series) / 1e6:F0} MB");

        // The same images as a camera would write them: with a 160 px thumbnail in the EXIF block.
        string withThumbnails = Directory.CreateDirectory(Path.Combine(root, "series", "jpeg_12mp_exif")).FullName;
        foreach (string source in Directory.GetFiles(series, "*.jpg"))
        {
            string target = Path.Combine(withThumbnails, Path.GetFileName(source));
            if (File.Exists(target)) continue;
            Fiv.Imaging.FileBytes file = Fiv.Imaging.FileBytes.Read(source);
            try
            {
                using Fiv.Thumbnails.Thumbnail thumbnail = Fiv.Thumbnails.ThumbnailGenerator.FromImage(file, Fiv.Imaging.ImageDecoding.Probe(file))!;
                using Fiv.Imaging.DecodedImage small = Fiv.Thumbnails.ThumbnailGenerator.Fit(thumbnail.Pixels, 160);
                File.WriteAllBytes(target, ImageWriters.WithExif(File.ReadAllBytes(source), 1, Fiv.Imaging.JpegEncoder.Encode(small, 75)));
            }
            finally
            {
                file.Release();
            }
        }
        Console.WriteLine($"series\\jpeg_12mp_exif: 40 files with embedded thumbnails, {FolderSize(withThumbnails) / 1e6:F0} MB");

        // Enough camera-sized files to fill a screen of the folder panel: the series three times over.
        string tripled = Directory.CreateDirectory(Path.Combine(root, "series", "jpeg_12mp_x3")).FullName;
        foreach (string source in Directory.GetFiles(series, "*.jpg"))
        {
            foreach (char set in "ABC")
            {
                string target = Path.Combine(tripled, set + "_" + Path.GetFileName(source));
                if (!File.Exists(target)) File.Copy(source, target);
            }
        }
        Console.WriteLine($"series\\jpeg_12mp_x3: 120 files, {FolderSize(tripled) / 1e6:F0} MB");

        if (!skipFolders)
        {
            Folder(Path.Combine(root, "folders", "1k"), 1_000);
            Folder(Path.Combine(root, "folders", "10k"), 10_000);
            Folder(Path.Combine(root, "folders", "100k"), 100_000);
        }
        Console.WriteLine($"corpus ready in {total.Elapsed.TotalSeconds:F0} s: {root}");
        return 0;
    }

    static bool Missing(string folder, params string[] names) => names.Any(n => !File.Exists(Path.Combine(folder, n)));

    static void Write(string folder, string name, Action<string> write)
    {
        string path = Path.Combine(folder, name);
        if (File.Exists(path)) return;
        var sw = Stopwatch.StartNew();
        write(path + ".tmp");
        File.Move(path + ".tmp", path);
        Console.WriteLine($"{name,-28} {new FileInfo(path).Length / 1e6,7:F1} MB   ({sw.Elapsed.TotalSeconds:F1} s)");
    }

    static void Jpeg(string folder, string name, int width, int height, int seed,
        TurboJpeg.Subsampling subsampling = TurboJpeg.Subsampling.S420, bool progressive = false, bool quiet = false)
    {
        string path = Path.Combine(folder, name);
        if (File.Exists(path)) return;
        using var image = SyntheticImage.Create(width, height, seed);
        byte[] jpeg = ImageWriters.EncodeJpeg(image.Bgra, image.Stride, width, height, 95, subsampling, progressive);
        File.WriteAllBytes(path, jpeg);
        if (!quiet)
            Console.WriteLine($"{name,-28} {jpeg.Length / 1e6,7:F1} MB   {width}x{height}, {jpeg.Length * 8.0 / ((double)width * height):F2} bits/pixel");
    }

    static long FolderSize(string folder) => new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length);

    /// <summary>
    /// A folder of small files named the way cameras, phones and scanners name them. Numbers are
    /// not zero-padded in part of the names, so that natural and ordinal sort orders differ.
    /// </summary>
    static void Folder(string folder, int count)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Take(count).Count() >= count) return;
        Directory.CreateDirectory(folder);
        var sw = Stopwatch.StartNew();

        var blobs = new byte[16][];
        for (int i = 0; i < blobs.Length; i++)
        {
            using var image = SyntheticImage.Create(160, 120, 1000 + i, marker: false);
            blobs[i] = ImageWriters.EncodeJpeg(image.Bgra, image.Stride, image.Width, image.Height, 80, TurboJpeg.Subsampling.S420);
        }
        string pngPath = Path.Combine(folder, "_tmp.png");
        using (var image = SyntheticImage.Create(160, 120, 2000, marker: false))
            ImageWriters.WritePng(pngPath, image.Bgra, image.Stride, image.Width, image.Height, ImageWriters.PngKind.Rgb8);
        byte[] png = File.ReadAllBytes(pngPath);
        File.Delete(pngPath);

        var random = new Random(count);
        var start = new DateTime(2019, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            int kind = i % 20;
            (string name, byte[] data) = kind switch
            {
                < 11 => ($"IMG_{i}.jpg", blobs[i % blobs.Length]),
                < 15 => ($"DSC{i:D6}.JPG", blobs[i % blobs.Length]),
                < 17 => ($"PXL_{start.AddMinutes(i):yyyyMMdd_HHmmss}{i % 1000:D3}.jpg", blobs[i % blobs.Length]),
                17 => ($"scan {i}.png", png),
                18 => ($"Screenshot ({i}).png", png),
                _ => ($"IMG_{i}.xmp", "<x:xmpmeta/>"u8.ToArray()),
            };
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, start.AddSeconds((long)i * 37 % 150_000_000));
        });
        Console.WriteLine($"folders\\{Path.GetFileName(folder)}: {count} files in {sw.Elapsed.TotalSeconds:F0} s");
    }
}
