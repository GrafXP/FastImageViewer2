// The final pass of the compare modes that show two images in one pane. Each image was drawn,
// under its own transform, into a pane-sized intermediate target that holds its stored sample
// values (alpha below zero: the image has no pixel there). This pass decides per screen pixel
// what the two become. A compare mode is a few lines in ps_main.

#include "display.hlsli"

Texture2D<float4> imageA : register(t0);
Texture2D<float4> imageB : register(t1);

cbuffer Combine : register(b0)
{
    float4 window;    // see display.hlsli
    float4 checker;
    float4 backdrop;  // rgb: what is shown where an image has no pixels
    float4 pane;      // xy: the pane's top-left corner on the render target
    float4 amount;    // x: by mode, the divider's position in pane pixels, the opacity of B, or the cell size; yz: where the cells are centred
    uint mode;        // 0: A left of a divider, B right; 1: A above, B below; 2: B over A with an opacity; 3: checkerboard of A and B
    uint channel;
    uint2 unused;
};

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

float4 ps_main(float4 position : SV_Position) : SV_Target
{
    float2 p = position.xy - pane.xy;
    int3 texel = int3(p, 0);
    float4 storedA = imageA.Load(texel), storedB = imageB.Load(texel);
    float3 a = Shown(storedA, position.xy), b = Shown(storedB, position.xy);

    float3 color;
    if (mode == 0)
    {
        color = p.x < amount.x ? a : b;
    }
    else if (mode == 1)
    {
        color = p.y < amount.x ? a : b;
    }
    else if (mode == 2)
    {
        // B lies over A: where B has no pixels, A is seen in full.
        color = lerp(a, b, storedB.a < 0 ? 0 : amount.x);
    }
    else
    {
        float2 cell = floor((p - amount.yz) / amount.x);
        color = abs(fmod(cell.x + cell.y, 2)) < 0.5 ? a : b;
    }
    return float4(color, 1);
}
