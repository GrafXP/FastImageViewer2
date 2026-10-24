using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;

namespace Fiv.Imaging;

/// <summary>
/// Decoding through the Windows Imaging Component: PNG, TIFF, BMP, GIF, ICO, JPEG XR, and what the
/// bundled decoders (<see cref="NativeDecoder"/>) do not read.
/// 16-bit and float grayscale stay as they are; other formats become BGRA8, or RGBA16 if the
/// file has more than 8 bits per channel.
/// </summary>
public static unsafe class WicDecoder
{
    [ThreadStatic] static IWICImagingFactory* t_factory;

    static IWICImagingFactory* Factory()
    {
        if (t_factory == null)
        {
            // S_FALSE (already initialised) and RPC_E_CHANGED_MODE (thread is STA) are both fine for WIC.
            PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            Guid clsid = PInvoke.CLSID_WICImagingFactory, iid = IWICImagingFactory.IID_Guid;
            IWICImagingFactory* factory;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).ThrowOnFailure();
            t_factory = factory;
        }
        return t_factory;
    }

    public static DecodedImage Decode(byte* data, nuint size)
    {
        IWICImagingFactory* factory = Factory();
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICFormatConverter* converter = null;
        try
        {
            factory->CreateStream(&stream).ThrowOnFailure();
            stream->InitializeFromMemory(data, checked((uint)size)).ThrowOnFailure();
            factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).ThrowOnFailure();
            decoder->GetFrame(0, &frame).ThrowOnFailure();
            uint width, height;
            frame->GetSize(&width, &height).ThrowOnFailure();
            Guid sourceFormat;
            frame->GetPixelFormat(&sourceFormat).ThrowOnFailure();

            (PixelLayout layout, Guid targetFormat) = Target(factory, sourceFormat);
            var source = (IWICBitmapSource*)frame;
            if (sourceFormat != targetFormat)
            {
                factory->CreateFormatConverter(&converter).ThrowOnFailure();
                converter->Initialize(source, &targetFormat, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0,
                    WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnFailure();
                source = (IWICBitmapSource*)converter;
            }

            var image = new DecodedImage((int)width, (int)height, layout, (int)width, (int)height);
            // CopyPixels takes a 32-bit buffer size, so large images are copied in bands.
            int bandRows = Math.Max(1, (int)Math.Min(height, (1u << 30) / (uint)image.Stride));
            for (int y = 0; y < height; y += bandRows)
            {
                var rect = new WICRect { X = 0, Y = y, Width = (int)width, Height = Math.Min(bandRows, (int)height - y) };
                HRESULT hr = source->CopyPixels(&rect, (uint)image.Stride, (uint)(image.Stride * rect.Height),
                    image.Pixels + (long)y * image.Stride);
                if (hr.Failed)
                {
                    image.Dispose();
                    hr.ThrowOnFailure();
                }
            }
            return image;
        }
        finally
        {
            if (converter != null) converter->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
        }
    }

    /// <summary>
    /// Decodes straight to a BGRA8 image no larger than <paramref name="maxSize"/> on its long
    /// side, letting WIC average the pixels down as it decodes, so the full image never has to be
    /// held. Returns null for files with more than 8 bits per channel: those are decoded in full
    /// and reduced with their brightness range taken into account.
    /// </summary>
    public static DecodedImage? DecodeReduced(byte* data, nuint size, int maxSize)
    {
        IWICImagingFactory* factory = Factory();
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICFormatConverter* converter = null;
        IWICBitmapScaler* scaler = null;
        try
        {
            factory->CreateStream(&stream).ThrowOnFailure();
            stream->InitializeFromMemory(data, checked((uint)size)).ThrowOnFailure();
            factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).ThrowOnFailure();
            decoder->GetFrame(0, &frame).ThrowOnFailure();
            uint width, height;
            frame->GetSize(&width, &height).ThrowOnFailure();
            Guid sourceFormat;
            frame->GetPixelFormat(&sourceFormat).ThrowOnFailure();
            (PixelLayout layout, _) = Target(factory, sourceFormat);
            if (layout is PixelLayout.Gray16 or PixelLayout.GrayFloat or PixelLayout.Rgba16) return null;

            // To BGRA first (any palette or bit depth becomes something the scaler takes), then reduce.
            var source = (IWICBitmapSource*)frame;
            Guid bgra = PInvoke.GUID_WICPixelFormat32bppBGRA;
            if (sourceFormat != bgra)
            {
                factory->CreateFormatConverter(&converter).ThrowOnFailure();
                converter->Initialize(source, &bgra, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0,
                    WICBitmapPaletteType.WICBitmapPaletteTypeCustom).ThrowOnFailure();
                source = (IWICBitmapSource*)converter;
            }
            (int targetWidth, int targetHeight) = Resampler.FitWithin((int)width, (int)height, maxSize);
            if (targetWidth != width || targetHeight != height)
            {
                factory->CreateBitmapScaler(&scaler).ThrowOnFailure();
                scaler->Initialize(source, (uint)targetWidth, (uint)targetHeight, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).ThrowOnFailure();
                source = (IWICBitmapSource*)scaler;
            }

            var image = new DecodedImage(targetWidth, targetHeight, PixelLayout.Bgra8, (int)width, (int)height);
            HRESULT hr = source->CopyPixels(null, (uint)image.Stride, (uint)image.ByteCount, image.Pixels);
            if (hr.Failed)
            {
                image.Dispose();
                hr.ThrowOnFailure();
            }
            return image;
        }
        finally
        {
            if (scaler != null) scaler->Release();
            if (converter != null) converter->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
        }
    }

    static (PixelLayout, Guid) Target(IWICImagingFactory* factory, Guid sourceFormat)
    {
        if (sourceFormat == PInvoke.GUID_WICPixelFormat32bppBGRA) return (PixelLayout.Bgra8, sourceFormat);
        if (sourceFormat == PInvoke.GUID_WICPixelFormat8bppGray) return (PixelLayout.Gray8, sourceFormat);
        if (sourceFormat == PInvoke.GUID_WICPixelFormat16bppGray) return (PixelLayout.Gray16, sourceFormat);
        if (sourceFormat == PInvoke.GUID_WICPixelFormat32bppGrayFloat) return (PixelLayout.GrayFloat, sourceFormat);
        if (sourceFormat == PInvoke.GUID_WICPixelFormat64bppRGBA) return (PixelLayout.Rgba16, sourceFormat);

        // Anything else: keep more than 8 bits per channel if the file has them.
        uint bitsPerPixel = 0, channels = 0;
        IWICComponentInfo* info = null;
        IWICPixelFormatInfo* formatInfo = null;
        try
        {
            Guid iid = IWICPixelFormatInfo.IID_Guid;
            if (factory->CreateComponentInfo(&sourceFormat, &info).Succeeded &&
                info->QueryInterface(&iid, (void**)&formatInfo).Succeeded)
            {
                formatInfo->GetBitsPerPixel(&bitsPerPixel);
                formatInfo->GetChannelCount(&channels);
            }
        }
        finally
        {
            if (formatInfo != null) formatInfo->Release();
            if (info != null) info->Release();
        }
        bool deep = channels > 0 && bitsPerPixel / channels > 8;
        return deep
            ? (PixelLayout.Rgba16, PInvoke.GUID_WICPixelFormat64bppRGBA)
            : (PixelLayout.Bgra8, PInvoke.GUID_WICPixelFormat32bppBGRA);
    }
}
