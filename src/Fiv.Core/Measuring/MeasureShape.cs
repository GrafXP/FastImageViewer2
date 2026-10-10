using Fiv.Comparing;

namespace Fiv.Measuring;

public enum MeasureKind
{
    Line,
    Rectangle,
    Circle,
}

/// <summary>Where a measurement can be taken hold of with the mouse.</summary>
public enum MeasureHandle
{
    None,

    /// <summary>The two ends of a line.</summary>
    Start,
    End,

    // The corners and the middles of the sides of a rectangle.
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,

    /// <summary>Anywhere on a circle: changes its radius.</summary>
    Radius,

    /// <summary>The rest of it (a line between its ends, a rectangle's outline, a circle's centre): moves the whole.</summary>
    Body,
}

/// <summary>
/// A line, a rectangle or a circle on the pixel grid on show: the upright image, or the canvas
/// of a comparison. Pixel k covers k to k + 1 and has its centre at k + 0.5.
/// A line runs from (X0, Y0) to (X1, Y1). A rectangle has its top left corner at (X0, Y0) and
/// its bottom right one at (X1, Y1), both on whole pixels, so that it holds whole pixels. A
/// circle has its centre at (X0, Y0) and its radius in X1 - X0.
/// </summary>
public readonly record struct MeasureShape(MeasureKind Kind, double X0, double Y0, double X1, double Y1)
{
    public static MeasureShape Line(double x0, double y0, double x1, double y1) => new(MeasureKind.Line, x0, y0, x1, y1);

    /// <summary>The rectangle between two points, in any order, its sides moved onto the nearest pixel edges.</summary>
    public static MeasureShape Rectangle(double x0, double y0, double x1, double y1)
    {
        double left = Edge(Math.Min(x0, x1)), right = Edge(Math.Max(x0, x1));
        double top = Edge(Math.Min(y0, y1)), bottom = Edge(Math.Max(y0, y1));
        return new(MeasureKind.Rectangle, left, top, right, bottom);
    }

    /// <summary>The pixel edge nearest to a coordinate. Half-way between two it is the right or lower one, always: Math.Round would go to the even one.</summary>
    static double Edge(double value) => Math.Floor(value + 0.5) + 0.0;

    public static MeasureShape Circle(double centreX, double centreY, double radius) =>
        new(MeasureKind.Circle, centreX, centreY, centreX + Math.Max(0, radius), centreY);

    // ---------------------------------------------------------------- numbers

    public double Dx => X1 - X0;
    public double Dy => Y1 - Y0;

    /// <summary>A line's length, in pixels.</summary>
    public double Length => Math.Sqrt(Dx * Dx + Dy * Dy);

    /// <summary>
    /// The direction of a line in degrees, as it looks on screen: 0 pointing right, 90 pointing
    /// up, -90 pointing down, 180 pointing left.
    /// </summary>
    public double Angle => Dx == 0 && Dy == 0 ? 0 : Math.Atan2(0.0 - Dy, Dx) * (180 / Math.PI); // 0.0 - Dy: never a negative zero, which would make "left" -180

    public double Width => Kind == MeasureKind.Circle ? 2 * Radius : Math.Abs(Dx);
    public double Height => Kind == MeasureKind.Circle ? 2 * Radius : Math.Abs(Dy);
    public double Radius => Kind == MeasureKind.Circle ? X1 - X0 : 0;
    public double Diameter => 2 * Radius;

    /// <summary>The area inside, in square pixels; none for a line.</summary>
    public double Area => Kind switch
    {
        MeasureKind.Rectangle => Width * Height,
        MeasureKind.Circle => Math.PI * Radius * Radius,
        _ => 0,
    };

    /// <summary>The length of the outline; of a line, its length.</summary>
    public double Perimeter => Kind switch
    {
        MeasureKind.Rectangle => 2 * (Width + Height),
        MeasureKind.Circle => 2 * Math.PI * Radius,
        _ => Length,
    };

    /// <summary>How large it is where it is smallest: what tells a measurement from a click.</summary>
    public double Extent => Kind switch
    {
        MeasureKind.Rectangle => Math.Min(Width, Height),
        MeasureKind.Circle => Radius,
        _ => Length,
    };

    public bool HasArea => Kind != MeasureKind.Line;

    /// <summary>The whole pixels that can hold a pixel of the shape: the rectangle itself, or the square around the circle.</summary>
    public CanvasRect PixelBounds
    {
        get
        {
            if (Kind == MeasureKind.Circle)
            {
                double r = Radius;
                return CanvasRect.FromEdges(Whole(Math.Floor(X0 - r)), Whole(Math.Floor(Y0 - r)), Whole(Math.Ceiling(X0 + r)), Whole(Math.Ceiling(Y0 + r)));
            }
            return CanvasRect.FromEdges(Whole(Math.Floor(Math.Min(X0, X1))), Whole(Math.Floor(Math.Min(Y0, Y1))),
                Whole(Math.Ceiling(Math.Max(X0, X1))), Whole(Math.Ceiling(Math.Max(Y0, Y1))));
        }
    }

    static int Whole(double value) => (int)Math.Clamp(value, -1e9, 1e9);

    /// <summary>True if the centre of pixel (<paramref name="x"/>, <paramref name="y"/>) is inside a rectangle or a circle.</summary>
    public bool HoldsPixel(int x, int y)
    {
        double px = x + 0.5, py = y + 0.5;
        if (Kind == MeasureKind.Circle) return (px - X0) * (px - X0) + (py - Y0) * (py - Y0) <= Radius * Radius;
        return Kind == MeasureKind.Rectangle && px > X0 && px < X1 && py > Y0 && py < Y1;
    }

    /// <summary>
    /// The pixels of row <paramref name="y"/> whose centres a rectangle or a circle holds: from
    /// <paramref name="left"/> up to, not including, <paramref name="right"/>. None if the two are equal.
    /// </summary>
    public void RowSpan(int y, out int left, out int right)
    {
        left = right = 0;
        if (Kind == MeasureKind.Rectangle)
        {
            if (y + 0.5 > Y0 && y + 0.5 < Y1) (left, right) = (Whole(X0), Whole(X1));
            return;
        }
        if (Kind != MeasureKind.Circle) return;
        double dy = y + 0.5 - Y0, r = Radius, rest = r * r - dy * dy;
        if (rest < 0) return;
        double half = Math.Sqrt(rest);
        left = Whole(Math.Ceiling(X0 - half - 0.5));
        right = Whole(Math.Floor(X0 + half - 0.5)) + 1;
        // The square root is rounded: the two ends are settled by the same test that says whether a pixel is inside.
        while (HoldsPixel(left - 1, y)) left--;
        while (left < right && !HoldsPixel(left, y)) left++;
        while (HoldsPixel(right, y)) right++;
        while (right > left && !HoldsPixel(right - 1, y)) right--;
    }

    // ---------------------------------------------------------------- taking hold of it

    /// <summary>
    /// What of the shape is at a point, within <paramref name="tolerance"/> (in pixels of the
    /// grid): one of its handles, the rest of it, or nothing. The inside of a rectangle or a
    /// circle is not part of it: a drag there is still the view's.
    /// </summary>
    public MeasureHandle HitTest(double x, double y, double tolerance)
    {
        switch (Kind)
        {
            case MeasureKind.Line:
            {
                double toStart = Distance(x, y, X0, Y0), toEnd = Distance(x, y, X1, Y1);
                if (toEnd <= tolerance && toEnd <= toStart) return MeasureHandle.End;
                if (toStart <= tolerance) return MeasureHandle.Start;
                return DistanceToSegment(x, y, X0, Y0, X1, Y1) <= tolerance ? MeasureHandle.Body : MeasureHandle.None;
            }
            case MeasureKind.Rectangle:
            {
                double middleX = (X0 + X1) / 2, middleY = (Y0 + Y1) / 2;
                MeasureHandle nearest = MeasureHandle.None;
                double best = tolerance;
                Try(X0, Y0, MeasureHandle.TopLeft);
                Try(X1, Y0, MeasureHandle.TopRight);
                Try(X1, Y1, MeasureHandle.BottomRight);
                Try(X0, Y1, MeasureHandle.BottomLeft);
                if (nearest != MeasureHandle.None) return nearest; // a corner before the middle of a side, where a small rectangle has both in reach
                Try(middleX, Y0, MeasureHandle.Top);
                Try(X1, middleY, MeasureHandle.Right);
                Try(middleX, Y1, MeasureHandle.Bottom);
                Try(X0, middleY, MeasureHandle.Left);
                if (nearest != MeasureHandle.None) return nearest;
                double outline = Math.Min(
                    Math.Min(DistanceToSegment(x, y, X0, Y0, X1, Y0), DistanceToSegment(x, y, X0, Y1, X1, Y1)),
                    Math.Min(DistanceToSegment(x, y, X0, Y0, X0, Y1), DistanceToSegment(x, y, X1, Y0, X1, Y1)));
                return outline <= tolerance ? MeasureHandle.Body : MeasureHandle.None;

                void Try(double hx, double hy, MeasureHandle handle)
                {
                    double d = Distance(x, y, hx, hy);
                    if (d > best) return;
                    best = d;
                    nearest = handle;
                }
            }
            default:
            {
                double toCentre = Distance(x, y, X0, Y0);
                // The outline first: of a circle smaller than the tolerance the centre would otherwise be all there is.
                if (Math.Abs(toCentre - Radius) <= tolerance && toCentre > Radius / 2) return MeasureHandle.Radius;
                return toCentre <= tolerance ? MeasureHandle.Body : MeasureHandle.None;
            }
        }
    }

    static double Distance(double x, double y, double toX, double toY) => Math.Sqrt((x - toX) * (x - toX) + (y - toY) * (y - toY));

    /// <summary>How far a point is from the nearest point of a line between two others.</summary>
    public static double DistanceToSegment(double x, double y, double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0, lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared > 0 ? Math.Clamp(((x - x0) * dx + (y - y0) * dy) / lengthSquared, 0, 1) : 0;
        return Distance(x, y, x0 + t * dx, y0 + t * dy);
    }

    // ---------------------------------------------------------------- drawing and changing it

    /// <summary>
    /// The shape a drag from one point to another draws, kept within a grid of
    /// <paramref name="width"/> by <paramref name="height"/> pixels: a line between the two, the
    /// rectangle with the two as opposite corners, or the circle about the first through the
    /// second. With <paramref name="constrain"/> a line is horizontal, vertical or diagonal, and
    /// a rectangle is a square.
    /// </summary>
    public static MeasureShape Drawn(MeasureKind kind, double fromX, double fromY, double toX, double toY, bool constrain, double width, double height)
    {
        (fromX, fromY) = (Math.Clamp(fromX, 0, width), Math.Clamp(fromY, 0, height));
        switch (kind)
        {
            case MeasureKind.Line:
                if (constrain) (toX, toY) = AlongAnAxis(fromX, fromY, toX, toY, width, height);
                return Line(fromX, fromY, Math.Clamp(toX, 0, width), Math.Clamp(toY, 0, height));
            case MeasureKind.Rectangle:
                // Onto the pixel edges first: a square of whole pixels is then as wide as it is high, exactly.
                (fromX, fromY, toX, toY) = (Edge(fromX), Edge(fromY), Edge(toX), Edge(toY));
                if (constrain) (toX, toY) = Square(fromX, fromY, toX, toY, Math.Floor(width), Math.Floor(height));
                return Rectangle(fromX, fromY, Math.Clamp(toX, 0, width), Math.Clamp(toY, 0, height));
            default:
                return Circle(fromX, fromY, Distance(toX, toY, fromX, fromY));
        }
    }

    /// <summary>
    /// The shape after one of its handles, taken hold of at (<paramref name="grabX"/>,
    /// <paramref name="grabY"/>), has been dragged to (<paramref name="x"/>, <paramref name="y"/>):
    /// what is held moves as far as the mouse has. Kept within the grid, and with
    /// <paramref name="constrain"/> a line's end stays horizontal, vertical or diagonal to its
    /// other end and a rectangle dragged by a corner is a square.
    /// </summary>
    public MeasureShape Dragged(MeasureHandle handle, double grabX, double grabY, double x, double y, bool constrain, double width, double height)
    {
        double dx = x - grabX, dy = y - grabY;
        if (handle == MeasureHandle.Body)
        {
            // The whole of it, as far as the grid lets all of it go; a rectangle by whole pixels.
            if (Kind == MeasureKind.Rectangle) (dx, dy) = (Math.Floor(dx + 0.5), Math.Floor(dy + 0.5));
            double left = Kind == MeasureKind.Circle ? X0 : Math.Min(X0, X1), right = Kind == MeasureKind.Circle ? X0 : Math.Max(X0, X1);
            double top = Kind == MeasureKind.Circle ? Y0 : Math.Min(Y0, Y1), bottom = Kind == MeasureKind.Circle ? Y0 : Math.Max(Y0, Y1);
            dx = Keep(dx, left, right, width);
            dy = Keep(dy, top, bottom, height);
            return this with { X0 = X0 + dx, Y0 = Y0 + dy, X1 = X1 + dx, Y1 = Y1 + dy };
        }
        switch (Kind)
        {
            case MeasureKind.Line when handle is MeasureHandle.Start or MeasureHandle.End:
            {
                bool start = handle == MeasureHandle.Start;
                double fixedX = start ? X1 : X0, fixedY = start ? Y1 : Y0;
                double movedX = (start ? X0 : X1) + dx, movedY = (start ? Y0 : Y1) + dy;
                if (constrain) (movedX, movedY) = AlongAnAxis(fixedX, fixedY, movedX, movedY, width, height);
                (movedX, movedY) = (Math.Clamp(movedX, 0, width), Math.Clamp(movedY, 0, height));
                return start ? Line(movedX, movedY, fixedX, fixedY) : Line(fixedX, fixedY, movedX, movedY);
            }
            case MeasureKind.Rectangle when handle is >= MeasureHandle.TopLeft and <= MeasureHandle.Left:
            {
                // The sides the handle holds move; the others stay. Dragged past them, the rectangle turns over.
                double left = X0, top = Y0, right = X1, bottom = Y1;
                bool movesLeft = handle is MeasureHandle.TopLeft or MeasureHandle.Left or MeasureHandle.BottomLeft;
                bool movesRight = handle is MeasureHandle.TopRight or MeasureHandle.Right or MeasureHandle.BottomRight;
                bool movesTop = handle is MeasureHandle.TopLeft or MeasureHandle.Top or MeasureHandle.TopRight;
                bool movesBottom = handle is MeasureHandle.BottomLeft or MeasureHandle.Bottom or MeasureHandle.BottomRight;
                if ((movesLeft || movesRight) && (movesTop || movesBottom))
                {
                    double fixedX = movesLeft ? right : left, fixedY = movesTop ? bottom : top;
                    double movedX = Edge((movesLeft ? left : right) + dx), movedY = Edge((movesTop ? top : bottom) + dy);
                    if (constrain) (movedX, movedY) = Square(fixedX, fixedY, movedX, movedY, Math.Floor(width), Math.Floor(height));
                    return Rectangle(fixedX, fixedY, Math.Clamp(movedX, 0, width), Math.Clamp(movedY, 0, height));
                }
                if (movesLeft) left = Math.Clamp(left + dx, 0, width);
                if (movesRight) right = Math.Clamp(right + dx, 0, width);
                if (movesTop) top = Math.Clamp(top + dy, 0, height);
                if (movesBottom) bottom = Math.Clamp(bottom + dy, 0, height);
                return Rectangle(left, top, right, bottom);
            }
            case MeasureKind.Circle when handle == MeasureHandle.Radius:
                // The circle goes on passing as far from the mouse as it did when it was taken hold of.
                return Circle(X0, Y0, Radius + Distance(x, y, X0, Y0) - Distance(grabX, grabY, X0, Y0));
        }
        return this;

        // How far something that reaches from low to high may move within 0 .. size, of the move asked for.
        static double Keep(double move, double low, double high, double size)
        {
            if (high - low > size) return 0; // larger than the grid: it stays where it is
            return Math.Clamp(move, -low, size - high);
        }
    }

    /// <summary>The shape with all of its points within the grid: after the image on show has become another, smaller one.</summary>
    public MeasureShape Within(double width, double height) => Kind switch
    {
        MeasureKind.Line => Line(Math.Clamp(X0, 0, width), Math.Clamp(Y0, 0, height), Math.Clamp(X1, 0, width), Math.Clamp(Y1, 0, height)),
        MeasureKind.Rectangle => Rectangle(Math.Clamp(X0, 0, width), Math.Clamp(Y0, 0, height), Math.Clamp(X1, 0, width), Math.Clamp(Y1, 0, height)),
        _ => Circle(Math.Clamp(X0, 0, width), Math.Clamp(Y0, 0, height), Radius),
    };

    /// <summary>
    /// The point nearest to (<paramref name="x"/>, <paramref name="y"/>) that lies level with,
    /// straight above or below, or diagonally from the first; a diagonal ends where the grid does.
    /// </summary>
    static (double X, double Y) AlongAnAxis(double fromX, double fromY, double x, double y, double width, double height)
    {
        double dx = x - fromX, dy = y - fromY, ax = Math.Abs(dx), ay = Math.Abs(dy);
        const double tan22 = 0.41421356237309503; // half-way between an axis and the diagonal
        if (ay <= ax * tan22) return (x, fromY);
        if (ax <= ay * tan22) return (fromX, y);
        double side = Math.Min((ax + ay) / 2, Room(fromX, fromY, dx, dy, width, height));
        return (fromX + Math.Sign(dx) * side, fromY + Math.Sign(dy) * side);
    }

    /// <summary>
    /// The corner of the square whose opposite corner is the first point, on the side the second
    /// point is: as large as the longer of the two distances, and as the grid has room for.
    /// </summary>
    static (double X, double Y) Square(double fromX, double fromY, double x, double y, double width, double height)
    {
        double dx = x - fromX, dy = y - fromY;
        double side = Math.Min(Math.Max(Math.Abs(dx), Math.Abs(dy)), Room(fromX, fromY, dx, dy, width, height));
        return (fromX + (dx < 0 ? -side : side), fromY + (dy < 0 ? -side : side));
    }

    /// <summary>How far one can go from a point both ways at once, in the directions given, before leaving the grid.</summary>
    static double Room(double fromX, double fromY, double dx, double dy, double width, double height) =>
        Math.Max(0, Math.Min(dx < 0 ? fromX : width - fromX, dy < 0 ? fromY : height - fromY));
}
