// Draws one tile of an image: position from an affine transform of image coordinates,
// then the display controls (brightness window, gamma) and the transparency checkerboard.

Texture2D image : register(t0);
SamplerState imageSampler : register(s0);

cbuffer View : register(b0)
{
    float4 clipX;     // clip.x = dot(float3(imagePosition, 1), clipX.xyz)
    float4 clipY;
    float4 tileRect;  // the part of the image this tile draws: x0, y0, x1, y1 (image pixels)
    float4 tileUv;    // xy: image position of the texture's corner; zw: 1 / size the texture covers
    float4 texSize;   // xy: texture size in texels
    float4 window;    // x: low; y: 1 / (high - low); z: 1 / gamma
    float4 checker;   // x: cell size in screen pixels; y, z: the two grey levels
    uint sharp;       // 1: crisp texel edges when magnified; 0: plain (tri)linear
    uint gray;        // 1: single-channel texture, shown as grey
    uint2 unused;
};

struct VsOut
{
    float4 position : SV_Position;
    float2 imagePosition : TEXCOORD0;
};

VsOut vs_main(uint id : SV_VertexID)
{
    float2 corner = float2(id & 1, id >> 1);
    VsOut o;
    o.imagePosition = lerp(tileRect.xy, tileRect.zw, corner);
    float3 p = float3(o.imagePosition, 1);
    o.position = float4(dot(p, clipX.xyz), dot(p, clipY.xyz), 0, 1);
    return o;
}

float4 ps_main(VsOut i) : SV_Target
{
    float2 texel = (i.imagePosition - tileUv.xy) * tileUv.zw * texSize.xy;
    if (sharp)
    {
        // Sample texel centres, except within half a screen pixel of a texel edge, where the two
        // neighbours are blended: exact pixels without the shimmer of nearest-neighbour sampling.
        float2 edge = floor(texel + 0.5);
        texel = edge + clamp((texel - edge) / fwidth(texel), -0.5, 0.5);
    }
    float4 color = image.Sample(imageSampler, texel / texSize.xy);
    if (gray) color = float4(color.rrr, 1);

    color.rgb = saturate((color.rgb - window.x) * window.y);
    color.rgb = pow(color.rgb, window.z);

    float2 cell = floor(i.position.xy / checker.x);
    float background = lerp(checker.y, checker.z, fmod(cell.x + cell.y, 2));
    return float4(lerp(background.xxx, color.rgb, color.a), 1);
}
