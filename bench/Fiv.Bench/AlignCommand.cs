using System.Diagnostics;
using System.Text;
using Fiv.Comparing;
using Fiv.Imaging;

namespace Fiv.Bench;

/// <summary>
/// What aligning two images costs and how close it comes: the 24 MP scene of the corpus against
/// copies of itself that were moved, turned and scaled by known amounts (resampled in memory,
/// and one from a JPEG file), against its recompressed version, which needs nothing, and against
/// its half-size version. No viewer is started.
/// </summary>
public static unsafe class AlignCommand
{
    /// <summary>With <paramref name="Region"/> the alignment goes by that rectangle alone, and "off by" is taken at its corners.</summary>
    sealed record Case(string Label, DecodedImage Moving, Affine2 Placement, AlignPose Truth, AlignModel Model, bool Fine = false, AlignPose Start = default, CanvasRect? Region = null);

    public static int Run(string corpus, int repetitions, string? outFile)
    {
        string compare = Path.Combine(corpus, "compare");
        string[] needed = ["scene_1_original.jpg", "scene_2_quality50.jpg", "scene_4_half.jpg", "scene_5_moved.jpg"];
        if (needed.Any(name => !File.Exists(Path.Combine(compare, name))))
        {
            Console.Error.WriteLine("corpus incomplete; run 'fivbench corpus'");
            return 1;
        }

        using DecodedImage original = Load(Path.Combine(compare, "scene_1_original.jpg"));
        int width = original.Width, height = original.Height;
        double centreX = width / 2.0, centreY = height / 2.0;
        var cases = new List<Case>();
        var owned = new List<DecodedImage>();
        DecodedImage Keep(DecodedImage image)
        {
            owned.Add(image);
            return image;
        }
        // A copy of the original that lies on it again under the pose: resampled, four pixels blended, not compressed.
        DecodedImage Moved(AlignPose pose) => Keep(CompareExport.Resampled(
            new CompareSource(original, pose.About(centreX, centreY).Inverse()), new CanvasRect(0, 0, width, height), out _));

        try
        {
            DecodedImage half = Keep(Load(Path.Combine(compare, "scene_4_half.jpg")));
            Affine2 fit = CompareGeometry.Placement(SizeMatch.ScaleToFit, half.Width, half.Height, width, height);
            const AlignModel all = AlignModel.ShiftRotationScale;
            DecodedImage whole = Moved(new AlignPose(37, -21));
            cases.Add(new("moved by whole pixels", whole, Affine2.Identity, new AlignPose(37, -21), all));
            cases.Add(new("moved by whole pixels, only moving allowed", whole, Affine2.Identity, new AlignPose(37, -21), AlignModel.Shift));
            cases.Add(new("moved by a fraction of a pixel", Moved(new AlignPose(12.3, -7.6)), Affine2.Identity, new AlignPose(12.3, -7.6), all));
            cases.Add(new("moved and turned by 0.31 degrees", Moved(new AlignPose(-18.4, 9.7, 0.31)), Affine2.Identity, new AlignPose(-18.4, 9.7, 0.31), all));
            cases.Add(new("moved, turned by -2.5 degrees, 2 % larger", Moved(new AlignPose(64.2, 31.5, -2.5, 1.02)), Affine2.Identity, new AlignPose(64.2, 31.5, -2.5, 1.02), all));
            cases.Add(new("turned by 25 degrees, 10 % smaller", Moved(new AlignPose(-120, 80, 25, 0.9)), Affine2.Identity, new AlignPose(-120, 80, 25, 0.9), all));
            cases.Add(new("scene_5_moved.jpg (JPEG; moved, 0.75 degrees, 0.4 % larger)", Keep(Load(Path.Combine(compare, "scene_5_moved.jpg"))), Affine2.Identity, CorpusCommand.MovedPose, all));
            cases.Add(new("the same, refined from a pose set by hand", owned[^1], Affine2.Identity, CorpusCommand.MovedPose, all, Fine: true, Start: new AlignPose(20, -10, 0.6)));
            cases.Add(new("the same, by a region of 1500 x 1000 pixels", owned[^1], Affine2.Identity, CorpusCommand.MovedPose, all, Region: new CanvasRect(2250, 1500, 1500, 1000)));
            cases.Add(new("the same, by a region of 400 x 300 pixels, only moving and turning allowed", owned[^1], Affine2.Identity, CorpusCommand.MovedPose, AlignModel.ShiftRotation,
                Fine: true, Start: new AlignPose(20, -10, 0.6, CorpusCommand.MovedPose.Scale), Region: new CanvasRect(800, 2600, 400, 300)));
            cases.Add(new("scene_2_quality50.jpg (nothing to align)", Keep(Load(Path.Combine(compare, "scene_2_quality50.jpg"))), Affine2.Identity, AlignPose.Identity, all));
            cases.Add(new("scene_4_half.jpg, scaled to fit (nothing to align)", half, fit, AlignPose.Identity, all));

            var sb = new StringBuilder();
            sb.AppendLine($"Aligning to a 24 MP image, {Environment.ProcessorCount} logical cores. Milliseconds, median of {repetitions}.");
            sb.AppendLine("\"Off by\" is how far the pose found puts the corners of the image from where the true pose puts them;");
            sb.AppendLine("where the alignment went by a region, the corners of that region.");
            sb.AppendLine();
            sb.AppendLine("| Moved image | Brightness at reduced sizes, both images | Aligning | Pose found | Off by | Turn off by | Alike before -> after |");
            sb.AppendLine("|---|---|---|---|---|---|---|");

            foreach (Case c in cases)
            {
                var pyramidTimes = new List<double>();
                var alignTimes = new List<double>();
                AlignResult? result = null;
                for (int i = 0; i < repetitions + 1; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    using var referenceLuma = new LumaPyramid(original);
                    using var movingLuma = new LumaPyramid(c.Moving);
                    double pyramidMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    started = Stopwatch.GetTimestamp();
                    result = Registration.Align(new AlignInput(referenceLuma, Affine2.Identity), new AlignInput(movingLuma, c.Placement), c.Start, c.Model, c.Fine, region: c.Region)!;
                    double alignMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (i == 0) continue; // the first run compiles the code
                    pyramidTimes.Add(pyramidMs);
                    alignTimes.Add(alignMs);
                }

                (double pivotX, double pivotY) = c.Placement.Apply(c.Moving.Width / 2.0, c.Moving.Height / 2.0);
                Affine2 found = result!.Pose.About(pivotX, pivotY), wanted = c.Truth.About(pivotX, pivotY);
                double error = 0;
                for (int corner = 0; corner < 4; corner++)
                {
                    // At the corners of the image; of an alignment by a region, at the corners of the region, which is where it looked.
                    (double x, double y) = c.Region is { } region
                        ? (region.X + (corner & 1) * (double)region.Width, region.Y + (corner >> 1) * (double)region.Height)
                        : c.Placement.Apply((corner & 1) * c.Moving.Width, (corner >> 1) * c.Moving.Height);
                    (double fx, double fy) = found.Apply(x, y);
                    (double wx, double wy) = wanted.Apply(x, y);
                    error = Math.Max(error, Math.Sqrt((fx - wx) * (fx - wx) + (fy - wy) * (fy - wy)));
                }
                string line = $"| {c.Label} | {new Stats(pyramidTimes).Median:F0} | {new Stats(alignTimes).Median:F0} | {result.Pose} | {error:F3} px | " +
                              $"{Math.Abs(result.Pose.Rotation - c.Truth.Rotation):F4} deg | {result.ScoreBefore:F4} -> {result.ScoreAfter:F4} |";
                sb.AppendLine(line);
                Console.WriteLine(line + $"   ({result.Patches} patches, clear to {result.Confidence:F2})");
            }

            // How alike two images are as they lie: what the align panel shows after every change of the pose.
            {
                using var referenceLuma = new LumaPyramid(original);
                using var movingLuma = new LumaPyramid(cases[0].Moving);
                var reference = new AlignInput(referenceLuma, Affine2.Identity);
                var times = new List<double>();
                double score = 0;
                for (int i = 0; i < 4 * repetitions + 1; i++)
                {
                    // A slightly different pose each time, as when the image is dragged.
                    var moving = new AlignInput(movingLuma, new AlignPose(37 + i * 0.1, -21, 0.01 * i).About(centreX, centreY));
                    long started = Stopwatch.GetTimestamp();
                    score = Registration.Score(reference, moving);
                    if (i > 0) times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                string line = $"How alike the two are at a given pose (the score of the align panel): {new Stats(times).Median:F1} ms, longest {new Stats(times).Max:F1} ms.";
                sb.AppendLine().AppendLine(line);
                Console.WriteLine(line);
                GC.KeepAlive(score);
            }

            // The Fourier transform everything rests on, there and back, on one core.
            foreach (int n in new[] { 256, 512 })
            {
                var random = new Random(n);
                float[] re = new float[n * n], im = new float[n * n];
                for (int i = 0; i < re.Length; i++) re[i] = random.NextSingle();
                Fft2D fft = Fft2D.For(n, n);
                var times = new List<double>();
                fixed (float* r = re, i = im)
                {
                    for (int run = 0; run < 40; run++)
                    {
                        long started = Stopwatch.GetTimestamp();
                        fft.Forward(r, i);
                        fft.Inverse(r, i);
                        if (run >= 5) times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 2);
                    }
                }
                string line = $"Fourier transform of {n} x {n} samples, one core: {new Stats(times).Median:F2} ms.";
                sb.AppendLine(line);
                Console.WriteLine(line);
            }

            if (outFile != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                File.WriteAllText(outFile, sb.ToString());
            }
            return 0;
        }
        finally
        {
            foreach (DecodedImage image in owned) image.Dispose();
        }
    }

    static DecodedImage Load(string path)
    {
        FileBytes file = FileBytes.Read(path);
        try
        {
            return ImageDecoding.DecodeFull(file, ImageDecoding.Probe(file), null)!;
        }
        finally
        {
            file.Release();
        }
    }
}
