using System.Diagnostics;
using System.Runtime.InteropServices;
using Fiv.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;

namespace Fiv.App;

/// <summary>What can be done with files from the viewer beyond looking at them. All of it on the UI thread.</summary>
internal static unsafe class FileActions
{
    static bool s_comReady;

    /// <summary>The shell's dialogs need COM in single-threaded mode on the thread that shows them.</summary>
    static void EnsureCom()
    {
        if (s_comReady) return;
        s_comReady = true;
        PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
    }

    /// <summary>Opens the file's folder in Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>The system's Open dialog for one or more files, or for a folder. Empty if cancelled.</summary>
    public static List<string> Pick(HWND owner, bool folder)
    {
        var result = new List<string>();
        EnsureCom();
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"); // CLSID_FileOpenDialog
        Guid iid = IFileOpenDialog.IID_Guid;
        IFileOpenDialog* dialog = null;
        IShellItemArray* items = null;
        try
        {
            if (PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&dialog).Failed) return result;
            FILEOPENDIALOGOPTIONS options;
            dialog->GetOptions(&options);
            options |= FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST;
            options |= folder ? FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS : FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST;
            dialog->SetOptions(options);
            if (dialog->Show(owner).Failed) return result; // cancelled
            if (dialog->GetResults(&items).Failed) return result;
            uint count;
            items->GetCount(&count);
            for (uint i = 0; i < count; i++)
            {
                IShellItem* item;
                if (items->GetItemAt(i, &item).Failed) continue;
                PWSTR name;
                if (item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name).Succeeded)
                {
                    result.Add(new string(name.Value));
                    PInvoke.CoTaskMemFree(name.Value);
                }
                item->Release();
            }
            return result;
        }
        finally
        {
            if (items != null) items->Release();
            if (dialog != null) dialog->Release();
        }
    }

    /// <summary>
    /// Moves files to the Recycle Bin. True if the shell reports that all of them went. Where
    /// there is no bin (some removable and network drives), the shell asks before deleting for
    /// good: the viewer never destroys a file silently.
    /// </summary>
    public static bool Recycle(HWND owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;
        // The shell wants the paths one after another, each ended by a zero, the list by one more.
        string joined = string.Join('\0', paths) + "\0\0";
        fixed (char* from = joined)
        {
            var operation = new SHFILEOPSTRUCTW
            {
                hwnd = owner,
                wFunc = 3, // FO_DELETE
                pFrom = from,
                fFlags = 0x0040 | 0x0010 | 0x0004 | 0x4000, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING
            };
            return PInvoke.SHFileOperation(&operation) == 0 && !operation.fAnyOperationsAborted;
        }
    }

    /// <summary>Renames a file within its folder. Returns null on success, otherwise why it failed.</summary>
    public static string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "That is not a valid file name.";
        string target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return null;
        try
        {
            // A change of letter case only is still a rename.
            if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return $"'{newName}' already exists.";
            File.Move(path, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Puts text on the clipboard.</summary>
    public static bool CopyText(HWND owner, string text)
    {
        nuint size = (nuint)(text.Length + 1) * sizeof(char);
        HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, size);
        if (memory.IsNull) return false;
        char* target = (char*)PInvoke.GlobalLock(memory);
        if (target == null)
        {
            PInvoke.GlobalFree(memory);
            return false;
        }
        text.AsSpan().CopyTo(new Span<char>(target, text.Length));
        target[text.Length] = '\0';
        PInvoke.GlobalUnlock(memory);
        if (!PInvoke.OpenClipboard(owner))
        {
            PInvoke.GlobalFree(memory);
            return false;
        }
        PInvoke.EmptyClipboard();
        bool ok = !PInvoke.SetClipboardData(13 /* CF_UNICODETEXT */, new HANDLE(memory.Value)).IsNull;
        if (!ok) PInvoke.GlobalFree(memory);
        PInvoke.CloseClipboard();
        return ok;
    }

    /// <summary>Puts an upright BGRA8 image on the clipboard as a device-independent bitmap.</summary>
    public static bool CopyToClipboard(HWND owner, DecodedImage image)
    {
        if (image.Layout != PixelLayout.Bgra8) return false;
        int rowBytes = image.Width * 4;
        nuint size = (nuint)sizeof(BITMAPINFOHEADER) + (nuint)rowBytes * (nuint)image.Height;
        HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, size);
        if (memory.IsNull) return false;
        byte* target = (byte*)PInvoke.GlobalLock(memory);
        if (target == null)
        {
            PInvoke.GlobalFree(memory);
            return false;
        }
        var header = (BITMAPINFOHEADER*)target;
        *header = default;
        header->biSize = (uint)sizeof(BITMAPINFOHEADER);
        header->biWidth = image.Width;
        header->biHeight = image.Height; // positive: rows stored bottom-up, which every program accepts
        header->biPlanes = 1;
        header->biBitCount = 32;
        byte* pixels = target + sizeof(BITMAPINFOHEADER);
        for (int y = 0; y < image.Height; y++)
            Buffer.MemoryCopy(image.Pixels + (long)(image.Height - 1 - y) * image.Stride, pixels + (long)y * rowBytes, rowBytes, rowBytes);
        PInvoke.GlobalUnlock(memory);

        if (!PInvoke.OpenClipboard(owner))
        {
            PInvoke.GlobalFree(memory);
            return false;
        }
        PInvoke.EmptyClipboard();
        bool ok = !PInvoke.SetClipboardData(8 /* CF_DIB */, new HANDLE(memory.Value)).IsNull; // the clipboard owns the memory now
        if (!ok) PInvoke.GlobalFree(memory);
        PInvoke.CloseClipboard();
        return ok;
    }
}
