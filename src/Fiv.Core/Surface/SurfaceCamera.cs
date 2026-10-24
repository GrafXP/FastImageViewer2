using System.Numerics;

namespace Fiv.Surface;

/// <summary>
/// The camera of the 3D height map. It looks at a point of the ground from a direction and a
/// distance, and is turned about that point, moved over the ground and brought closer by the
/// mouse. The world is the upright image lying on the ground: x to the right, z down the image
/// (towards someone looking at it from its lower edge), y up, a unit one image pixel.
/// </summary>
public struct SurfaceCamera
{
    /// <summary>Vertical field of view: narrow, so that perspective distorts the surface little.</summary>
    public const float FieldOfView = 30 * MathF.PI / 180;

    /// <summary>The line of sight between just above the ground and straight down.</summary>
    public const float MinPitch = 5 * MathF.PI / 180, MaxPitch = MathF.PI / 2;

    const float RadiansPerPixel = 0.3f * MathF.PI / 180;

    /// <summary>The point of the ground looked at, and turned about.</summary>
    public Vector3 Target;

    /// <summary>Turn about the vertical, in radians: 0 looks from the image's lower edge towards its upper one, more from the right as it grows.</summary>
    public float Yaw;

    /// <summary>How far the line of sight is above the ground, in radians: π/2 looks straight down.</summary>
    public float Pitch;

    public float Distance;

    /// <summary>From the camera towards the target.</summary>
    public readonly Vector3 Forward
    {
        get
        {
            (float sinYaw, float cosYaw) = MathF.SinCos(Yaw);
            (float sinPitch, float cosPitch) = MathF.SinCos(Pitch);
            return new Vector3(-cosPitch * sinYaw, -sinPitch, -cosPitch * cosYaw);
        }
    }

    /// <summary>Right on the screen; always level.</summary>
    public readonly Vector3 Right
    {
        get
        {
            (float sinYaw, float cosYaw) = MathF.SinCos(Yaw);
            return new Vector3(cosYaw, 0, -sinYaw);
        }
    }

    /// <summary>Up on the screen. Straight down it is the image's upper edge, as in the 2D view.</summary>
    public readonly Vector3 Up => Vector3.Cross(Right, Forward);

    public readonly Vector3 Position => Target - Forward * Distance;

    /// <summary>
    /// Straight down at the ground point (<paramref name="x"/>, <paramref name="z"/>), from as far
    /// away as makes an image pixel <paramref name="zoom"/> screen pixels in a view
    /// <paramref name="viewHeight"/> pixels high: the flat image looks as it does in the 2D view.
    /// </summary>
    public static SurfaceCamera TopDown(float x, float z, double zoom, float viewHeight) => new()
    {
        Target = new Vector3(x, 0, z),
        Pitch = MaxPitch,
        Distance = (float)(viewHeight / (2 * Math.Tan(FieldOfView / 2) * zoom)),
    };

    /// <summary>
    /// World to clip space for a view <paramref name="aspect"/> times as wide as it is high.
    /// The depth is reversed, 1 at <paramref name="near"/> and 0 at infinity, which keeps it
    /// precise however far the surface reaches.
    /// </summary>
    public readonly Matrix4x4 WorldToClip(float aspect, float near)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(Position, Target, Up);
        float f = 1 / MathF.Tan(FieldOfView / 2);
        var projection = new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
        return view * projection;
    }

    /// <summary>Where a world point is in a view of this size, in its pixels; null if it is behind the camera.</summary>
    public readonly Vector2? Project(Vector3 world, float viewWidth, float viewHeight)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1), WorldToClip(viewWidth / viewHeight, Distance * 1e-3f));
        if (clip.W <= 0) return null;
        return new Vector2((clip.X / clip.W + 1) / 2 * viewWidth, (1 - clip.Y / clip.W) / 2 * viewHeight);
    }

    /// <summary>Turns about the target by a drag of the mouse: to the right turns the surface with it, down shows it more from above.</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * RadiansPerPixel;
        Pitch = Math.Clamp(Pitch + dy * RadiansPerPixel, MinPitch, MaxPitch);
    }

    /// <summary>Moves the target over the ground so that the ground follows a drag of the mouse, in a view <paramref name="viewHeight"/> pixels high.</summary>
    public void Pan(float dx, float dy, float viewHeight)
    {
        float unitsPerPixel = 2 * MathF.Tan(FieldOfView / 2) * Distance / viewHeight;
        (float sinYaw, float cosYaw) = MathF.SinCos(Yaw);
        var away = new Vector3(-sinYaw, 0, -cosYaw); // along the ground, away from the camera
        // A pixel up the screen is further along the ground the flatter the view is.
        Target += away * (dy * unitsPerPixel / MathF.Max(MathF.Sin(Pitch), 0.2f)) - Right * (dx * unitsPerPixel);
    }

    /// <summary>
    /// Comes closer by <paramref name="factor"/> (below 1) or goes further away (above 1),
    /// keeping where it is the point of the ground under a point of the view, given from -1 to 1
    /// across it (<paramref name="ndcY"/> up). Where that point is no ground, or too far off to
    /// zoom at, it comes closer to the target.
    /// </summary>
    public void ZoomAt(float factor, float ndcX, float ndcY, float aspect)
    {
        float t = MathF.Tan(FieldOfView / 2);
        Vector3 ray = Forward + Right * (ndcX * t * aspect) + Up * (ndcY * t);
        Vector3 from = Position;
        if (ray.Y < 0)
        {
            Vector3 ground = from + ray * (-from.Y / ray.Y);
            if (Vector3.Distance(ground, Target) < 10 * Distance) Target = ground + (Target - ground) * factor;
        }
        Distance *= factor;
    }

    /// <summary>Between two cameras at <paramref name="t"/> from 0 to 1: the target and the angles along a straight line, the distance by equal ratios.</summary>
    public static SurfaceCamera Lerp(in SurfaceCamera a, in SurfaceCamera b, float t) => new()
    {
        Target = Vector3.Lerp(a.Target, b.Target, t),
        Yaw = a.Yaw + (b.Yaw - a.Yaw) * t,
        Pitch = a.Pitch + (b.Pitch - a.Pitch) * t,
        Distance = a.Distance * MathF.Pow(b.Distance / a.Distance, t),
    };

    /// <summary>The same view with its turn between -π and π, the shortest way back to 0.</summary>
    public readonly SurfaceCamera WithTurnWrapped() => this with { Yaw = MathF.IEEERemainder(Yaw, 2 * MathF.PI) };
}
