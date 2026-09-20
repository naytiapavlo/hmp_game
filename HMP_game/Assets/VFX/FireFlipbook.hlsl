#ifndef HMP_FIRE_FLIPBOOK_INCLUDED
#define HMP_FIRE_FLIPBOOK_INCLUDED
#if !defined(SHADERGRAPH_PREVIEW)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#endif

float HmpFireNoise(float2 p)
{
    return sin(p.x * 9.1 + sin(p.y * 5.3)) * sin(p.y * 8.7 + cos(p.x * 4.1));
}

void FireFlipbook_float(UnityTexture2D Atlas, float4 UV, float4 Tint,
    float3 PositionWS, float Gain, float Distortion, float SoftDistance,
    out float3 RGB, out float Alpha)
{
    // UV.xy = unmodified sprite UV; z = AgePercent; w = StableRandomX.
    // Particle age drives both animation and turbulence: Pause freezes every layer.
    float age = saturate(UV.z);
    float phase = age * 6.0 + UV.w * 13.0;
    float2 p = UV.xy;
    float edge = smoothstep(0.0, 0.08, p.x) * smoothstep(0.0, 0.08, 1.0-p.x)
               * smoothstep(0.0, 0.025, p.y) * smoothstep(0.0, 0.08, 1.0-p.y);
    p += float2(HmpFireNoise(p*2.0 + float2(0,-phase)),
                HmpFireNoise(p*2.7 + float2(phase*.2,-phase*.8))) * Distortion * edge;
    p = clamp(p, .012, .988); // gutter prevents bilinear sampling adjacent atlas cells
    float frame = age * 63.0;
    float a = floor(frame), b = min(a+1.0, 63.0);
    float2 ca = float2(fmod(a,8.0), 7.0-floor(a/8.0));
    float2 cb = float2(fmod(b,8.0), 7.0-floor(b/8.0));
    float4 sa = SAMPLE_TEXTURE2D(Atlas.tex, Atlas.samplerstate, (ca+p)/8.0);
    float4 sb = SAMPLE_TEXTURE2D(Atlas.tex, Atlas.samplerstate, (cb+p)/8.0);
    // Interpolate premultiplied color, then return straight alpha to the SG blend mode.
    float alpha = lerp(sa.a,sb.a,frac(frame));
    float3 rgb = lerp(sa.rgb*sa.a,sb.rgb*sb.a,frac(frame))/max(alpha,.0001);
    float fade=1.0;
#if !defined(SHADERGRAPH_PREVIEW)
    if (SoftDistance > .0001)
    {
        float4 clip = TransformWorldToHClip(PositionWS);
        float4 screen = ComputeScreenPos(clip);
        float rawDepth = SampleSceneDepth(screen.xy/screen.w);
        float eyeDepth = LinearEyeDepth(rawDepth,_ZBufferParams);
        float particleDepth = -TransformWorldToView(PositionWS).z;
        fade = saturate((eyeDepth-particleDepth)/SoftDistance);
    }
#endif
    RGB = rgb * Tint.rgb * Gain;
    Alpha = saturate(alpha * Tint.a * edge * fade);
}
#endif
