// Draws one texture over the whole current viewport. The caller positions the image by
// setting the viewport, so no vertex buffer and no constants are needed.

Texture2D image : register(t0);
SamplerState imageSampler : register(s0);

struct VsOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

// One triangle that covers the viewport: ids 0,1,2 -> uv (0,0), (2,0), (0,2).
VsOut vs_main(uint id : SV_VertexID)
{
    VsOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 ps_main(VsOut i) : SV_Target
{
    return image.Sample(imageSampler, i.uv);
}
