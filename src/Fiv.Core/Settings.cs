using System.Text.Json;
using Fiv.Indexing;

namespace Fiv;

/// <summary>
/// What the viewer remembers between runs: where its window was, which panels were open and how
/// they were set, and what was opened recently. Stored as settings.json in the profile directory.
/// Read and written with the JSON reader and writer directly, not the serializer: the file is
/// read before the window exists, on the way to the first picture, and this costs microseconds.
/// </summary>
public sealed class Settings
{
    public const int MaxRecent = 20;

    // Window: position and size of the restored window in screen pixels; 0 size means "never saved".
    public int WindowX, WindowY, WindowWidth, WindowHeight;
    public bool Maximized;

    // Where the image area was inside the client area, as fractions, so that the first picture
    // of the next run is fitted to it before the panels are there.
    public double ViewLeft, ViewTop, ViewRight = 1, ViewBottom = 1;

    public bool ShowBrowser = true, ShowFilmstrip;
    public bool BrowserList;
    public int ThumbnailSize = 128;
    public FolderSort Sort = FolderSort.Name;
    public bool SortDescending;
    public bool KeepView;
    public bool ExactPixels = true;
    public int CacheMb = 4096, VramMb = 2048, ThumbnailCacheMb = 2048;
    public List<string> RecentFiles = [], RecentFolders = [];

    /// <summary>Moves an entry to the front of a recent list, case-insensitively, and trims the list.</summary>
    public static void Remember(List<string> list, string path)
    {
        list.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }

    /// <summary>Loads the settings; a missing or damaged file gives the defaults.</summary>
    public static Settings Load(string path)
    {
        var settings = new Settings();
        byte[] json;
        try
        {
            json = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return settings;
        }
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return settings;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string? name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "windowX": settings.WindowX = reader.GetInt32(); break;
                    case "windowY": settings.WindowY = reader.GetInt32(); break;
                    case "windowWidth": settings.WindowWidth = reader.GetInt32(); break;
                    case "windowHeight": settings.WindowHeight = reader.GetInt32(); break;
                    case "maximized": settings.Maximized = reader.GetBoolean(); break;
                    case "viewLeft": settings.ViewLeft = reader.GetDouble(); break;
                    case "viewTop": settings.ViewTop = reader.GetDouble(); break;
                    case "viewRight": settings.ViewRight = reader.GetDouble(); break;
                    case "viewBottom": settings.ViewBottom = reader.GetDouble(); break;
                    case "showBrowser": settings.ShowBrowser = reader.GetBoolean(); break;
                    case "showFilmstrip": settings.ShowFilmstrip = reader.GetBoolean(); break;
                    case "browserList": settings.BrowserList = reader.GetBoolean(); break;
                    case "thumbnailSize": settings.ThumbnailSize = Math.Clamp(reader.GetInt32() / 16 * 16, 64, 256); break;
                    case "sort": settings.Sort = Enum.TryParse(reader.GetString(), true, out FolderSort sort) ? sort : FolderSort.Name; break;
                    case "sortDescending": settings.SortDescending = reader.GetBoolean(); break;
                    case "keepView": settings.KeepView = reader.GetBoolean(); break;
                    case "exactPixels": settings.ExactPixels = reader.GetBoolean(); break;
                    case "cacheMb": settings.CacheMb = Math.Max(0, reader.GetInt32()); break;
                    case "vramMb": settings.VramMb = Math.Max(0, reader.GetInt32()); break;
                    case "thumbnailCacheMb": settings.ThumbnailCacheMb = Math.Max(0, reader.GetInt32()); break;
                    case "recentFiles": ReadList(ref reader, settings.RecentFiles); break;
                    case "recentFolders": ReadList(ref reader, settings.RecentFolders); break;
                    default: reader.Skip(); break; // a setting of a newer version: leave it
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // A damaged file: keep what was read so far, defaults for the rest.
        }
        if (!(settings.ViewRight > settings.ViewLeft && settings.ViewBottom > settings.ViewTop &&
              settings.ViewLeft >= 0 && settings.ViewTop >= 0 && settings.ViewRight <= 1 && settings.ViewBottom <= 1))
        {
            (settings.ViewLeft, settings.ViewTop, settings.ViewRight, settings.ViewBottom) = (0, 0, 1, 1);
        }
        return settings;
    }

    static void ReadList(ref Utf8JsonReader reader, List<string> list)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } item && list.Count < MaxRecent) list.Add(item);
            else reader.Skip();
        }
    }

    /// <summary>Writes the settings. Failing to (read-only profile, disk full) is not an error worth reporting.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("windowX", WindowX);
                writer.WriteNumber("windowY", WindowY);
                writer.WriteNumber("windowWidth", WindowWidth);
                writer.WriteNumber("windowHeight", WindowHeight);
                writer.WriteBoolean("maximized", Maximized);
                writer.WriteNumber("viewLeft", Math.Round(ViewLeft, 5));
                writer.WriteNumber("viewTop", Math.Round(ViewTop, 5));
                writer.WriteNumber("viewRight", Math.Round(ViewRight, 5));
                writer.WriteNumber("viewBottom", Math.Round(ViewBottom, 5));
                writer.WriteBoolean("showBrowser", ShowBrowser);
                writer.WriteBoolean("showFilmstrip", ShowFilmstrip);
                writer.WriteBoolean("browserList", BrowserList);
                writer.WriteNumber("thumbnailSize", ThumbnailSize);
                writer.WriteString("sort", Sort.ToString());
                writer.WriteBoolean("sortDescending", SortDescending);
                writer.WriteBoolean("keepView", KeepView);
                writer.WriteBoolean("exactPixels", ExactPixels);
                writer.WriteNumber("cacheMb", CacheMb);
                writer.WriteNumber("vramMb", VramMb);
                writer.WriteNumber("thumbnailCacheMb", ThumbnailCacheMb);
                writer.WriteStartArray("recentFiles");
                foreach (string file in RecentFiles) writer.WriteStringValue(file);
                writer.WriteEndArray();
                writer.WriteStartArray("recentFolders");
                foreach (string folder in RecentFolders) writer.WriteStringValue(folder);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            // Written whole or not at all: to a temporary file first, then renamed over the old one.
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
