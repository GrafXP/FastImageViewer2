using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Fiv.Indexing;

/// <summary>
/// The files of one directory as flat arrays: no object per file. Names live in one character
/// arena. Size and modified time come from the directory entries themselves, so reading a
/// directory never opens or stats a file.
/// </summary>
public sealed unsafe partial class FolderListing
{
    char[] _names = new char[1 << 14];
    int[] _nameStart = new int[513];
    long[] _size = new long[512];
    long[] _modified = new long[512];
    int _nameLength;

    public int Count { get; private set; }

    public ReadOnlySpan<char> Name(int index) => _names.AsSpan(_nameStart[index], _nameStart[index + 1] - _nameStart[index]);
    public long Size(int index) => _size[index];

    /// <summary>Last write time as a Windows FILETIME (100 ns ticks since 1601, UTC).</summary>
    public long ModifiedFileTime(int index) => _modified[index];

    void Add(ReadOnlySpan<char> name, long size, long modified)
    {
        if (Count == _size.Length)
        {
            int capacity = Count * 2;
            Array.Resize(ref _size, capacity);
            Array.Resize(ref _modified, capacity);
            Array.Resize(ref _nameStart, capacity + 1);
        }
        if (_nameLength + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _nameLength + name.Length));
        name.CopyTo(_names.AsSpan(_nameLength));
        _nameStart[Count] = _nameLength;
        _nameLength += name.Length;
        _nameStart[Count + 1] = _nameLength;
        _size[Count] = size;
        _modified[Count] = modified;
        Count++;
    }

    /// <summary>
    /// Reads a directory in the order the file system returns it. Subdirectories are skipped;
    /// with <paramref name="imagesOnly"/>, so is everything <see cref="ImageFormats"/> does not know.
    /// </summary>
    public static FolderListing Read(string directory, bool imagesOnly = true)
    {
        const uint FileListDirectory = 1, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
        const uint AttributeDirectory = 0x10;
        const int FileFullDirectoryInfo = 14, ErrorNoMoreFiles = 18;
        const int BufferSize = 256 * 1024;

        nint handle = CreateFileW(directory, FileListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open folder '{directory}': {new Win32Exception(error).Message}");
        }
        var listing = new FolderListing();
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            while (GetFileInformationByHandleEx(handle, FileFullDirectoryInfo, buffer, BufferSize) != 0)
            {
                for (var entry = (FileFullDirInfo*)buffer; ; entry = (FileFullDirInfo*)((byte*)entry + entry->NextEntryOffset))
                {
                    if ((entry->FileAttributes & AttributeDirectory) == 0)
                    {
                        var name = new ReadOnlySpan<char>((byte*)entry + FileFullDirInfo.FileNameOffset, (int)entry->FileNameLength / 2);
                        if (!imagesOnly || ImageFormats.IsImageFileName(name)) listing.Add(name, entry->EndOfFile, entry->LastWriteTime);
                    }
                    if (entry->NextEntryOffset == 0) break;
                }
            }
            int end = Marshal.GetLastPInvokeError();
            if (end != ErrorNoMoreFiles) throw new IOException($"Cannot read folder '{directory}': {new Win32Exception(end).Message}");
            return listing;
        }
        finally
        {
            NativeMemory.Free(buffer);
            CloseHandle(handle);
        }
    }

    /// <summary>FILE_FULL_DIR_INFO without its trailing name.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FileFullDirInfo
    {
        public const int FileNameOffset = 68;
        public uint NextEntryOffset, FileIndex;
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime, EndOfFile, AllocationSize;
        public uint FileAttributes, FileNameLength, EaSize;
    }

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int GetFileInformationByHandleEx(nint file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32")]
    private static partial int CloseHandle(nint handle);
}
