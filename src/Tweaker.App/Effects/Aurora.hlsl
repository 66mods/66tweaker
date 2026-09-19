// Aurora backdrop. A port of the fragment shader behind the approved mockup: four layers of value noise
// drifting at different speeds, tinted purple, magenta and a trace of gold, lit from the right.
//
// ps_3_0 because the nested noise runs to a few hundred instructions. WPF only runs ps_3_0 in hardware,
// so the control that hosts this checks RenderCapability first and falls back to the vector backdrop.
float Time : register(c0);
float Aspect : register(c1);

float hashNoise(float n) { return frac(sin(n) * 43758.5453123); }

float noise2d(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = hashNoise(i.x + hashNoise(i.y));
    float b = hashNoise(i.x + 1.0 + hashNoise(i.y));
    float c = hashNoise(i.x + hashNoise(i.y + 1.0));
    float d = hashNoise(i.x + 1.0 + hashNoise(i.y + 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float3 aurora(float2 uv, float layerSpeed, float intensity, float3 color)
{
    float time = Time * 0.6 * layerSpeed;
    float2 scaled = float2(uv.x * Aspect, uv.y) * 3.0;
    float2 pt = scaled + time * float2(-1.1, -2.2);
    float n = noise2d(pt + noise2d(color.xy + pt + time));
    float alpha = n - uv.y * 0.55;
    return color * alpha * intensity * 1.4;
}

float4 main(float2 tex : TEXCOORD) : COLOR
{
    // The original ran with y up; WPF hands y down.
    float2 uv = float2(tex.x, 1.0 - tex.y);
    float3 color = 0;
    color += aurora(uv, 0.35, 0.56, float3(0.545, 0.361, 0.965));
    color += aurora(uv, 0.18, 0.50, float3(0.753, 0.149, 0.827));
    color += aurora(uv, 0.12, 0.34, float3(0.357, 0.165, 0.525));
    color += aurora(uv, 0.08, 0.14, float3(0.902, 0.725, 0.290));
    color *= lerp(0.22, 1.0, smoothstep(0.05, 0.95, uv.x));
    color += float3(0.078, 0.063, 0.118) * (1.0 - smoothstep(0.78, 1.0, uv.y));
    color += float3(0.043, 0.039, 0.071) * (1.0 - smoothstep(0.0, 0.52, uv.y));
    float gray = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(gray.xxx, color, 1.15) * 0.66;
    color = max(color, float3(0.039, 0.035, 0.071));
    return float4(color, 1.0);
}
