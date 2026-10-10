using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>How the images of a compare set are put on screen.</summary>
public enum CompareMode
{
    /// <summary>One image at a time, switched in place.</summary>
    Single,

    /// <summary>Two images, each in its own pane.</summary>
    SideBySide,

    /// <summary>All images, each in its own pane.</summary>
    Grid,

    /// <summary>Two images in one pane, one on each side of a divider.</summary>
    Split,

    /// <summary>Two images in one pane, the second over the first with an opacity.</summary>
    Blend,

    /// <summary>Two images in one pane, alternating in the cells of a checkerboard.</summary>
    Checker,

    /// <summary>The difference between two images, in the colours of a colour map.</summary>
    Difference,

    /// <summary>The structural similarity of two images, pixel by pixel, as a heat map.</summary>
    Ssim,
}

public static class CompareModeExtensions
{
    /// <summary>The last of the modes, for going through them in turn.</summary>
    public const CompareMode Last = CompareMode.Ssim;

    /// <summary>True for the modes that put two images into the same pane.</summary>
    public static bool IsOverlay(this CompareMode mode) => mode >= CompareMode.Split;

    /// <summary>True for the modes with a pane per image, where each pane can have its own zoom and position.</summary>
    public static bool HasPanes(this CompareMode mode) => mode is CompareMode.SideBySide or CompareMode.Grid;
}

/// <summary>How images of different sizes are laid over each other.</summary>
public enum SizeMatch
{
    /// <summary>Pixel for pixel, top-left corners together.</summary>
    TopLeft,

    /// <summary>Pixel for pixel, centres together.</summary>
    Centre,

    /// <summary>Each image scaled to fill the common area.</summary>
    ScaleToFit,
}

/// <summary>A pane's rectangle in window pixels.</summary>
public readonly record struct PaneRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>
/// Where the panes of a comparison go and how each image sits in the area all of them share.
/// That area, the canvas, is as wide as the widest image and as tall as the tallest; zoom and
/// position are kept in canvas coordinates, which is what makes them the same for every image.
/// </summary>
public static class CompareGeometry
{
    /// <summary>
    /// The number of columns and rows for <paramref name="count"/> equal panes that shows the
    /// canvas largest: two landscape images go next to each other in a wide window and above
    /// each other in a tall one.
    /// </summary>
    public static (int Columns, int Rows) BestGrid(int count, double areaWidth, double areaHeight, double canvasWidth, double canvasHeight, double gap)
    {
        if (count <= 1) return (1, 1);
        if (canvasWidth <= 0 || canvasHeight <= 0) (canvasWidth, canvasHeight) = (3, 2); // nothing loaded yet: assume a photo
        int bestColumns = 1, bestEmpty = int.MaxValue;
        double bestScale = double.MinValue;
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            double paneWidth = (areaWidth - gap * (columns - 1)) / columns, paneHeight = (areaHeight - gap * (rows - 1)) / rows;
            double scale = Math.Min(paneWidth / canvasWidth, paneHeight / canvasHeight);
            int empty = columns * rows - count;
            bool same = Math.Abs(scale - bestScale) <= 1e-9 * Math.Abs(bestScale);
            if ((!same && scale > bestScale) || (same && empty < bestEmpty))
            {
                bestColumns = columns;
                bestScale = scale;
                bestEmpty = empty;
            }
        }
        return (bestColumns, (count + bestColumns - 1) / bestColumns);
    }

    /// <summary>
    /// The rectangle of pane <paramref name="index"/> in a grid over <paramref name="area"/>. All
    /// panes have exactly the same size, so that the same view puts the same canvas point at the
    /// same place in each; pixels left over are split between the two sides.
    /// </summary>
    public static PaneRect Pane(int index, int columns, int rows, PaneRect area, int gap)
    {
        int width = Math.Max(1, (area.Width - gap * (columns - 1)) / columns), height = Math.Max(1, (area.Height - gap * (rows - 1)) / rows);
        int left = area.X + Math.Max(0, area.Width - (width * columns + gap * (columns - 1))) / 2;
        int top = area.Y + Math.Max(0, area.Height - (height * rows + gap * (rows - 1))) / 2;
        return new PaneRect(left + index % columns * (width + gap), top + index / columns * (height + gap), width, height);
    }

    /// <summary>
    /// The transform from an upright image's pixel coordinates to canvas coordinates. The pixel-aligned
    /// placements move by whole pixels only, so pixels of two images stay exactly on top of each other.
    /// </summary>
    public static Affine2 Placement(SizeMatch match, double width, double height, double canvasWidth, double canvasHeight)
    {
        if (width <= 0 || height <= 0) return Affine2.Identity;
        switch (match)
        {
            case SizeMatch.Centre:
                return Affine2.ScaleTranslate(1, 1, Math.Floor((canvasWidth - width) / 2), Math.Floor((canvasHeight - height) / 2));
            case SizeMatch.ScaleToFit:
                double scale = Math.Min(canvasWidth / width, canvasHeight / height);
                return Affine2.ScaleTranslate(scale, scale, (canvasWidth - width * scale) / 2, (canvasHeight - height * scale) / 2);
            default:
                return Affine2.Identity;
        }
    }
}
