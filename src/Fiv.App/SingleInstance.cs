using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.DataExchange;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Fiv.App;

/// <summary>
/// One viewer per session. A second launch hands its files to the first one's window with
/// WM_COPYDATA and exits, which makes opening a file with the viewer already running cost a
/// process start plus the decode.
/// </summary>
internal static unsafe class SingleInstance
{
    /// <summary>The window class of an instance group; forwarding finds the running viewer by it.</summary>
    public static string WindowClass(string instance) => instance.Length == 0 ? "FastImageViewer2" : "FastImageViewer2." + instance;
    public const nuint CopyDataId = 0x46495632; // "FIV2"
    const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Returns true if another instance took the files. Otherwise this process is now the
    /// instance (it holds the mutex until it exits).
    /// </summary>
    public static bool TryForward(List<string> files, string instance)
    {
        fixed (char* name = "Local\\" + WindowClass(instance) + ".Instance")
        {
            PInvoke.CreateMutex(null, false, name);
            if (Marshal.GetLastPInvokeError() != ErrorAlreadyExists) return false;
        }

        string payload = string.Join('\n', files);
        // The other instance may still be creating its window.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            HWND window;
            fixed (char* className = WindowClass(instance)) window = PInvoke.FindWindow(className, null);
            if (!window.IsNull)
            {
                uint processId;
                PInvoke.GetWindowThreadProcessId(window, &processId);
                PInvoke.AllowSetForegroundWindow(processId);
                fixed (char* text = payload)
                {
                    var data = new COPYDATASTRUCT { dwData = CopyDataId, cbData = (uint)(payload.Length * sizeof(char)), lpData = text };
                    nuint result = 0;
                    LRESULT sent = PInvoke.SendMessageTimeout(window, PInvoke.WM_COPYDATA, default, (nint)(&data),
                        SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 5000, &result);
                    if (sent.Value != 0 && result == 1) return true;
                }
                return false; // it did not answer: open the files here
            }
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>The paths of a WM_COPYDATA message from <see cref="TryForward"/>; null if it is not one.</summary>
    public static string[]? ReadForwarded(LPARAM lParam)
    {
        var data = (COPYDATASTRUCT*)lParam.Value;
        if (data == null || data->dwData != CopyDataId) return null;
        string payload = new((char*)data->lpData, 0, (int)data->cbData / sizeof(char));
        return payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
