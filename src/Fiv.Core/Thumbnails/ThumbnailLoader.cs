using Fiv.Imaging;

namespace Fiv.Thumbnails;

/// <summary>Where a thumbnail came from.</summary>
public enum ThumbnailSource : byte
{
    /// <summary>Read from the folder's pack on disk.</summary>
    Cache,

    /// <summary>The thumbnail the camera embedded in the file; a better one follows.</summary>
    Embedded,

    /// <summary>Decoded from the image and added to the pack.</summary>
    Image,
}

/// <summary>
/// The work of one thumbnail request, run on a pool thread: return the stored thumbnail if the
/// folder's pack has one, otherwise make it (showing the camera's embedded one first if there is
/// one), store it, and return it at the size asked for.
/// </summary>
public static unsafe class ThumbnailLoader
{
    [ThreadStatic] static byte[]? t_buffer;

    /// <summary>How much of a file is read before looking for an embedded thumbnail. EXIF data is rarely larger.</summary>
    const int HeadBytes = 128 * 1024;

    /// <summary>
    /// Loads the thumbnail of <paramref name="path"/> at no more than <paramref name="size"/> pixels a side.
    /// <paramref name="preliminary"/> is called with the embedded thumbnail, when the file has one
    /// and its own thumbnail has to be made first; the caller owns that image.
    /// Returns null if cancelled. <paramref name="entry"/> has the image's dimensions afterwards.
    /// </summary>
    public static DecodedImage? Load(ThumbnailPack pack, string path, ReadOnlySpan<char> name, long fileSize, long modified, int size,
        int* cancel, Action<DecodedImage>? preliminary, out PackEntry entry, out ThumbnailSource source)
    {
        source = ThumbnailSource.Cache;
        if (pack.TryGet(name, fileSize, modified, out entry) && entry.HasThumbnail)
        {
            DecodedImage? stored = FromPack(pack, entry, size);
            if (stored != null) return stored;
        }
        if (cancel != null && *cancel != 0) return null;

        source = ThumbnailSource.Image;
        // A camera's embedded thumbnail sits in the first kilobytes: it is handed out as soon as
        // those are read, before the rest of the file.
        FileBytes file = preliminary == null
            ? FileBytes.Read(path)
            : FileBytes.Read(path, HeadBytes, (data, headLength, _) =>
            {
                using Thumbnail? embedded = ThumbnailGenerator.FromEmbedded(data, headLength);
                if (embedded != null) preliminary(ThumbnailGenerator.Fit(embedded.Pixels, size));
            });
        try
        {
            ImageInfo info = ImageDecoding.Probe(file);
            using Thumbnail? thumbnail = ThumbnailGenerator.FromImage(file, info, cancel);
            if (thumbnail == null) return null;
            byte[] jpeg = JpegEncoder.Encode(thumbnail.Pixels, 88);
            pack.Add(name, fileSize, modified, thumbnail.Width, thumbnail.Height, thumbnail.Orientation,
                thumbnail.Pixels.Width, thumbnail.Pixels.Height, jpeg);
            entry = new PackEntry(fileSize, modified, thumbnail.Width, thumbnail.Height, thumbnail.Orientation,
                thumbnail.Pixels.Width, thumbnail.Pixels.Height, 0, jpeg.Length);
            return ThumbnailGenerator.Fit(thumbnail.Pixels, size);
        }
        finally
        {
            file.Release();
        }
    }

    /// <summary>Decodes a stored thumbnail at the size asked for. Null if the pack's data cannot be read or decoded.</summary>
    static DecodedImage? FromPack(ThumbnailPack pack, in PackEntry entry, int size)
    {
        byte[] buffer = t_buffer ??= new byte[64 * 1024];
        if (buffer.Length < entry.BlobLength) buffer = t_buffer = new byte[Math.Max(entry.BlobLength, buffer.Length * 2)];
        if (!pack.TryReadThumbnail(entry, buffer)) return null;
        try
        {
            fixed (byte* p = buffer)
            {
                // Halving inside the JPEG decoder is nearly free; the exact size is then averaged down.
                int stored = Math.Max(entry.ThumbnailWidth, entry.ThumbnailHeight);
                int denominator = stored >= size * 4 ? 4 : stored >= size * 2 ? 2 : 1;
                using DecodedImage? decoded = JpegDecoder.Decode(p, (nuint)entry.BlobLength, denominator);
                return decoded == null ? null : ThumbnailGenerator.Fit(decoded, size);
            }
        }
        catch (InvalidDataException)
        {
            return null; // damaged data: the thumbnail is made again
        }
    }
}
