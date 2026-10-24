using Fiv.Comparing;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Fiv.Render;

/// <summary>A colour map's table in a texture of 256 by 1, as the shaders read it.</summary>
internal static unsafe class ColormapTexture
{
    public static ID3D11ShaderResourceView* Create(ID3D11Device* device, Colormap map)
    {
        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = Colormaps.Size;
        desc.Height = 1;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;

        ID3D11Texture2D* texture;
        fixed (byte* table = Colormaps.Table(map))
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = table, SysMemPitch = Colormaps.Size * 4 };
            Hr.Check(device->CreateTexture2D(&desc, &data, &texture), "CreateTexture2D");
        }
        ID3D11ShaderResourceView* view;
        HRESULT hr = device->CreateShaderResourceView((ID3D11Resource*)texture, (D3D11_SHADER_RESOURCE_VIEW_DESC*)null, &view);
        texture->Release();
        Hr.Check(hr, "CreateShaderResourceView");
        return view;
    }
}
