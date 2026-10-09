using System.Runtime.InteropServices;

namespace Fiv.Bench;

/// <summary>
/// Generates photo-like test images: equal energy per octave down to the scale where lens blur
/// would take over, smooth and busy regions, hard edges, sensor noise. The point is that the
/// encoded files have about the size, and therefore the decode cost, of camera output.
/// A solid magenta square marks the centre so that the screen probe in the time-to-first-pixel
/// harness can recognise the image.
/// </summary>
public static unsafe class SyntheticImage
{
    public const byte MarkerR = 255, MarkerG = 0, MarkerB = 255;

    static readonly int[] Cells = [1024, 512, 256, 128, 64, 32, 16, 8, 4, 2];
    static readonly float[] LumaAmplitude = [1f, 1f, 1f, 1f, 1f, 1f, 1f, 0.9f, 0.7f, 0.45f];
    static readonly float[] ChromaAmplitude = [1f, 1f, 1f, 0.8f, 0.6f, 0.4f, 0.2f, 0.1f, 0f, 0f];
    const int FirstDetailOctave = 5; // octaves from here on are damped in the smooth regions

    readonly record struct Shape(bool Disc, float X, float Y, float W, float H, float R, float G, float B, float Alpha);

    sealed class Lattice
    {
        public readonly int Cell, Width, Height;
        public readonly float[] Values;

        public Lattice(int cell, int imageWidth, int imageHeight, Random random)
        {
            Cell = cell;
            Width = imageWidth / cell + 2;
            Height = imageHeight / cell + 2;
            Values = new float[Width * Height];
            for (int i = 0; i < Values.Length; i++) Values[i] = random.NextSingle() * 2 - 1;
        }

        /// <summary>Adds amplitude * noise(x, y) to every element of <paramref name="row"/>.</summary>
        public void AddRow(Span<float> row, int y, float amplitude, ReadOnlySpan<float> perPixelGain, Span<float> scratch)
        {
            int ly = y / Cell;
            float fy = Smooth((y - ly * Cell) / (float)Cell);
            ReadOnlySpan<float> a = Values.AsSpan(ly * Width, Width), b = Values.AsSpan((ly + 1) * Width, Width);
            for (int i = 0; i < Width; i++) scratch[i] = (a[i] + (b[i] - a[i]) * fy) * amplitude;

            float inverseCell = 1f / Cell;
            for (int lx = 0, x = 0; x < row.Length; lx++)
            {
                float v0 = scratch[lx], dv = scratch[lx + 1] - v0;
                int end = Math.Min(row.Length, x + Cell);
                if (perPixelGain.IsEmpty)
                {
                    for (int i = 0; x < end; x++, i++) row[x] += v0 + dv * Smooth(i * inverseCell);
                }
                else
                {
                    for (int i = 0; x < end; x++, i++) row[x] += (v0 + dv * Smooth(i * inverseCell)) * perPixelGain[x];
                }
            }
        }

        static float Smooth(float t) => t * t * (3 - 2 * t);
    }

    /// <summary>Fills a BGRA8 buffer. 16-bit luma, if wanted, is written to <paramref name="gray16"/>.</summary>
    public static void Fill(byte* bgra, int stride, int width, int height, int seed, ushort* gray16 = null, bool marker = true)
    {
        var random = new Random(seed);
        var luma = new Lattice[Cells.Length];
        var cb = new Lattice[Cells.Length];
        var cr = new Lattice[Cells.Length];
        for (int o = 0; o < Cells.Length; o++)
        {
            luma[o] = new Lattice(Cells[o], width, height, random);
            cb[o] = new Lattice(Cells[o], width, height, random);
            cr[o] = new Lattice(Cells[o], width, height, random);
        }
        var detail = new Lattice(768, width, height, random);

        float unit = Math.Max(width, height);
        var shapes = new Shape[48];
        for (int i = 0; i < shapes.Length; i++)
        {
            shapes[i] = new Shape(random.Next(2) == 0,
                random.NextSingle() * width, random.NextSingle() * height,
                (0.01f + random.NextSingle() * 0.12f) * unit, (0.01f + random.NextSingle() * 0.12f) * unit,
                random.Next(256), random.Next(256), random.Next(256), 0.25f + random.NextSingle() * 0.6f);
        }

        int markerSize = Math.Max(16, Math.Min(width, height) / 12);
        int markerLeft = (width - markerSize) / 2, markerTop = (height - markerSize) / 2;

        Parallel.For(0, height, () => new float[4][] { new float[width], new float[width], new float[width], new float[width] },
            (y, _, buffers) =>
            {
                Span<float> l = buffers[0], u = buffers[1], v = buffers[2], gain = buffers[3];
                Span<float> scratch = stackalloc float[width / 2 + 3];
                l.Clear();
                u.Clear();
                v.Clear();
                gain.Clear();
                detail.AddRow(gain, y, 1f, default, scratch);
                for (int x = 0; x < width; x++)
                {
                    float g = Math.Clamp(gain[x] * 0.9f + 0.5f, 0f, 1f);
                    gain[x] = 0.12f + 0.88f * g * g;
                }
                for (int o = 0; o < Cells.Length; o++)
                {
                    ReadOnlySpan<float> octaveGain = o >= FirstDetailOctave ? gain : default;
                    luma[o].AddRow(l, y, LumaAmplitude[o] * 26f, octaveGain, scratch);
                    if (ChromaAmplitude[o] == 0f) continue;
                    cb[o].AddRow(u, y, ChromaAmplitude[o] * 12f, octaveGain, scratch);
                    cr[o].AddRow(v, y, ChromaAmplitude[o] * 12f, octaveGain, scratch);
                }

                byte* row = bgra + (long)y * stride;
                ushort* row16 = gray16 == null ? null : gray16 + (long)y * width;
                uint noise = (uint)(seed * 7919 + y * 104729) | 1;
                for (int x = 0; x < width; x++)
                {
                    float yy = 128f + l[x];
                    float r = yy + 1.402f * v[x], g = yy - 0.344f * u[x] - 0.714f * v[x], b = yy + 1.772f * u[x];
                    foreach (ref readonly Shape s in shapes.AsSpan())
                    {
                        float dx = (x - s.X) / s.W, dy = (y - s.Y) / s.H;
                        bool inside = s.Disc ? dx * dx + dy * dy < 1f : MathF.Abs(dx) < 1f && MathF.Abs(dy) < 1f;
                        if (!inside) continue;
                        r += (s.R - r) * s.Alpha;
                        g += (s.G - g) * s.Alpha;
                        b += (s.B - b) * s.Alpha;
                    }
                    noise ^= noise << 13;
                    noise ^= noise >> 17;
                    noise ^= noise << 5;
                    r += ((noise & 0xFF) - 127.5f) * (1f / 28f);
                    g += (((noise >> 8) & 0xFF) - 127.5f) * (1f / 28f);
                    b += (((noise >> 16) & 0xFF) - 127.5f) * (1f / 28f);
                    row[x * 4 + 0] = (byte)Math.Clamp(b + 0.5f, 0f, 255f);
                    row[x * 4 + 1] = (byte)Math.Clamp(g + 0.5f, 0f, 255f);
                    row[x * 4 + 2] = (byte)Math.Clamp(r + 0.5f, 0f, 255f);
                    row[x * 4 + 3] = 255;
                    if (row16 != null)
                    {
                        float gray = 0.299f * r + 0.587f * g + 0.114f * b;
                        row16[x] = (ushort)Math.Clamp(gray * 257f + ((noise >> 24) - 127.5f), 0f, 65535f);
                    }
                }
                if (marker && y >= markerTop && y < markerTop + markerSize)
                {
                    for (int x = markerLeft; x < markerLeft + markerSize; x++)
                    {
                        row[x * 4 + 0] = MarkerB;
                        row[x * 4 + 1] = MarkerG;
                        row[x * 4 + 2] = MarkerR;
                        if (row16 != null) row16[x] = 65535;
                    }
                }
                return buffers;
            },
            _ => { });
    }

    /// <summary>A generated image in native memory. Dispose to free it.</summary>
    public sealed class Buffer(int width, int height, bool withGray16) : IDisposable
    {
        public readonly int Width = width, Height = height, Stride = width * 4;
        public byte* Bgra = (byte*)NativeMemory.Alloc((nuint)width * 4 * (nuint)height);
        public ushort* Gray16 = withGray16 ? (ushort*)NativeMemory.Alloc((nuint)width * 2 * (nuint)height) : null;

        public void Dispose()
        {
            NativeMemory.Free(Bgra);
            NativeMemory.Free(Gray16);
            Bgra = null;
            Gray16 = null;
        }
    }

    public static Buffer Create(int width, int height, int seed, bool withGray16 = false, bool marker = true)
    {
        var buffer = new Buffer(width, height, withGray16);
        Fill(buffer.Bgra, buffer.Stride, width, height, seed, buffer.Gray16, marker);
        return buffer;
    }
}
