// 火焰/烟雾粒子着色器（URP）——占位视觉用，Astra 交付正式 Shader Graph 后可整体替换。
// 单着色器双混合模式：材质里设 _SrcMode/_DstMode 即可切换 加色（火焰）与透明（烟雾）。
// 颜色 = 贴图 × 顶点色（粒子 Color over Lifetime）× _TintColor。
Shader "HMProtection/ParticleFx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _TintColor ("Tint Color", Color) = (1, 1, 1, 1)
        // 混合模式经装配脚本写入（SrcMode 固定 SrcAlpha=5；DstMode: 1=One 加色，10=OneMinusSrcAlpha 普通）
        [HideInInspector] _SrcMode ("Src Blend", Float) = 5
        [HideInInspector] _DstMode ("Dst Blend", Float) = 1
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }
        Pass
        {
            Name "Particle"
            Blend [_SrcMode][_DstMode]
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                float4 _MainTex_ST;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                return tex * IN.color * _TintColor;
            }
            ENDHLSL
        }
    }
}
