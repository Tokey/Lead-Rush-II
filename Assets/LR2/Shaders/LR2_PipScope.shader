// LR2 B136i: Circular Picture-in-Picture (PiP) optical scope lens shader for HDRP 17.5.
// Keyword-free ForwardOnly shader so standalone builds never strip scope variants.
// Clips square demo Plane corners into a crisp circle, adds subtle optical pincushion warp + eye-relief vignette,
// and composites a sharp reticle scaled accurately by _CrosshairSize.
Shader "LR2/PipScope"
{
    Properties
    {
        _MainTex ("Scope RenderTexture", 2D) = "black" {}
        _CrosshairTex ("Reticle Texture", 2D) = "black" {}
        [HDR] _CrosshairColor ("Reticle Color", Color) = (2.8, 0.14, 0.10, 1)
        _CrosshairSize ("Reticle Scale", Range(0.15, 1.5)) = 0.5
        _LensRadius ("Circular Lens Radius", Range(0.30, 0.50)) = 0.485
        _EdgeSoftness ("Eye-Relief Edge Vignette", Range(0.01, 0.25)) = 0.065
        _Distortion ("Optical Pincushion", Range(-0.25, 0.25)) = 0.07
        _Brightness ("Lens Gain", Range(0.5, 2.0)) = 1.15
        _Tint ("Optical Glass Tint", Color) = (0.95, 0.98, 1.0, 1.0)
    }

    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="AlphaTest" "Queue"="AlphaTest" "IgnoreProjector"="True" }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_CrosshairTex);
            SAMPLER(sampler_CrosshairTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _CrosshairColor;
                float _CrosshairSize;
                float _LensRadius;
                float _EdgeSoftness;
                float _Distortion;
                float _Brightness;
                float4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionRWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(positionRWS);
                o.uv = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float2 p = i.uv - 0.5;
                float r = length(p);
                clip(_LensRadius - r);

                float rNorm = saturate(r / max(0.001, _LensRadius));
                float2 warpedUv = 0.5 + p * (1.0 - _Distortion * (rNorm * rNorm));
                float3 sceneCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, saturate(warpedUv)).rgb * _Tint.rgb * _Brightness;

                float vig = smoothstep(_LensRadius, _LensRadius - max(0.005, _EdgeSoftness), r);
                sceneCol *= lerp(0.06, 1.0, vig);

                float2 retUv = 0.5 + p / max(0.08, _CrosshairSize);
                float inRet = step(0.0, retUv.x) * step(retUv.x, 1.0) * step(0.0, retUv.y) * step(retUv.y, 1.0);
                float4 ret = SAMPLE_TEXTURE2D(_CrosshairTex, sampler_CrosshairTex, saturate(retUv));
                float retMask = smoothstep(0.20, 0.70, ret.a) * inRet * vig;

                float3 outCol = lerp(sceneCol, _CrosshairColor.rgb, saturate(retMask * _CrosshairColor.a));
                return float4(outCol, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
