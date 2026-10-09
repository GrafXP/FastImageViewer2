using System.Buffers.Binary;
using System.Text;
using Fiv.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Fiv.Thumbnails;

/// <summary>What the pack knows about one file: its image size and where its stored thumbnail is.</summary>
public readonly record struct PackEntry(long FileSize, long Modified, int Width, int Height, Orientation Orientation,
    int ThumbnailWidth, int ThumbnailHeight, long BlobOffset, int BlobLength)
{
    public bool HasThumbnail => BlobLength > 0;
}

/// <summary>
/// The thumbnails of one folder on disk, so that the folder opens with pictures the second time:
/// an index file (one small record per image: name, size, time, dimensions, where its thumbnail is)
/// and a data file of JPEG thumbnails. Opening reads only the index; thumbnails are read one by
/// one as they are needed. Both files are only ever appended to. An entry counts only if the
/// file still has the size and time it had when the entry was written.
///
/// Thread-safe. If another viewer has the pack open for writing, this one reads it and adds nothing.
/// </summary>
public sealed class ThumbnailPack : IDisposable
{
    const uint Magic = 0x54564946; // "FIVT"
    const int Version = 1;
    const int FixedRecordSize = 8 + 8 + 4 + 4 + 1 + 2 + 2 + 8 + 4 + 2; // everything but the name

    readonly object _gate = new();
    readonly Dictionary<ulong, PackEntry> _entries = [];
    readonly FileStream? _index;   // null: read-only
    readonly SafeFileHandle? _data;
    long _dataLength;
    bool _disposed;

    public string Folder { get; }
    public bool ReadOnly => _index == null;
    public int Count { get { lock (_gate) return _entries.Count; } }
    public long DataBytes => Volatile.Read(ref _dataLength);

    /// <summary>The cache directory used when none is given: %LOCALAPPDATA%\FastImageViewer2\thumbs.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastImageViewer2", "thumbs");

    ThumbnailPack(string folder, FileStream? index, SafeFileHandle? data, long dataLength)
    {
        Folder = folder;
        _index = index;
        _data = data;
        _dataLength = dataLength;
    }

    static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();

    /// <summary>Opens the pack of a folder, creating it if there is none. Never throws for a damaged or unreadable pack: it starts empty then.</summary>
    public static ThumbnailPack Open(string root, string folder)
    {
        string key = Normalize(folder);
        string stem = Path.Combine(root, Hash(key).ToString("x16"));
        string indexPath = stem + ".idx", dataPath = stem + ".dat";
        FileStream? index = null;
        SafeFileHandle? data = null;
        try
        {
            Directory.CreateDirectory(root);
            try
            {
                index = new FileStream(indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                data = File.OpenHandle(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException) when (File.Exists(indexPath))
            {
                // Another viewer writes this pack: read what it has.
                index?.Dispose();
                data?.Dispose();
                index = null;
                data = File.Exists(dataPath) ? File.OpenHandle(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
                using var readOnly = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var shared = new ThumbnailPack(folder, null, data, data == null ? 0 : RandomAccess.GetLength(data));
                shared.Load(readOnly, key);
                return shared;
            }

            var pack = new ThumbnailPack(folder, index, data, RandomAccess.GetLength(data));
            if (index.Length == 0 || !pack.Load(index, key))
            {
                // New, or not ours (damaged, another version, or a hash collision with another folder): start over.
                lock (pack._gate) pack._entries.Clear();
                index.SetLength(0);
                RandomAccess.SetLength(data, 0);
                pack._dataLength = 0;
                WriteHeader(index, key);
            }
            index.Seek(0, SeekOrigin.End);
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow); // marks the pack as used, for the size cap
            return pack;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            index?.Dispose();
            data?.Dispose();
            return new ThumbnailPack(folder, null, null, 0); // no cache: thumbnails are made every time
        }
    }

    static void WriteHeader(FileStream index, string key)
    {
        byte[] name = Encoding.UTF8.GetBytes(key);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], name.Length);
        index.Write(header);
        index.Write(name);
        index.Flush();
    }

    /// <summary>Reads the whole index. False if the file is not the index of this folder.</summary>
    bool Load(FileStream index, string key)
    {
        index.Seek(0, SeekOrigin.Begin);
        byte[] bytes = new byte[index.Length];
        index.ReadExactly(bytes);
        ReadOnlySpan<byte> all = bytes;
        if (all.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(all) != Magic || BinaryPrimitives.ReadInt32LittleEndian(all[4..]) != Version) return false;
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(all[8..]);
        if (nameLength < 0 || 12 + nameLength > all.Length || !all.Slice(12, nameLength).SequenceEqual(Encoding.UTF8.GetBytes(key))) return false;

        long dataLength = _dataLength;
        int position = 12 + nameLength;
        lock (_gate)
        {
            while (position + FixedRecordSize <= all.Length)
            {
                ReadOnlySpan<byte> r = all[position..];
                int nameChars = BinaryPrimitives.ReadUInt16LittleEndian(r[41..]);
                int recordSize = FixedRecordSize + nameChars * 2;
                if (position + recordSize > all.Length) break; // a record cut short by a crash: ignore it and what follows
                var entry = new PackEntry(
                    BinaryPrimitives.ReadInt64LittleEndian(r), BinaryPrimitives.ReadInt64LittleEndian(r[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(r[16..]), BinaryPrimitives.ReadInt32LittleEndian(r[20..]), (Orientation)r[24],
                    BinaryPrimitives.ReadUInt16LittleEndian(r[25..]), BinaryPrimitives.ReadUInt16LittleEndian(r[27..]),
                    BinaryPrimitives.ReadInt64LittleEndian(r[29..]), BinaryPrimitives.ReadInt32LittleEndian(r[37..]));
                // A thumbnail the data file does not hold (the index got ahead of it in a crash) does not count.
                if (entry.BlobLength == 0 || entry.BlobOffset + entry.BlobLength <= dataLength)
                    _entries[Hash(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(r.Slice(FixedRecordSize, nameChars * 2)))] = entry;
                position += recordSize;
            }
        }
        return true;
    }

    /// <summary>The entry of a file, if it was written for the file as it is now.</summary>
    public bool TryGet(ReadOnlySpan<char> name, long fileSize, long modified, out PackEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Hash(name), out entry) && entry.FileSize == fileSize && entry.Modified == modified;
        }
    }

    /// <summary>The stored thumbnail of an entry as JPEG bytes; false if it cannot be read.</summary>
    public bool TryReadThumbnail(in PackEntry entry, Span<byte> buffer)
    {
        if (_data == null || !entry.HasThumbnail || buffer.Length < entry.BlobLength) return false;
        try
        {
            return RandomAccess.Read(_data, buffer[..entry.BlobLength], entry.BlobOffset) == entry.BlobLength;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records a file's image size and, if <paramref name="jpeg"/> is not empty, its thumbnail
    /// (<paramref name="thumbnailWidth"/> x <paramref name="thumbnailHeight"/>). Replaces an older entry of the same name.
    /// </summary>
    public void Add(ReadOnlySpan<char> name, long fileSize, long modified, int width, int height, Orientation orientation,
        int thumbnailWidth, int thumbnailHeight, ReadOnlySpan<byte> jpeg)
    {
        if (_index == null || _data == null || name.Length > ushort.MaxValue) return;
        Span<byte> record = stackalloc byte[FixedRecordSize];
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                long offset = _dataLength;
                if (!jpeg.IsEmpty)
                {
                    RandomAccess.Write(_data, jpeg, offset);
                    _dataLength = offset + jpeg.Length;
                }
                var entry = new PackEntry(fileSize, modified, width, height, orientation, thumbnailWidth, thumbnailHeight, jpeg.IsEmpty ? 0 : offset, jpeg.Length);
                BinaryPrimitives.WriteInt64LittleEndian(record, fileSize);
                BinaryPrimitives.WriteInt64LittleEndian(record[8..], modified);
                BinaryPrimitives.WriteInt32LittleEndian(record[16..], width);
                BinaryPrimitives.WriteInt32LittleEndian(record[20..], height);
                record[24] = (byte)orientation;
                BinaryPrimitives.WriteUInt16LittleEndian(record[25..], (ushort)thumbnailWidth);
                BinaryPrimitives.WriteUInt16LittleEndian(record[27..], (ushort)thumbnailHeight);
                BinaryPrimitives.WriteInt64LittleEndian(record[29..], entry.BlobOffset);
                BinaryPrimitives.WriteInt32LittleEndian(record[37..], entry.BlobLength);
                BinaryPrimitives.WriteUInt16LittleEndian(record[41..], (ushort)name.Length);
                _index.Write(record);
                _index.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name));
                _entries[Hash(name)] = entry;
            }
            catch (IOException)
            {
                // Disk full or gone: the thumbnail simply is not cached.
            }
        }
    }

    /// <summary>Hands what was added to the operating system. Called now and then, and on close.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _index?.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _index?.Flush();
            }
            catch (IOException)
            {
            }
            _disposed = true;
            _index?.Dispose();
            _data?.Dispose();
        }
    }

    /// <summary>
    /// Deletes the packs that were used longest ago until the cache directory holds at most
    /// <paramref name="maxBytes"/>. Packs in use by a running viewer cannot be deleted and are skipped.
    /// </summary>
    public static void Trim(string root, long maxBytes)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var packs = new DirectoryInfo(root).GetFiles("*.idx")
                .Select(index => (Index: index, Data: new FileInfo(Path.ChangeExtension(index.FullName, ".dat"))))
                .Select(p => (p.Index, p.Data, Bytes: p.Index.Length + (p.Data.Exists ? p.Data.Length : 0)))
                .OrderBy(p => p.Index.LastWriteTimeUtc).ToList();
            long total = packs.Sum(p => p.Bytes);
            foreach (var pack in packs)
            {
                if (total <= maxBytes) break;
                try
                {
                    pack.Index.Delete();
                    if (pack.Data.Exists) pack.Data.Delete();
                    total -= pack.Bytes;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>FNV-1a over the upper-cased characters: the same for names that differ only in case.</summary>
    public static ulong Hash(ReadOnlySpan<char> text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text)
        {
            char upper = (uint)(c - 'a') <= 'z' - 'a' ? (char)(c - 32) : c < 128 ? c : char.ToUpperInvariant(c);
            hash = (hash ^ (byte)upper) * 1099511628211;
            hash = (hash ^ (byte)(upper >> 8)) * 1099511628211;
        }
        return hash;
    }
}
