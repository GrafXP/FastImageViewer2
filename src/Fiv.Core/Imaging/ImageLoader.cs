using System.Diagnostics;

namespace Fiv.Imaging;

public enum LoadStage
{
    /// <summary>A reduced-size decode, enough for the viewport. Only for formats that have a cheap one.</summary>
    Preview,

    /// <summary>The whole image with its mip chain.</summary>
    Full,

    Failed,
}

/// <summary>One delivery from the loader. The receiver owns <see cref="Image"/> and <see cref="Mips"/>.</summary>
public sealed class LoadResult
{
    public required string Path { get; init; }
    public required int Generation { get; init; }
    public required LoadStage Stage { get; init; }
    public DecodedImage? Image { get; init; }
    public MipChain? Mips { get; init; }
    public Orientation Orientation { get; init; } = Orientation.Normal;
    public long FileSize { get; init; }
    public string? Error { get; init; }

    /// <summary>Smallest and largest sample of a grayscale image, in the units the GPU samples (0..1 for integer formats).</summary>
    public (float Min, float Max) ValueRange { get; init; } = (0, 1);

    public double ReadMs { get; init; }
    public double DecodeMs { get; init; }
    public double MipMs { get; init; }

    /// <summary>Stopwatch timestamps: when the file was in memory, and when this result was delivered.</summary>
    public long FileRead { get; init; }
    public long Completed { get; init; }
}

/// <summary>
/// Loads one image at a time for display: reads the file, then decodes a viewport-sized preview
/// and the full image on two threads at once, delivering each as it completes. A newer
/// <see cref="Load"/> supersedes the older ones; their results carry an older generation number
/// and the receiver discards them.
/// </summary>
public sealed unsafe class ImageLoader(Action<LoadResult> deliver)
{
    /// <summary>How far a preview may be magnified to fill the viewport.</summary>
    const double PreviewUpscale = 1.5;

    int _generation;

    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Starts loading and returns the generation number its results will carry.</summary>
    public int Load(string path, int viewWidth, int viewHeight)
    {
        int generation = Interlocked.Increment(ref _generation);
        new Thread(() => Run(path, generation, viewWidth, viewHeight)) { IsBackground = true, Name = "fiv load" }.Start();
        return generation;
    }

    bool Superseded(int generation) => generation != Volatile.Read(ref _generation);

    void Run(string path, int generation, int viewWidth, int viewHeight)
    {
        FileBytes? file = null;
        try
        {
            long started = Stopwatch.GetTimestamp();
            file = FileBytes.Read(path);
            long fileRead = Stopwatch.GetTimestamp();
            double readMs = Stopwatch.GetElapsedTime(started, fileRead).TotalMilliseconds;
            if (Superseded(generation)) return;

            bool jpeg = file.Length > 3 && file.Data[0] == 0xFF && file.Data[1] == 0xD8 && file.Data[2] == 0xFF;
            if (!jpeg || !JpegDecoder.TryReadSize(file.Data, file.Length, out int width, out int height))
            {
                DecodeFull(path, generation, file, jpeg: false, Orientation.Normal, readMs, fileRead);
                return;
            }

            Orientation orientation = Exif.ReadJpegOrientation(file.Data, file.Length);
            FileBytes shared = file;
            shared.AddReference();
            new Thread(() =>
            {
                try
                {
                    DecodeFull(path, generation, shared, jpeg: true, orientation, readMs, fileRead);
                }
                finally
                {
                    shared.Release();
                }
            }) { IsBackground = true, Name = "fiv decode" }.Start();

            // The preview is sized for the image as it will be fitted into the viewport. It is on
            // screen only until the full decode lands, so it may be stretched a little
            // (PreviewUpscale) if that lets a smaller, faster reduction do.
            (int boxWidth, int boxHeight) = orientation.SwapsAxes() ? (viewHeight, viewWidth) : (viewWidth, viewHeight);
            double fit = Math.Min(1.0, Math.Min((double)boxWidth / width, (double)boxHeight / height)) / PreviewUpscale;
            int denominator = JpegDecoder.ChooseDenominator(width, height, (int)Math.Ceiling(width * fit), (int)Math.Ceiling(height * fit));
            if (denominator == 1) return; // the full decode is the preview

            long decodeStarted = Stopwatch.GetTimestamp();
            DecodedImage preview = JpegDecoder.Decode(file.Data, file.Length, denominator);
            deliver(new LoadResult
            {
                Path = path,
                Generation = generation,
                Stage = LoadStage.Preview,
                Image = preview,
                Orientation = orientation,
                FileSize = (long)file.Length,
                ReadMs = readMs,
                FileRead = fileRead,
                DecodeMs = Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds,
                Completed = Stopwatch.GetTimestamp(),
            });
        }
        catch (Exception e)
        {
            Fail(path, generation, e);
        }
        finally
        {
            file?.Release();
        }
    }

    void DecodeFull(string path, int generation, FileBytes file, bool jpeg, Orientation orientation, double readMs, long fileRead)
    {
        try
        {
            if (Superseded(generation)) return;
            long decodeStarted = Stopwatch.GetTimestamp();
            DecodedImage image = jpeg ? JpegDecoder.Decode(file.Data, file.Length) : WicDecoder.Decode(file.Data, file.Length);
            double decodeMs = Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds;
            if (Superseded(generation))
            {
                image.Dispose();
                return;
            }
            long mipStarted = Stopwatch.GetTimestamp();
            var mips = new MipChain(image);
            var range = ImageStatistics.ValueRange(image);
            deliver(new LoadResult
            {
                Path = path,
                Generation = generation,
                Stage = LoadStage.Full,
                Image = image,
                Mips = mips,
                Orientation = orientation,
                FileSize = (long)file.Length,
                ValueRange = range,
                ReadMs = readMs,
                FileRead = fileRead,
                DecodeMs = decodeMs,
                MipMs = Stopwatch.GetElapsedTime(mipStarted).TotalMilliseconds,
                Completed = Stopwatch.GetTimestamp(),
            });
        }
        catch (Exception e)
        {
            Fail(path, generation, e);
        }
    }

    void Fail(string path, int generation, Exception e) => deliver(new LoadResult
    {
        Path = path,
        Generation = generation,
        Stage = LoadStage.Failed,
        Error = e.Message,
        Completed = Stopwatch.GetTimestamp(),
    });
}
