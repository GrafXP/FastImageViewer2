using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>
/// How the images of a compare set are put on screen. The modes that show several images at once
/// take the shown ones (see <see cref="CompareSelection"/>), beginning with the reference.
/// </summary>
public enum CompareMode
{
    /// <summary>One image at a time, switched in place.</summary>
    Single,

    /// <summary>Each image in its own pane, all in one row, or in one column if that shows them larger.</summary>
    SideBySide,

    /// <summary>Each image in its own pane, in as many rows and columns as show them largest.</summary>
    Grid,

    /// <summary>The images in one pane, each in a strip of its own between dividers.</summary>
    Split,

    /// <summary>The images in one pane, each over those before it with an opacity of its own.</summary>
    Blend,

    /// <summary>The images in one pane, each counting by an opacity of its own, in no order and against no reference (see <see cref="OverlayMix"/>).</summary>
    Overlay,

    /// <summary>The images in one pane, taking turns in the cells of a checkerboard.</summary>
    Checker,

    /// <summary>For each image, its difference from the reference, in the colours of a colour map: a pane each.</summary>
    Difference,

    /// <summary>For each image, its structural similarity to the reference, pixel by pixel, as a heat map: a pane each.</summary>
    Ssim,
}

public static class CompareModeExtensions
{
    /// <summary>The last of the modes, for going through them in turn.</summary>
    public const CompareMode Last = CompareMode.Ssim;

    /// <summary>True for the modes that put images on top of each other: several in one pane, or two to be compared.</summary>
    public static bool IsOverlay(this CompareMode mode) => mode >= CompareMode.Split;

    /// <summary>True for the modes that lay all the images shown into one pane.</summary>
    public static bool IsStack(this CompareMode mode) => mode is CompareMode.Split or CompareMode.Blend or CompareMode.Overlay or CompareMode.Checker;

    /// <summary>True for the modes with a pane for each image against the reference.</summary>
    public static bool IsPairwise(this CompareMode mode) => mode is CompareMode.Difference or CompareMode.Ssim;

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
    /// One row of <paramref name="count"/> panes, or one column of them, whichever shows the
    /// canvas larger: the side-by-side view.
    /// </summary>
    public static (int Columns, int Rows) BestRow(int count, double areaWidth, double areaHeight, double canvasWidth, double canvasHeight, double gap)
    {
        if (count <= 1) return (1, 1);
        if (canvasWidth <= 0 || canvasHeight <= 0) (canvasWidth, canvasHeight) = (3, 2);
        double across = Math.Min((areaWidth - gap * (count - 1)) / count / canvasWidth, areaHeight / canvasHeight);
        double down = Math.Min(areaWidth / canvasWidth, (areaHeight - gap * (count - 1)) / count / canvasHeight);
        return down > across ? (1, count) : (count, 1);
    }

    /// <summary>
    /// Which of <paramref name="layers"/> images a cell of the checkerboard shows. Two take turns
    /// as black and white squares do; more go round so that no two cells next to each other, also
    /// across a corner where there are four or more, show the same image. As combine.hlsl has it.
    /// </summary>
    public static int CheckerLayer(long cellX, long cellY, int layers)
    {
        if (layers <= 1) return 0;
        long step = Math.Max(1, layers / 2);
        return (int)(((cellX + cellY * step) % layers + layers) % layers);
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
