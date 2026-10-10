using Microsoft.Win32;

namespace Fiv.Shell;

/// <summary>One ending the viewer can be registered for.</summary>
/// <param name="Extension">Without the dot, lower case.</param>
/// <param name="Format">The format it belongs to: endings of one format are shown together.</param>
/// <param name="TypeName">What Explorer calls such a file.</param>
/// <param name="Common">Part of the set suggested where nothing is registered yet.</param>
public readonly record struct FileType(string Extension, string Format, string TypeName, bool Common);

/// <summary>
/// Registers the viewer with Windows as a program for image files, for the current user and
/// without an installer, and takes the registration back. The viewer stays a folder that can be
/// copied anywhere; after a move, registering again points Windows at the new place.
///
/// For each ending, below HKEY_CURRENT_USER\Software:
/// <code>
/// Classes\FastImageViewer2.jpg                  the file type: name, icon, shell\open\command
/// Classes\.jpg                                  (Default) = FastImageViewer2.jpg
/// Classes\.jpg\OpenWithProgids                  FastImageViewer2.jpg
/// FastImageViewer2\Capabilities\FileAssociations   .jpg = FastImageViewer2.jpg
/// FastImageViewer2\ReplacedDefaults             .jpg = what (Default) was before
/// RegisteredApplications                        FastImageViewer2 = Software\FastImageViewer2\Capabilities
/// </code>
/// That puts the viewer into "Open with" and into the default apps of the Windows settings, and
/// makes it the program for endings the user has not chosen a program for. Where the user has
/// chosen one (on Windows 10 and 11: nearly always), the choice stands until they change it in
/// Windows: it is kept under a hash only Windows can write, and is not touched here.
///
/// Taking an ending back removes all of the above and puts the previous default back, unless
/// another program has made itself the default since. It also removes the viewer's name from
/// Explorer's own list of programs offered for the ending (Explorer\FileExts\.jpg\OpenWithProgids),
/// which the shell adds to when it shows "Open with".
/// </summary>
public sealed class FileAssociations : IDisposable
{
    public const string AppName = "FastImageViewer2";

    /// <summary>The value of RegisteredApplications, and so the path Windows reads the capabilities from.</summary>
    const string CapabilitiesPath = @"Software\" + CapabilitiesKey;
    const string CapabilitiesKey = AppName + @"\Capabilities";
    const string AssociationsKey = CapabilitiesKey + @"\FileAssociations";
    const string ReplacedKey = AppName + @"\ReplacedDefaults";

    /// <summary>
    /// The endings of <see cref="Fiv.Indexing.ImageFormats.Extensions"/>, format by format. The
    /// ones that are not common decode only with a Windows codec that may not be installed, or
    /// are files one rarely wants a viewer for.
    /// </summary>
    public static readonly FileType[] Types =
    [
        new("jpg", "JPEG", "JPEG image", true), new("jpeg", "JPEG", "JPEG image", true),
        new("jpe", "JPEG", "JPEG image", true), new("jfif", "JPEG", "JPEG image", true),
        new("png", "PNG", "PNG image", true),
        new("tif", "TIFF", "TIFF image", true), new("tiff", "TIFF", "TIFF image", true),
        new("bmp", "BMP", "Bitmap image", true), new("dib", "BMP", "Bitmap image", true),
        new("gif", "GIF", "GIF image", true),
        new("webp", "WebP", "WebP image", true),
        new("ico", "Icon", "Icon", false),
        new("heic", "HEIF", "HEIF image", false), new("heif", "HEIF", "HEIF image", false),
        new("avif", "AVIF", "AVIF image", false),
        new("jxr", "JPEG XR", "JPEG XR image", false), new("wdp", "JPEG XR", "JPEG XR image", false),
        new("dng", "Camera raw", "DNG raw image", false),
        new("cr2", "Camera raw", "Canon raw image", false), new("cr3", "Camera raw", "Canon raw image", false),
        new("nef", "Camera raw", "Nikon raw image", false),
        new("arw", "Camera raw", "Sony raw image", false),
        new("raf", "Camera raw", "Fujifilm raw image", false),
        new("orf", "Camera raw", "Olympus raw image", false),
        new("rw2", "Camera raw", "Panasonic raw image", false),
    ];

    readonly RegistryKey _software;

    /// <param name="software">The key the registrations go below, writable; closed with this object. Tests pass a key of their own.</param>
    public FileAssociations(RegistryKey software) => _software = software;

    /// <summary>The registrations of the current user: HKEY_CURRENT_USER\Software.</summary>
    public static FileAssociations ForCurrentUser() => new(Registry.CurrentUser.CreateSubKey("Software"));

    public void Dispose() => _software.Dispose();

    /// <summary>The name of the file type an ending is registered as.</summary>
    public static string ProgId(string extension) => AppName + "." + extension;

    /// <summary>
    /// Reads a list of endings as the command line gives it: "jpg,png,tif" (a dot before an
    /// ending and upper case are fine), or "common", "all" or "none". Null if there is an ending
    /// in it that the viewer does not open.
    /// </summary>
    public static string[]? ParseList(string list)
    {
        switch (list.Trim().ToLowerInvariant())
        {
            case "none": return [];
            case "all": return Array.ConvertAll(Types, type => type.Extension);
            case "common": return Array.ConvertAll(Array.FindAll(Types, type => type.Common), type => type.Extension);
        }
        var extensions = new List<string>();
        foreach (string item in list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string extension = item.TrimStart('.').ToLowerInvariant();
            if (!Array.Exists(Types, type => type.Extension == extension)) return null;
            if (!extensions.Contains(extension)) extensions.Add(extension);
        }
        return extensions.Count > 0 ? [.. extensions] : null;
    }

    /// <summary>The endings registered now, without the dot.</summary>
    public List<string> Registered()
    {
        var extensions = new List<string>();
        using RegistryKey? associations = _software.OpenSubKey(AssociationsKey);
        if (associations == null) return extensions;
        foreach (string name in associations.GetValueNames())
            if (name.Length > 1 && name[0] == '.') extensions.Add(name[1..].ToLowerInvariant());
        return extensions;
    }

    /// <summary>The executable the registered endings start; null if none is registered.</summary>
    public string? Executable()
    {
        foreach (string extension in Registered())
        {
            using RegistryKey? command = _software.OpenSubKey($@"Classes\{ProgId(extension)}\shell\open\command");
            if (command?.GetValue("") is string line && line.StartsWith('"') && line.IndexOf('"', 1) is > 1 and int end) return line[1..end];
        }
        return null;
    }

    /// <summary>
    /// Makes the registered endings exactly these: registers them for <paramref name="executable"/>
    /// and takes back every other one. With none, nothing of the viewer is left in the registry.
    /// </summary>
    public void Apply(IEnumerable<string> extensions, string executable)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        // Also endings that this version no longer lists, and ones whose entry in the list was lost.
        List<string> known = Registered();
        foreach (FileType type in Types)
            if (!known.Contains(type.Extension)) known.Add(type.Extension);
        foreach (string extension in known)
            if (!wanted.Contains(extension)) Unregister(extension);

        bool any = false;
        foreach (FileType type in Types)
        {
            if (!wanted.Contains(type.Extension)) continue;
            Register(type, executable);
            any = true;
        }

        if (any)
        {
            using RegistryKey capabilities = _software.CreateSubKey(CapabilitiesKey);
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "An image viewer built for speed");
            using RegistryKey applications = _software.CreateSubKey("RegisteredApplications");
            applications.SetValue(AppName, CapabilitiesPath);
        }
        else
        {
            bool empty = false;
            using (RegistryKey? applications = _software.OpenSubKey("RegisteredApplications", writable: true))
            {
                if (applications?.GetValue(AppName) != null)
                {
                    applications.DeleteValue(AppName, throwOnMissingValue: false);
                    empty = IsEmpty(applications);
                }
            }
            if (empty) _software.DeleteSubKey("RegisteredApplications", throwOnMissingSubKey: false);
            _software.DeleteSubKeyTree(CapabilitiesKey, throwOnMissingSubKey: false);
            _software.DeleteSubKeyTree(ReplacedKey, throwOnMissingSubKey: false);
            DeleteIfEmpty(AppName);
        }
    }

    void Register(FileType type, string executable)
    {
        string id = ProgId(type.Extension), dotted = "." + type.Extension;
        using (RegistryKey fileType = _software.CreateSubKey($@"Classes\{id}"))
        {
            fileType.SetValue("", type.TypeName);
            using (RegistryKey icon = fileType.CreateSubKey("DefaultIcon")) icon.SetValue("", IconOf(type.Extension), RegistryValueKind.ExpandString);
            using RegistryKey command = fileType.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{executable}\" \"%1\"");
        }

        using RegistryKey ending = _software.CreateSubKey($@"Classes\{dotted}");
        using (RegistryKey openWith = ending.CreateSubKey("OpenWithProgids")) openWith.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);

        // What the default was is kept once, when it is taken: registering again must not record the viewer as its own predecessor.
        string previous = ending.GetValue("") as string ?? "";
        if (!string.Equals(previous, id, StringComparison.OrdinalIgnoreCase))
        {
            using RegistryKey replaced = _software.CreateSubKey(ReplacedKey);
            replaced.SetValue(dotted, previous);
            ending.SetValue("", id);
        }

        using RegistryKey associations = _software.CreateSubKey(AssociationsKey);
        associations.SetValue(dotted, id);
    }

    void Unregister(string extension)
    {
        string id = ProgId(extension), dotted = "." + extension;
        string? previous;
        using (RegistryKey? replaced = _software.OpenSubKey(ReplacedKey, writable: true))
        {
            previous = replaced?.GetValue(dotted) as string;
            replaced?.DeleteValue(dotted, throwOnMissingValue: false);
        }

        // Keys that hold nothing once the viewer's entries are out go too; keys it found empty and never wrote to stay.
        bool endingEmpty = false;
        using (RegistryKey? ending = _software.OpenSubKey($@"Classes\{dotted}", writable: true))
        {
            if (ending != null)
            {
                bool changed = false, openWithEmpty = false;
                // Only while the default is still the viewer's: a program registered since may have taken it, and keeps it.
                if (string.Equals(ending.GetValue("") as string, id, StringComparison.OrdinalIgnoreCase))
                {
                    // A previous default that has been uninstalled meanwhile would open nothing.
                    if (!string.IsNullOrEmpty(previous) && FileTypeExists(previous)) ending.SetValue("", previous);
                    else ending.DeleteValue("", throwOnMissingValue: false);
                    changed = true;
                }
                using (RegistryKey? openWith = ending.OpenSubKey("OpenWithProgids", writable: true))
                {
                    if (openWith?.GetValue(id) != null)
                    {
                        openWith.DeleteValue(id, throwOnMissingValue: false);
                        openWithEmpty = IsEmpty(openWith);
                        changed = true;
                    }
                }
                if (openWithEmpty) ending.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
                endingEmpty = changed && IsEmpty(ending);
            }
        }
        if (endingEmpty) _software.DeleteSubKey($@"Classes\{dotted}", throwOnMissingSubKey: false);

        _software.DeleteSubKeyTree($@"Classes\{id}", throwOnMissingSubKey: false);
        using (RegistryKey? associations = _software.OpenSubKey(AssociationsKey, writable: true))
            associations?.DeleteValue(dotted, throwOnMissingValue: false);

        // The shell notes the programs it has offered for an ending in a list of its own, where the viewer's name would
        // outlast it. A list it made for that name alone goes with it.
        try
        {
            using RegistryKey? noted = _software.OpenSubKey($@"Microsoft\Windows\CurrentVersion\Explorer\FileExts\{dotted}", writable: true);
            bool empty = false;
            using (RegistryKey? offered = noted?.OpenSubKey("OpenWithProgids", writable: true))
            {
                if (offered?.GetValue(id) != null)
                {
                    offered.DeleteValue(id, throwOnMissingValue: false);
                    empty = IsEmpty(offered);
                }
            }
            if (empty) noted!.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Explorer's key, and a name in it that nothing answers to does no harm.
        }
    }

    /// <summary>Whether a file type of that name is registered, for the user or for the machine.</summary>
    bool FileTypeExists(string id)
    {
        try
        {
            using RegistryKey? user = _software.OpenSubKey(@"Classes\" + id);
            if (user != null) return true;
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + id);
            return machine != null;
        }
        catch (ArgumentException)
        {
            return false; // not a name a key can have
        }
    }

    void DeleteIfEmpty(string name)
    {
        bool empty;
        using (RegistryKey? key = _software.OpenSubKey(name)) empty = key != null && IsEmpty(key);
        if (empty) _software.DeleteSubKey(name, throwOnMissingSubKey: false);
    }

    static bool IsEmpty(RegistryKey key) => key.ValueCount == 0 && key.SubKeyCount == 0;

    /// <summary>
    /// The icon of the files: the one Windows itself gives image files, so that they go on looking
    /// like pictures. The viewer has no icon of its own to offer.
    /// </summary>
    static string IconOf(string extension) => extension switch
    {
        "ico" => "%1", // an icon file shows itself
        "jpg" or "jpeg" or "jpe" or "jfif" => @"%SystemRoot%\System32\imageres.dll,-72",
        "png" => @"%SystemRoot%\System32\imageres.dll,-83",
        "gif" => @"%SystemRoot%\System32\imageres.dll,-71",
        "tif" or "tiff" => @"%SystemRoot%\System32\imageres.dll,-122",
        _ => @"%SystemRoot%\System32\imageres.dll,-70",
    };
}
