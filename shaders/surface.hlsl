// The 3D height map: the image as a surface whose height is the value it shows. Over each tile
// of the image a grid of vertices is drawn, a row of cells per instance as a triangle strip
// (no vertex buffer: SV_VertexID and SV_InstanceID say where a vertex is). Each vertex is raised
// by the height grid (Fiv.Surface.HeightField) after the display controls, so that black, white,
// gamma and exposure shape the surface as they shape the picture. The colour comes from the tile
// itself, every pixel of it, or from a colour map by height; a light from the image's upper
// left shades it by the slope, which is read from the height grid per screen pixel.

#include "display.hlsli"

Texture2D image : register(t0);            // one tile of the image: its stored values
Texture2D<float> heights : register(t1);   // the values heights are made of, over the whole image, with mip levels; unbound: flat
Texture2D<float4> colours : register(t2);  // the colour map: 256 texels in a row
SamplerState imageSampler : register(s0);
SamplerState heightSampler : register(s1);

cbuffer Surface : register(b0)
{
    row_major float4x4 worldToClip;
    float4 toWorldX;    // world x = dot(float3(imagePosition, 1), toWorldX.xyz): the ground is the upright image, or a comparison's canvas
    float4 toWorldZ;    // world z likewise; world y is the height
    float4 slopeX;      // xy: a slope along the image to one along the ground (the inverse transpose of the above): x
    float4 slopeZ;      // xy: the same, z
    float4 tileRect;    // the part of the image this tile draws: x0, y0, x1, y1 (image pixels)
    float4 tileUv;      // xy: image position of the texture's corner; zw: 1 / size the texture covers
    float4 texSize;     // xy: texture size in texels
    float4 window;      // see display.hlsli
    float4 grid;        // xy: where the tile's first column and row of vertices would be, unclipped; z: vertex spacing in image pixels; w: mip level of the heights the vertices read
    float4 surface;     // x: world height of a value shown as white; yz: 1 / image size in pixels; w: how much the light shades (0: not at all)
    float4 heightSize;  // xy: the height grid's size in values; z: the coarsest mip level the heights are read at, which smooths them
    float4 light;       // xyz: towards the light; w: 1 / its y, so that level ground is lit as 1
    float4 backdrop;    // rgb: what shows through transparent pixels
    uint sharp;         // 1: crisp texel edges where the image is magnified
    uint gray;          // 1: single-channel texture, shown as grey
    uint channel;       // see display.hlsli
    uint flags;         // 1: heights inverted; 2: coloured by height through the colour map
};

struct VsOut
{
    float4 position : SV_Position;
    float2 imagePosition : TEXCOORD0;
    float height : TEXCOORD1;
};

// The height 0..1 at a point of the height grid (uv 0..1 across the image).
float HeightAt(float2 uv, float level)
{
    float h = Tone(heights.SampleLevel(heightSampler, uv, level).xxx, window).x;
    return (flags & 1) ? 1 - h : h;
}

VsOut vs_main(uint id : SV_VertexID, uint row : SV_InstanceID)
{
    // Even vertices on the upper edge of the row of cells, odd ones on its lower edge.
    float2 cell = float2(id >> 1, row + (id & 1));
    float2 p = clamp(grid.xy + cell * grid.z, tileRect.xy, tileRect.zw);
    VsOut o;
    o.imagePosition = p;
    o.height = HeightAt(p * surface.yz, max(grid.w, heightSize.z));
    float3 world = float3(dot(float3(p, 1), toWorldX.xyz), o.height * surface.x, dot(float3(p, 1), toWorldZ.xyz));
    o.position = mul(float4(world, 1), worldToClip);
    return o;
}

// The image's stored values at a point, with crisp texel edges where it is magnified, as view.hlsl
// draws it. Here that is decided per pixel: near parts of the surface may be magnified, far ones not.
float4 Stored(float2 imagePosition)
{
    float2 texel = (imagePosition - tileUv.xy) * tileUv.zw * texSize.xy;
    float2 size = fwidth(texel);
    float2 dx = ddx(texel) / texSize.xy, dy = ddy(texel) / texSize.xy;
    if (sharp && max(size.x, size.y) < 1)
    {
        float2 edge = floor(texel + 0.5);
        texel = edge + clamp((texel - edge) / size, -0.5, 0.5);
    }
    float4 color = image.SampleGrad(imageSampler, texel / texSize.xy, dx, dy);
    return gray ? float4(color.rrr, 1) : color;
}

// The colour at a position 0..1 of the map: the two nearest entries blended.
float3 MapColor(float t)
{
    float p = saturate(t) * 255;
    int i = (int)p;
    return lerp(colours.Load(int3(i, 0, 0)).rgb, colours.Load(int3(min(i + 1, 255), 0, 0)).rgb, p - i);
}

float4 ps_main(VsOut i) : SV_Target
{
    // The slope here, from the heights on either side at the detail the screen shows of them.
    float2 uv = i.imagePosition * surface.yz;
    float level = max(heights.CalculateLevelOfDetail(heightSampler, uv), heightSize.z);
    float2 step = exp2(level) / heightSize.xy;
    float2 rise = float2(HeightAt(uv + float2(step.x, 0), level) - HeightAt(uv - float2(step.x, 0), level),
                         HeightAt(uv + float2(0, step.y), level) - HeightAt(uv - float2(0, step.y), level));
    float2 slope = rise * surface.x * surface.yz / (2 * step); // world height per image pixel
    float2 ground = float2(dot(slope, slopeX.xy), dot(slope, slopeZ.xy));
    float3 normal = normalize(float3(-ground.x, 1, -ground.y));
    float lit = lerp(1, saturate(dot(normal, light.xyz)) * light.w, surface.w);

    float3 color = (flags & 2) ? MapColor(i.height) : ShadeOver(Stored(i.imagePosition), backdrop.rgb, window, channel);
    return float4(color * lit, 1);
}
