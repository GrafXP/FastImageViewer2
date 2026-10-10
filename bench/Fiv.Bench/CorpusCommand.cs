using System.Diagnostics;
using Fiv.Native;

namespace Fiv.Bench;

/// <summary>
/// Generates the test corpus. Existing files are kept, so the command can be re-run.
///
///   single\   one file per format and size, for open and decode timing
///   compare\  one scene in versions to compare, and an image with transparency
///   series\   distinct 12 MP JPEGs, for navigation timing
/// </summary>
public static unsafe class CorpusCommand
{
    public static int Run(string root)
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

        Compare(Directory.CreateDirectory(Path.Combine(root, "compare")).FullName);

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
        Console.WriteLine($"corpus ready in {total.Elapsed.TotalSeconds:F0} s: {root}");
        return 0;
    }

    /// <summary>
    /// One 24 MP scene in versions that differ the way compared images do: recompressed, changed
    /// in one small place and a little brighter, and at half the size. Plus an image with
    /// transparency, which the rest of the corpus lacks.
    /// </summary>
    static void Compare(string folder)
    {
        const int width = 6000, height = 4000;
        if (Missing(folder, "scene_1_original.jpg", "scene_2_quality50.jpg", "scene_3_changed.jpg", "scene_4_half.jpg"))
        {
            using var scene = SyntheticImage.Create(width, height, 7, marker: false);
            Write(folder, "scene_1_original.jpg", p => File.WriteAllBytes(p, ImageWriters.EncodeJpeg(scene.Bgra, scene.Stride, width, height, 95, TurboJpeg.Subsampling.S420)));
            Write(folder, "scene_2_quality50.jpg", p => File.WriteAllBytes(p, ImageWriters.EncodeJpeg(scene.Bgra, scene.Stride, width, height, 50, TurboJpeg.Subsampling.S420)));

            using var half = new SyntheticImage.Buffer(width / 2, height / 2, withGray16: false);
            for (int y = 0; y < half.Height; y++)
            {
                byte* top = scene.Bgra + (long)(2 * y) * scene.Stride, bottom = top + scene.Stride, target = half.Bgra + (long)y * half.Stride;
                for (int i = 0; i < half.Stride; i++)
                {
                    int x = i / 4 * 8 + i % 4; // the same channel of the left pixel of the pair
                    target[i] = (byte)((top[x] + top[x + 4] + bottom[x] + bottom[x + 4] + 2) / 4);
                }
            }
            Write(folder, "scene_4_half.jpg", p => File.WriteAllBytes(p, ImageWriters.EncodeJpeg(half.Bgra, half.Stride, half.Width, half.Height, 95, TurboJpeg.Subsampling.S420)));

            // Last, because it changes the scene: six levels brighter, and a square of 64 pixels turned grey at (4200, 1300).
            for (int y = 0; y < height; y++)
            {
                byte* row = scene.Bgra + (long)y * scene.Stride;
                for (int x = 0; x < width; x++)
                {
                    bool inSquare = x >= 4200 && x < 4264 && y >= 1300 && y < 1364;
                    for (int c = 0; c < 3; c++) row[x * 4 + c] = inSquare ? (byte)128 : (byte)Math.Min(255, row[x * 4 + c] + 6);
                }
            }
            Write(folder, "scene_3_changed.jpg", p => File.WriteAllBytes(p, ImageWriters.EncodeJpeg(scene.Bgra, scene.Stride, width, height, 95, TurboJpeg.Subsampling.S420)));
        }

        if (Missing(folder, "alpha.png"))
        {
            // Opaque in the middle, fading to fully transparent at the corners, with a clear-cut transparent disc.
            using var image = SyntheticImage.Create(1920, 1080, 8, marker: false);
            for (int y = 0; y < image.Height; y++)
            {
                byte* row = image.Bgra + (long)y * image.Stride;
                for (int x = 0; x < image.Width; x++)
                {
                    double dx = (x - 960) / 960.0, dy = (y - 540) / 540.0, distance = Math.Sqrt(dx * dx + dy * dy);
                    double hx = x - 1400, hy = y - 400;
                    bool hole = hx * hx + hy * hy < 120 * 120;
                    row[x * 4 + 3] = hole ? (byte)0 : (byte)Math.Clamp((1.25 - distance) * 255 / 0.5, 0, 255);
                }
            }
            Write(folder, "alpha.png", p => ImageWriters.WritePng(p, image.Bgra, image.Stride, image.Width, image.Height, ImageWriters.PngKind.Rgba8));
        }
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
}
