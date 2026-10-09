using Fiv.Imaging;

namespace Fiv.App;

/// <summary>
/// Where the image sits in the viewport: a zoom factor (screen pixels per image pixel) and the
/// image point at the viewport's centre, both for the upright image.
/// </summary>
internal struct ViewState
{
    public const double MinZoom = 1.0 / 64, MaxZoom = 64;

    public double Zoom;
    public double CenterX, CenterY;

    /// <summary>True while the image follows the viewport size (fit to window).</summary>
    public bool Fit;

    public void FitTo(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        Zoom = FitZoom(imageWidth, imageHeight, viewWidth, viewHeight);
        CenterX = imageWidth / 2;
        CenterY = imageHeight / 2;
        Fit = true;
    }

    /// <summary>The largest zoom that shows the whole image, never above 100 %.</summary>
    public static double FitZoom(double imageWidth, double imageHeight, double viewWidth, double viewHeight) =>
        imageWidth <= 0 || imageHeight <= 0
            ? 1
            : Math.Min(1, Math.Min(Math.Max(viewWidth, 1) / imageWidth, Math.Max(viewHeight, 1) / imageHeight));

    /// <summary>Upright image coordinates to viewport pixels, moved onto whole pixels.</summary>
    public readonly Affine2 ToScreen(double viewWidth, double viewHeight) =>
        Affine2.ScaleTranslate(Zoom, Zoom, Math.Round(viewWidth / 2 - CenterX * Zoom), Math.Round(viewHeight / 2 - CenterY * Zoom));

    /// <summary>Changes the zoom so that the image point under (<paramref name="screenX"/>, <paramref name="screenY"/>) stays there.</summary>
    public void ZoomAt(double newZoom, double screenX, double screenY, double viewWidth, double viewHeight)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        // Passing 100 % by a hair lands on it: exact pixels are what one zooms in for.
        if (Math.Abs(newZoom - 1) < 0.03) newZoom = 1;
        (double x, double y) = ToScreen(viewWidth, viewHeight).Inverse().Apply(screenX, screenY);
        Zoom = newZoom;
        CenterX = x - (screenX - viewWidth / 2) / newZoom;
        CenterY = y - (screenY - viewHeight / 2) / newZoom;
        Fit = false;
    }

    public void Pan(double screenDx, double screenDy, double imageWidth, double imageHeight)
    {
        CenterX = Math.Clamp(CenterX - screenDx / Zoom, 0, imageWidth);
        CenterY = Math.Clamp(CenterY - screenDy / Zoom, 0, imageHeight);
        Fit = false;
    }
}
