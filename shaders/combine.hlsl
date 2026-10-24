// The final pass of the compare modes that show two images in one pane. Each image was drawn,
// under its own transform, into a pane-sized intermediate target that holds its stored sample
// values (alpha below zero: the image has no pixel there). This pass decides per screen pixel
// what the two become. A compare mode is a few lines in ps_main.
//
// The difference modes follow Fiv.Comparing.DifferenceMath, which is also what the files saved
// from the viewer are computed with: change one, change the other.

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
    float4 amount;    // modes 0-3: x: the divider's position in pane pixels, the opacity of B, or the cell size; yz: where the cells are centred
                      // mode 4: x: gain / value range; y: gain; z: the smallest value of A a relative difference is taken of; w: the mask's threshold
                      // mode 5: x: gain; y: 1 if the similarity is shown only where B has pixels
    uint mode;        // 0: A left of a divider, B right; 1: A above, B below; 2: B over A with an opacity; 3: checkerboard of A and B;
                      // 4: the difference B - A; 5: A is a similarity, shown as 1 - A
    uint channel;
    uint kind;        // of difference: 0 absolute, 1 signed, 2 squared, 3 relative
    uint mask;        // 1: instead of the difference, A dimmed and the pixels that differ by more than the threshold marked
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
    float4 storedA = imageA.Load(texel), storedB = imageB.Load(texel);

    float3 color;
    float height = 0;
    if (mode == 4)
    {
        if (storedA.a < 0 || storedB.a < 0) color = backdrop.rgb;
        else color = Difference(storedA, storedB, position.xy, height);
    }
    else if (mode == 5)
    {
        if (storedA.a < 0 || (amount.y > 0.5 && storedB.a < 0)) color = backdrop.rgb;
        else color = MapColor(height = (1 - storedA.r) * amount.x);
    }
    else
    {
        // Brightness is height, as for an image on its own; where an image has no pixels, nothing stands.
        float3 a = Shown(storedA, position.xy), b = Shown(storedB, position.xy);
        float heightA = storedA.a < 0 ? 0 : dot(a, LumaWeights), heightB = storedB.a < 0 ? 0 : dot(b, LumaWeights);
        float second; // how much of B: all or nothing, but for the blend
        if (mode == 0)
        {
            second = p.x >= amount.x ? 1 : 0;
        }
        else if (mode == 1)
        {
            second = p.y >= amount.x ? 1 : 0;
        }
        else if (mode == 2)
        {
            // B lies over A: where B has no pixels, A is seen in full.
            second = storedB.a < 0 ? 0 : amount.x;
        }
        else
        {
            float2 cell = floor((p - amount.yz) / amount.x);
            second = abs(fmod(cell.x + cell.y, 2)) < 0.5 ? 0 : 1;
        }
        color = lerp(a, b, second);
        height = lerp(heightA, heightB, second);
    }
    Output o;
    o.color = float4(color, 1);
    o.height = saturate(height);
    return o;
}
