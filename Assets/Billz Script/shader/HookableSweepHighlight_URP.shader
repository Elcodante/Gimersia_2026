Shader "Billz/Hookable Sweep Highlight URP"
{
    Properties
    {
        [HDR] _GlowColor ("Highlight Color", Color) = (1, 0.82, 0.42, 1)
        _SweepPosition ("Sweep Position", Range(-1, 2)) = -1
        _SweepWidth ("Sweep Width", Range(0.01, 1)) = 0.22
        _SweepIntensity ("Sweep Intensity", Range(0, 10)) = 1.8
        _ObjectBottomY ("Object Bottom Y", Float) = 0
        _ObjectHeight ("Object Height", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HookableSweepOverlay"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _GlowColor;
                float _SweepPosition;
                float _SweepWidth;
                float _SweepIntensity;
                float _ObjectBottomY;
                float _ObjectHeight;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 viewDirectionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionHCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.viewDirectionWS = GetWorldSpaceViewDir(positionInputs.positionWS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float normalizedHeight = saturate(
                    (input.positionWS.y - _ObjectBottomY) / max(_ObjectHeight, 0.001)
                );

                float distanceFromBand = abs(normalizedHeight - _SweepPosition);
                float halfWidth = max(_SweepWidth * 0.5, 0.001);

                // Bright narrow core plus a softer halo around the moving band.
                float core = 1.0 - smoothstep(halfWidth * 0.45, halfWidth, distanceFromBand);
                float halo = 1.0 - smoothstep(halfWidth, halfWidth * 2.4, distanceFromBand);
                float band = saturate(core + halo * 0.22);

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = normalize(input.viewDirectionWS);
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), 2.0h);
                half surfaceFactor = 0.72h + fresnel * 0.28h;

                half3 glow = _GlowColor.rgb * (_SweepIntensity * band * surfaceFactor);
                return half4(glow, band);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
