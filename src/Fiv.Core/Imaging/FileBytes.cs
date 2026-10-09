using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Fiv.Imaging;

/// <summary>
/// A whole file in native memory, shared by the decodes that run on it. Each user calls
/// <see cref="AddReference"/> before starting and <see cref="Release"/> when done; the creator
/// holds the first reference.
/// </summary>
public sealed unsafe class FileBytes
{
    int _references = 1;

    public byte* Data { get; private set; }
    public nuint Length { get; }

    FileBytes(byte* data, nuint length)
    {
        Data = data;
        Length = length;
    }

    public static FileBytes Read(string path)
    {
        HANDLE file;
        fixed (char* p = path)
        {
            file = PInvoke.CreateFile(p, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_SEQUENTIAL_SCAN, default);
        }
        if ((nint)file.Value == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open '{path}': {new Win32Exception(error).Message}");
        }
        byte* data = null;
        try
        {
            long size;
            if (!PInvoke.GetFileSizeEx(file, &size)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            data = (byte*)NativeMemory.Alloc((nuint)Math.Max(size, 1));
            for (long offset = 0; offset < size;)
            {
                uint got = 0;
                uint want = (uint)Math.Min(size - offset, 1 << 30);
                if (!PInvoke.ReadFile(file, data + offset, want, &got, null)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (got == 0) throw new EndOfStreamException($"'{path}' ended after {offset} of {size} bytes.");
                offset += got;
            }
            var result = new FileBytes(data, (nuint)size);
            data = null;
            return result;
        }
        finally
        {
            NativeMemory.Free(data);
            PInvoke.CloseHandle(file);
        }
    }

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        NativeMemory.Free(Data);
        Data = null;
    }
}
