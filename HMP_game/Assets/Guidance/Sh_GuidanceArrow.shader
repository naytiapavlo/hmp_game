// 路线指引流动发光着色器（URP）——地面箭头带与目的地标记共用。
// 视觉基准：ui/demo界面/0c7a3b35-…png（纯白自发光三角箭头 + Bloom 辉光 + 沿路径流动脉动）。
//
// 网格约定（由 FloorArrowRenderer / DestinationMarker 生成）：
//   uv.x = 单个箭头内的渐变：0=尾部 → 1=尖端（尖端更亮更实）；
//   uv.y = 沿路径的累计距离（米），用于产生沿路径方向流动的亮度波。
// 材质为独立资产（MAT_GuidanceArrow.mat），后续可整颗替换为 Astra 出的升级版视觉。
Shader "HMProtection/GuidanceArrow"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _PulseColor ("Pulse Color", Color) = (0.85, 0.95, 1, 1)
        _PulseSpeed ("Pulse Speed (cycles/s)", Float) = 0.8
        _PulseWaveLength ("Pulse Wave Length (m)", Float) = 2.0
        _Intensity ("Intensity", Float) = 1.4
        _Alpha ("Alpha", Range(0, 1)) = 0.95
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+50"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        Pass
        {
            Name "GuidanceFlow"
            // 加色混合：亮度可超 1（配合 HDR 相机与 Bloom 出辉光）；双面渲染（Cull Off）
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _PulseColor;
                float _PulseSpeed;
                float _PulseWaveLength;
                float _Intensity;
                float _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 箭头内渐变：尾部淡、尖端亮且实
                float tip = saturate(IN.uv.x);
                // 沿路径流动的亮度波（uv.y 为累计米数）
                float phase = IN.uv.y * 6.2831853 / max(_PulseWaveLength, 0.01) - _Time.y * _PulseSpeed;
                float flow = 0.5 + 0.5 * sin(phase);

                float bright = lerp(0.45, 1.0, tip) * lerp(0.75, 1.35, flow);
                half3 col = lerp(_BaseColor.rgb, _PulseColor.rgb, flow * 0.5) * bright * _Intensity;
                half alpha = _Alpha * saturate(0.25 + tip * 0.75);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
