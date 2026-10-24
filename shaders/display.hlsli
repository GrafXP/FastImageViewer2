// What the display controls do to a stored sample. Shared by the shader that draws one image
// and the one that combines two, so that every view and every compare mode shows the same
// values the same way.

// channel: 0 colour; 1 red, 2 green, 3 blue, 5 luma, each as grey; 4 alpha as grey
// window:  x: low; y: 1 / (high - low); z: 1 / gamma; w: gain (2 ^ exposure)
// checker: x: cell size in screen pixels; y, z: the two grey levels behind transparent pixels

float3 Tone(float3 value, float4 window)
{
    value = saturate((value - window.x) * window.y * window.w);
    return pow(value, window.z);
}

float3 TransparencyPattern(float2 screenPosition, float4 checker)
{
    float2 cell = floor(screenPosition / checker.x);
    return lerp(checker.y, checker.z, abs(fmod(cell.x + cell.y, 2))).xxx;
}

// A stored sample (straight alpha) to the colour on screen, over what shows through where it is transparent.
float3 ShadeOver(float4 color, float3 behind, float4 window, uint channel)
{
    if (channel == 4) return color.aaa; // alpha is shown as it is: 0 black, 1 white
    if (channel == 1) color.rgb = color.rrr;
    else if (channel == 2) color.rgb = color.ggg;
    else if (channel == 3) color.rgb = color.bbb;
    else if (channel == 5) color.rgb = dot(color.rgb, float3(0.2126, 0.7152, 0.0722)).xxx;
    return lerp(behind, Tone(color.rgb, window), color.a);
}

// The same over the checkerboard.
float3 Shade(float4 color, float2 screenPosition, float4 window, float4 checker, uint channel)
{
    return ShadeOver(color, TransparencyPattern(screenPosition, checker), window, channel);
}
