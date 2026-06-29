Shader "Simple Animator Batching/Crowd Lit"
{
    Properties
    {
        _BaseMap        ("Base Map", 2D) = "white" {}
        _BaseColor      ("Base Color", Color) = (1,1,1,1)
        _Metallic       ("Metallic", Range(0,1)) = 0.0
        _Smoothness     ("Smoothness", Range(0,1)) = 0.5
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale      ("Normal Scale", Float) = 1.0
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,0)
        _EmissionMap    ("Emission Map", 2D) = "white" {}

        // Per-instance bone offset (instance * boneCount), driven by the BatchRendererGroup via DOTS
        // instancing. The serialized default is only the non-instanced fallback.
        [HideInInspector] _BoneOffset ("Bone Offset", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        // ---- Shared material data + skinning ------------------------------------------------
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // CommonMaterial.hlsl (LerpWhiteTo etc.) is pulled in by Lighting.hlsl for the forward pass,
        // but the shadow/depth passes that include only Shadows.hlsl need it explicitly.
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
        #include "Packages/com.simpleanimatorbatching.core/Runtime/Shaders/SkinningCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _EmissionColor;
            float  _Metallic;
            float  _Smoothness;
            float  _BumpScale;
            float4 _BoneOffset;
        CBUFFER_END

        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float4, _BoneOffset)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
        #define _BoneOffset UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BoneOffset)
        #endif

        TEXTURE2D(_BaseMap);     SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);     SAMPLER(sampler_BumpMap);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);

        // Skins the common vertex attributes to world space using the per-instance bone offset.
        void SAB_SkinAttributes(float3 positionOS, float3 normalOS, float4 tangentOS,
                                float4 boneIndices, float4 boneWeights,
                                out float3 positionWS, out float3 normalWS, out float3 tangentWS)
        {
            uint boneOffset = (uint)(_BoneOffset.x + 0.5);
            SAB_Skin(positionOS, normalOS, tangentOS, boneIndices, boneWeights, boneOffset,
                     positionWS, normalWS, tangentWS);
        }
        ENDHLSL

        // ====================================================================================
        //  Forward lighting
        // ====================================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 tangentOS   : TANGENT;
                float2 uv          : TEXCOORD0;
                float4 boneIndices : TEXCOORD2;
                float4 boneWeights : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float4 tangentWS   : TEXCOORD3; // xyz dir, w sign
                float  fogFactor   : TEXCOORD4;
            };

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS, normalWS, tangentWS;
                SAB_SkinAttributes(input.positionOS, input.normalOS, input.tangentOS,
                                   input.boneIndices, input.boneWeights,
                                   positionWS, normalWS, tangentWS);

                o.positionWS = positionWS;
                o.normalWS = normalWS;
                o.tangentWS = float4(tangentWS, input.tangentOS.w * GetOddNegativeScale());
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Tangent-space normal map -> world space.
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                float sgn = input.tangentWS.w;
                float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, bitangent, input.normalWS.xyz);
                half3 normalWS = NormalizeNormalPerPixel(mul(normalTS, tbn));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseMap.rgb;
                surfaceData.alpha = baseMap.a;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.occlusion = 1.0h;
                surfaceData.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb * _EmissionColor.rgb;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS); // v1: constant ambient SH (no per-instance probes)
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1,1,1,1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // ====================================================================================
        //  Shadow caster — MUST skin with the exact same function as ForwardLit so the cast
        //  shadow matches the lit silhouette.
        // ====================================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float3 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 boneIndices : TEXCOORD2;
                float4 boneWeights : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float4 GetShadowClip(float3 positionWS, float3 normalWS)
            {
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS, normalWS, tangentWS;
                SAB_SkinAttributes(input.positionOS, input.normalOS, float4(1,0,0,1),
                                   input.boneIndices, input.boneWeights,
                                   positionWS, normalWS, tangentWS);

                o.positionCS = GetShadowClip(positionWS, normalWS);
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ====================================================================================
        //  Depth only (used by depth prepass / depth texture)
        // ====================================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DOTS_INSTANCING_ON

            struct Attributes
            {
                float3 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 boneIndices : TEXCOORD2;
                float4 boneWeights : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS, normalWS, tangentWS;
                SAB_SkinAttributes(input.positionOS, input.normalOS, float4(1,0,0,1),
                                   input.boneIndices, input.boneWeights,
                                   positionWS, normalWS, tangentWS);
                o.positionCS = TransformWorldToHClip(positionWS);
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ====================================================================================
        //  Depth + normals (used by SSAO / depth-normals prepass)
        // ====================================================================================
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ DOTS_INSTANCING_ON

            struct Attributes
            {
                float3 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 tangentOS   : TANGENT;
                float4 boneIndices : TEXCOORD2;
                float4 boneWeights : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS, normalWS, tangentWS;
                SAB_SkinAttributes(input.positionOS, input.normalOS, input.tangentOS,
                                   input.boneIndices, input.boneWeights,
                                   positionWS, normalWS, tangentWS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = normalWS;
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                return half4(normalWS * 0.5 + 0.5, 0.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
