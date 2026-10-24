using System.Runtime.InteropServices;
using Fiv.Native;

namespace Fiv.Imaging;

/// <summary>
/// HEIF/HEIC, AVIF, WebP, JPEG XL and camera raw through the decoders bundled in fivcodecs.dll,
/// so that they open on every machine, not only where the matching Windows codec is installed.
/// The pixels arrive in the layouts the rest of the viewer takes: BGRA8 and Gray8, or RGBA16 and
/// Gray16 for files with more than 8 bits per channel (10- and 12-bit values spread over the
/// full 16-bit range), GrayFloat for float grey JPEG XL.
/// </summary>
public static unsafe class NativeDecoder
{
    static int s_unavailable;

    /// <summary>False once fivcodecs.dll has failed to load: everything then goes to WIC.</summary>
    public static bool Available => Volatile.Read(ref s_unavailable) == 0;

    /// <summary>Which bundled decoder takes the file, and for a raw its size, orientation and embedded preview.</summary>
    public static FivCodecs.Info Probe(byte* data, nuint size)
    {
        FivCodecs.Info info = default;
        if (!Available) return info;
        try
        {
            FivCodecs.fiv_probe(data, size, &info);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Volatile.Write(ref s_unavailable, 1);
            info = default;
        }
        return info;
    }

    /// <summary>
    /// Decodes the file as <paramref name="format"/>. With <paramref name="hint"/> above 0 the
    /// decoder may return something smaller whose long side is at least that, where the file makes
    /// it cheap (a HEIF thumbnail, a WebP scaled while decoding, a raw at half size): for thumbnails.
    /// Null if <paramref name="cancel"/> was set. Throws <see cref="InvalidDataException"/> if the
    /// file cannot be decoded.
    /// </summary>
    public static DecodedImage? Decode(byte* data, nuint size, FivCodecs.Format format, int hint = 0, int* cancel = null)
    {
        var target = new Target();
        GCHandle handle = GCHandle.Alloc(target);
        const int MessageSize = 512;
        byte* message = stackalloc byte[MessageSize];
        int result;
        try
        {
            result = FivCodecs.fiv_decode(data, size, format, hint, &Allocate, GCHandle.ToIntPtr(handle), cancel, message, MessageSize);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Volatile.Write(ref s_unavailable, 1);
            target.Image?.Dispose();
            throw new InvalidDataException("The decoders (fivcodecs.dll) cannot be loaded: " + e.Message, e);
        }
        finally
        {
            handle.Free();
        }
        if (result == FivCodecs.Ok && target.Image != null) return target.Image;
        target.Image?.Dispose();
        if (result == FivCodecs.Cancelled) return null;
        string? reason = Marshal.PtrToStringUTF8((nint)message);
        throw new InvalidDataException(string.IsNullOrEmpty(reason) ? "The image could not be decoded." : reason);
    }

    sealed class Target
    {
        public DecodedImage? Image;
    }

    // Called by the decoder, on the thread that called fiv_decode, once it knows what it will produce.
    [UnmanagedCallersOnly]
    static byte* Allocate(nint context, int width, int height, int layout, int sourceWidth, int sourceHeight)
    {
        try
        {
            if (width <= 0 || height <= 0 || (uint)layout > (uint)PixelLayout.Rgba16) return null;
            var target = (Target)GCHandle.FromIntPtr(context).Target!;
            target.Image?.Dispose();
            target.Image = new DecodedImage(width, height, (PixelLayout)layout, Math.Max(width, sourceWidth), Math.Max(height, sourceHeight));
            return target.Image.Pixels;
        }
        catch (Exception) // nothing may be thrown back into native code; null is reported as out of memory
        {
            return null;
        }
    }
}
