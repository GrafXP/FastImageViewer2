// The final pass of the compare modes that combine images in one pane. Each image was drawn,
// under its own transform, into a pane-sized intermediate target that holds its stored sample
// values (alpha below zero: the image has no pixel there). This pass decides per screen pixel
// what they become. A compare mode is a few lines in ps_main.
//
// Split, blend and checkerboard take any number of images, one pass each, in A: a pass draws
// its image where its strip or its cells are and discards the rest, or, for the blend, lies it
// over what the passes before it drew, by the opacity the blend state is given. The difference
// and the similarity take two images at once, A and B.
//
// The difference modes follow Fiv.Comparing.DifferenceMath, which is also what the files saved
// from the viewer are computed with: change one, change the other. The cells of the checkerboard
// follow Fiv.Comparing.CompareGeometry.CheckerLayer.

#include "display.hlsli"

Texture2D<float4> imageA : register(t0);
Texture2D<float4> imageB : register(t1);
Texture2D<float4> colours : register(t2);  // the colour map: 256 texels in a row

cbuffer Combine : register(b0)
{
    float4 window;    // see display.hlsli
    float4 checker;
    float4 backdrop;  // rgb: what is shown where an image has no pixels
    float4 pane;      // xy: the pane's top-left corner on the render target
    float4 amount;    // modes 0, 1: x, y: where the strip of this pass's image begins and ends, in pane pixels
                      // mode 2: x: the first image's opacity over the backdrop (those after it are the blend state's)
                      // mode 3: x: the cell size; yz: where the cells are centred
                      // mode 4: x: gain / value range; y: gain; z: the smallest value of A a relative difference is taken of; w: the mask's threshold
                      // mode 5: x: gain; y: 1 if the similarity is shown only where B has pixels
    uint mode;        // 0: strips side by side; 1: strips above each other; 2: each image over those before it; 3: checkerboard;
                      // 4: the difference B - A; 5: A is a similarity, shown as 1 - A
    uint channel;
    uint kind;        // of difference: 0 absolute, 1 signed, 2 squared, 3 relative
    uint mask;        // 1: instead of the difference, A dimmed and the pixels that differ by more than the threshold marked
    uint layer;       // modes 0-3: which of the images this pass draws, from 0
    uint layers;      // and how many there are
};

static const float3 LumaWeights = float3(0.2126, 0.7152, 0.0722);
static const float3 MaskColor = float3(1, 0.05, 0.85);
static const float MaskDim = 0.55;

// One triangle that covers the viewport, which the caller has set to the pane.
float4 vs_main(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float3 Shown(float4 stored, float2 screenPosition)
{
    return stored.a < 0 ? backdrop.rgb : Shade(stored, screenPosition, window, checker, channel);
}

// The colour at a position 0..1 of the map: the two nearest entries blended.
float3 MapColor(float t)
{
    float p = saturate(t) * 255;
    int i = (int)p;
    return lerp(colours.Load(int3(i, 0, 0)).rgb, colours.Load(int3(min(i + 1, 255), 0, 0)).rgb, p - i);
}

// The colour of the difference, and in t where it is on the colour map (also with the mask on).
float3 Difference(float4 a, float4 b, float2 screenPosition, out float t)
{
    float3 d3 = b.rgb - a.rgb;
    float d, reference;
    if (channel == 1) { d = d3.r; reference = a.r; }
    else if (channel == 2) { d = d3.g; reference = a.g; }
    else if (channel == 3) { d = d3.b; reference = a.b; }
    else if (channel == 4) { d = b.a - a.a; reference = a.a; }
    else if (channel == 5) { d = dot(d3, LumaWeights); reference = dot(a.rgb, LumaWeights); }
    else
    {
        // Colour: the channel that differs most, so that no change in one channel goes unseen.
        float3 size3 = abs(d3);
        if (size3.r >= size3.g && size3.r >= size3.b) { d = d3.r; reference = a.r; }
        else if (size3.g >= size3.b) { d = d3.g; reference = a.g; }
        else { d = d3.b; reference = a.b; }
    }

    float size = abs(d);
    if (kind == 1) t = 0.5 + 0.5 * d * amount.x;
    else if (kind == 2) t = size * amount.x * (size * amount.x);
    else if (kind == 3) t = size / max(abs(reference), amount.z) * amount.y;
    else t = size * amount.x;
    float3 color = MapColor(t);
    if (mask)
    {
        float grey = dot(Shade(a, screenPosition, window, checker, channel), LumaWeights) * MaskDim;
        color = size > amount.w ? MaskColor : grey.xxx;
    }
    return color;
}

struct Output
{
    float4 color : SV_Target0;
    // How high the 3D height map stands here, 0..1: the more of what the view shows, the higher.
    // Its target is bound only when the view is drawn for the 3D height map (SurfaceCanvas).
    float height : SV_Target1;
};

Output ps_main(float4 position : SV_Position)
{
    float2 p = position.xy - pane.xy;
    int3 texel = int3(p, 0);
    float4 storedA = imageA.Load(texel);

    float3 color;
    float height = 0;
    if (mode == 4)
    {
        float4 storedB = imageB.Load(texel);
        if (storedA.a < 0 || storedB.a < 0) color = backdrop.rgb;
        else color = Difference(storedA, storedB, position.xy, height);
    }
    else if (mode == 5)
    {
        if (storedA.a < 0 || (amount.y > 0.5 && imageB.Load(texel).a < 0)) color = backdrop.rgb;
        else color = MapColor(height = (1 - storedA.r) * amount.x);
    }
    else
    {
        // One image of several: where this pass's image is not seen, the pixel is left to the others.
        if (mode == 0 || mode == 1)
        {
            float at = mode == 0 ? p.x : p.y;
            if ((layer > 0 && at < amount.x) || (layer + 1 < layers && at >= amount.y)) discard;
        }
        else if (mode == 2)
        {
            // Over the images before it: where it has no pixels, they are seen as they are.
            if (layer > 0 && storedA.a < 0) discard;
        }
        else
        {
            int n = (int)layers, step = max(1, n / 2);
            int2 cell = (int2)floor((p - amount.yz) / amount.x);
            if (((cell.x + cell.y * step) % n + n) % n != (int)layer) discard;
        }
        // Brightness is height, as for an image on its own; where an image has no pixels, nothing stands.
        color = Shown(storedA, position.xy);
        height = storedA.a < 0 ? 0 : dot(color, LumaWeights);
        if (mode == 2 && layer == 0)
        {
            // The first image of a blend over the backdrop, by its opacity: the passes after it blend over this.
            color = lerp(backdrop.rgb, color, amount.x);
            height *= amount.x;
        }
    }
    Output o;
    o.color = float4(color, 1);
    o.height = saturate(height);
    return o;
}
