Shader "Custom/URP/TerrainRandomRotate"
{
    Properties
    {
        [HideInInspector] _Control("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat3("Layer 3 (A)", 2D) = "grey" {}
        [HideInInspector] _Splat2("Layer 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Splat1("Layer 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Splat0("Layer 0 (R)", 2D) = "grey" {}
        [HideInInspector] _MainTex("BaseMap (RGB)", 2D) = "grey" {}
        [HideInInspector] _BaseColor("Main Color", Color) = (1,1,1,1)

        _RandomGridSize("Random Grid Size (Meters)", Float) = 25.0
        _RandomStrength("Random Rotation Strength", Range(0.0, 1.0)) = 1.0
        _TileScaleMultiplier("Extra Tile Scale", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Control);
            SAMPLER(sampler_Control);
            float4 _Control_ST;

            TEXTURE2D(_Splat0); SAMPLER(sampler_Splat0); float4 _Splat0_ST;
            TEXTURE2D(_Splat1); SAMPLER(sampler_Splat1); float4 _Splat1_ST;
            TEXTURE2D(_Splat2); SAMPLER(sampler_Splat2); float4 _Splat2_ST;
            TEXTURE2D(_Splat3); SAMPLER(sampler_Splat3); float4 _Splat3_ST;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _RandomGridSize;
                float _RandomStrength;
                float _TileScaleMultiplier;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uvControl  : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float2 RotateUV(float2 uv, float angleRadians, float2 pivot)
            {
                float2 centered = uv - pivot;
                float s = sin(angleRadians);
                float c = cos(angleRadians);
                float2 rotated = float2(
                    centered.x * c - centered.y * s,
                    centered.x * s + centered.y * c
                );
                return rotated + pivot;
            }

            // Rotasi acak berbasis grid dunia agar pola tidak berulang seragam
            float2 GetRandomRotatedUV(float2 worldXZ, float4 splatST, float seedOffset)
            {
                float gridSize = max(_RandomGridSize, 0.001);
                float2 scaledWorld = worldXZ / gridSize;
                float2 cellId = floor(scaledWorld);

                float rnd = Hash21(cellId + seedOffset);
                // Snap ke kelipatan 90 derajat (0, 90, 180, 270) agar edge tile tetap pas
                float snappedTurns = floor(rnd * 4.0);
                float angle = snappedTurns * 1.57079632679 * saturate(_RandomStrength);

                float2 localCellUV = frac(scaledWorld);
                float2 rotatedCellUV = RotateUV(localCellUV, angle, float2(0.5, 0.5));

                float2 finalGridUV = cellId + rotatedCellUV;
                float scaleFactor = max(_TileScaleMultiplier, 0.0001);
                return finalGridUV * (gridSize * scaleFactor) * splatST.xy + splatST.zw;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normInputs.normalWS);
                output.uvControl = TRANSFORM_TEX(input.uv, _Control);
                output.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 splatControl = SAMPLE_TEXTURE2D(_Control, sampler_Control, input.uvControl);
                half weightSum = max(splatControl.r + splatControl.g + splatControl.b + splatControl.a, 0.0001h);
                splatControl /= weightSum;

                float2 uv0 = GetRandomRotatedUV(input.positionWS.xz, _Splat0_ST, 1.37);
                float2 uv1 = GetRandomRotatedUV(input.positionWS.xz, _Splat1_ST, 7.91);
                float2 uv2 = GetRandomRotatedUV(input.positionWS.xz, _Splat2_ST, 13.53);
                float2 uv3 = GetRandomRotatedUV(input.positionWS.xz, _Splat3_ST, 29.17);

                half3 col0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0).rgb;
                half3 col1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, uv1).rgb;
                half3 col2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, uv2).rgb;
                half3 col3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, uv3).rgb;

                half3 albedo = (col0 * splatControl.r +
                                col1 * splatControl.g +
                                col2 * splatControl.b +
                                col3 * splatControl.a) * _BaseColor.rgb;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 N = normalize(input.normalWS);
                float NdotL = saturate(dot(N, mainLight.direction));

                half3 ambient = SampleSH(N);
                half3 lighting = ambient + mainLight.color * (NdotL * mainLight.shadowAttenuation);

                #ifdef _ADDITIONAL_LIGHTS
                uint pixelLightCount = GetAdditionalLightsCount();
                for (uint i = 0u; i < pixelLightCount; ++i)
                {
                    Light addLight = GetAdditionalLight(i, input.positionWS);
                    float addNdotL = saturate(dot(N, addLight.direction));
                    lighting += addLight.color * (addNdotL * addLight.distanceAttenuation * addLight.shadowAttenuation);
                }
                #endif

                half3 finalColor = albedo * lighting;
                finalColor = MixFog(finalColor, input.fogFactor);
                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVary
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVary ShadowVert(ShadowAttr input)
            {
                ShadowVary output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = GetShadowPositionHClip(posInputs.positionWS, normalize(TransformObjectToWorldNormal(input.normalOS)), _MainLightPosition.xyz);
                return output;
            }

            half4 ShadowFrag(ShadowVary input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}