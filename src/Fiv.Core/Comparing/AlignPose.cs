using Fiv.Imaging;

namespace Fiv.Comparing;

/// <summary>What the automatic alignment may change.</summary>
public enum AlignModel
{
    /// <summary>The image is moved only.</summary>
    Shift,

    /// <summary>Moved and turned.</summary>
    ShiftRotation,

    /// <summary>Moved, turned and scaled.</summary>
    ShiftRotationScale,
}

/// <summary>
/// Where an image of a compare set lies relative to where its placement puts it: moved by
/// (<see cref="Dx"/>, <see cref="Dy"/>) canvas pixels, turned by <see cref="Rotation"/> degrees
/// (clockwise on screen) and scaled by <see cref="Scale"/>, both about the centre of the image.
/// The default value is "not moved at all".
/// </summary>
public readonly struct AlignPose : IEquatable<AlignPose>
{
    readonly double _scaleLessOne; // so that default(AlignPose) is the identity

    public double Dx { get; }
    public double Dy { get; }

    /// <summary>In degrees; positive turns the image clockwise on screen.</summary>
    public double Rotation { get; }

    public double Scale => 1 + _scaleLessOne;

    public AlignPose(double dx, double dy, double rotation = 0, double scale = 1)
    {
        Dx = dx;
        Dy = dy;
        Rotation = rotation;
        _scaleLessOne = scale - 1;
    }

    public static AlignPose Identity => default;

    public bool IsIdentity => Dx == 0 && Dy == 0 && Rotation == 0 && _scaleLessOne == 0;

    /// <summary>True if the pose moves by whole pixels and neither turns nor scales: pixels stay pixels.</summary>
    public bool IsWholePixels => Rotation == 0 && _scaleLessOne == 0 && Dx == Math.Floor(Dx) && Dy == Math.Floor(Dy);

    public AlignPose With(double? dx = null, double? dy = null, double? rotation = null, double? scale = null) =>
        new(dx ?? Dx, dy ?? Dy, rotation ?? Rotation, scale ?? Scale);

    /// <summary>
    /// The pose as a transform of canvas coordinates, turning and scaling about
    /// (<paramref name="centreX"/>, <paramref name="centreY"/>). A pose that only moves gives a
    /// transform that only moves, exactly: whole pixels stay whole pixels.
    /// </summary>
    public Affine2 About(double centreX, double centreY)
    {
        if (Rotation == 0 && _scaleLessOne == 0) return Affine2.ScaleTranslate(1, 1, Dx, Dy);
        double angle = Rotation * (Math.PI / 180), c = Scale * Math.Cos(angle), s = Scale * Math.Sin(angle);
        return new Affine2(c, s, -s, c, centreX - (c * centreX - s * centreY) + Dx, centreY - (s * centreX + c * centreY) + Dy);
    }

    /// <summary>
    /// The pose that a transform made of moving, turning and scaling alike in both directions
    /// amounts to, about the same centre.
    /// </summary>
    public static AlignPose FromSimilarity(in Affine2 transform, double centreX, double centreY)
    {
        double scale = Math.Sqrt(transform.A * transform.A + transform.B * transform.B);
        double rotation = Math.Atan2(transform.B, transform.A) * (180 / Math.PI);
        (double x, double y) = transform.Apply(centreX, centreY);
        return new AlignPose(x - centreX, y - centreY, rotation, scale);
    }

    /// <summary>A turn by <paramref name="degrees"/> and a scaling about a point, as a transform.</summary>
    public static Affine2 TurnAbout(double centreX, double centreY, double degrees, double scale = 1) =>
        new AlignPose(0, 0, degrees, scale).About(centreX, centreY);

    public bool Equals(AlignPose other) => Dx == other.Dx && Dy == other.Dy && Rotation == other.Rotation && _scaleLessOne == other._scaleLessOne;
    public override bool Equals(object? obj) => obj is AlignPose other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Dx, Dy, Rotation, _scaleLessOne);
    public static bool operator ==(AlignPose left, AlignPose right) => left.Equals(right);
    public static bool operator !=(AlignPose left, AlignPose right) => !left.Equals(right);

    public override string ToString() => FormattableString.Invariant($"x {Dx:0.###}, y {Dy:0.###}, {Rotation:0.####} deg, {Scale * 100:0.####} %");
}
