using Windows.Win32.Foundation;

namespace Fiv.Render;

/// <summary>Thrown when a Direct3D or DXGI call fails; the message names the call and the HRESULT.</summary>
public sealed class GraphicsException(string call, int hresult) : Exception($"{call} failed with 0x{hresult:X8}")
{
    public int HResultCode { get; } = hresult;
}

internal static class Hr
{
    public static void Check(HRESULT hr, string call)
    {
        if (hr.Failed) throw new GraphicsException(call, hr.Value);
    }
}
