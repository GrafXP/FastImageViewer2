using System.Diagnostics;

namespace Fiv.App;

/// <summary>What can be done with a file from the viewer, beyond looking at it.</summary>
internal static class FileActions
{
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
}
