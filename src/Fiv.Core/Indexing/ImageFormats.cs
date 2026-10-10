namespace Fiv.Indexing;

/// <summary>Which files the viewer treats as images, decided from the file name alone.</summary>
public static class ImageFormats
{
    /// <summary>
    /// Extensions without the dot, lower case. The camera raw and HEIF/AVIF ones decode only if
    /// the matching Windows codec is installed. <see cref="Fiv.Shell.FileAssociations.Types"/>
    /// lists the same endings, for registering the viewer with Windows; a test holds the two together.
    /// </summary>
    public static readonly string[] Extensions =
    [
        "jpg", "jpeg", "jpe", "jfif", "png", "tif", "tiff", "bmp", "dib", "gif", "webp", "ico",
        "heic", "heif", "avif", "jxr", "wdp", "dng", "cr2", "cr3", "nef", "arw", "raf", "orf", "rw2",
    ];

    const int MaxExtensionLength = 5;
    static readonly ulong[] Keys = Array.ConvertAll(Extensions, e => Pack(e));

    public static bool IsImageFileName(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;
        ReadOnlySpan<char> extension = name[(dot + 1)..];
        if (extension.Length is 0 or > MaxExtensionLength) return false;
        ulong key = Pack(extension);
        foreach (ulong known in Keys)
            if (known == key) return true;
        return false;
    }

    public static bool IsJpegFileName(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;
        ReadOnlySpan<char> extension = name[(dot + 1)..];
        return extension.Equals("jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("jpe", StringComparison.OrdinalIgnoreCase) || extension.Equals("jfif", StringComparison.OrdinalIgnoreCase);
    }

    // ASCII letters lower-cased, 8 bits per character; anything outside Latin-1 cannot match.
    static ulong Pack(ReadOnlySpan<char> extension)
    {
        ulong key = 0;
        foreach (char c in extension)
        {
            if (c > 0xFF) return ulong.MaxValue;
            key = (key << 8) | ((uint)(c - 'A') <= 'Z' - 'A' ? (uint)c | 0x20 : c);
        }
        return key;
    }
}
