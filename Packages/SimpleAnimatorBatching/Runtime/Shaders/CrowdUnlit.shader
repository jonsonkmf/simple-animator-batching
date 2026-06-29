Shader "Simple Animator Batching/Crowd Unlit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        // Per-instance bone offset (instance * boneCount). Driven by the BatchRendererGroup via DOTS
        // instancing; the serialized default is only the non-instanced fallback.
        [HideInInspector] _BoneOffset ("Bone Offset", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.simpleanimatorbatching.core/Runtime/Shaders/SkinningCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _BoneOffset;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BoneOffset)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #define _BoneOffset UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BoneOffset)
            #endif

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float3 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float2 uv          : TEXCOORD0;
                float4 boneIndices : TEXCOORD2;
                float4 boneWeights : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                uint boneOffset = (uint)(_BoneOffset.x + 0.5);

                float3 positionWS, normalWS, tangentWS;
                SAB_Skin(input.positionOS, input.normalOS, float4(1,0,0,1),
                         input.boneIndices, input.boneWeights, boneOffset,
                         positionWS, normalWS, tangentWS);

                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.normalWS = normalWS;
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                // Cheap hemispheric term so the skinned silhouette is readable in this unlit test pass.
                half ndl = saturate(dot(normalize(input.normalWS), normalize(half3(0.3, 0.9, 0.2)))) * 0.5h + 0.5h;
                return half4(col.rgb * ndl, col.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
