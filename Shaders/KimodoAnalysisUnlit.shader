Shader "Kimodo/AnalysisUnlit"
{
    Properties
    {
        _BaseColorMap ("Base Color Map", 2D) = "white" {}
        _BaseMap ("Base Map", 2D) = "white" {}
        _MainTex ("Main Texture", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _MaskMap ("Mask Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Color ("Color", Color) = (1,1,1,1)
        _TintColor ("Tint Color", Color) = (1,1,1,1)
        _GhostTint ("Ghost Tint", Color) = (1,1,1,1)
        _GhostAlpha ("Ghost Alpha", Range(0,1)) = 1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _Roughness ("Roughness", Range(0,1)) = 0.5
    }

    // HDRP analysis pass. It is intentionally unlit so analysis remains
    // independent of scene lights and Volume exposure.
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"

            TEXTURE2D(_BaseColorMap);
            SAMPLER(sampler_BaseColorMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MaskMap);
            SAMPLER(sampler_MaskMap);
            float4 _BaseColor;
            float4 _Color;
            float4 _TintColor;
            float4 _GhostTint;
            float _GhostAlpha;
            float _Cutoff;
            float _Metallic;
            float _Smoothness;
            float _Roughness;
            float4 _KimodoEvidenceKey;
            float4 _KimodoEvidenceFill;
            float4 _KimodoEvidenceRim;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                output.color = input.color;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.tangentWS = normalize(TransformObjectToWorldDir(input.tangentOS.xyz));
                output.bitangentWS = normalize(cross(output.normalWS, output.tangentWS) * input.tangentOS.w);
                output.positionWS = TransformObjectToWorld(input.positionOS);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 color = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, input.uv);
                color *= _BaseColor * _GhostTint * input.color;
                clip(color.a - _Cutoff);
                float3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));
                float3 normalWS = normalize(
                    input.tangentWS * normalTS.x +
                    input.bitangentWS * normalTS.y +
                    input.normalWS * normalTS.z);
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float metallic = 0.0;
                float3 keyDir = normalize(_KimodoEvidenceKey.xyz);
                float3 fillDir = normalize(_KimodoEvidenceFill.xyz);
                float3 rimDir = normalize(_KimodoEvidenceRim.xyz);
                float diffuse = saturate(dot(normalWS, keyDir)) * _KimodoEvidenceKey.w * 0.16;
                diffuse += saturate(dot(normalWS, fillDir)) * _KimodoEvidenceFill.w * 0.10;
                diffuse += saturate(dot(normalWS, rimDir)) * _KimodoEvidenceRim.w * 0.08;
                float3 halfDir = normalize(keyDir + viewDir);
                float specular = pow(saturate(dot(normalWS, halfDir)), lerp(8.0, 96.0, _Smoothness));
                float lighting = 0.28 + diffuse * (0.72 + metallic * 0.2);
                color.rgb *= lighting;
                color.rgb += specular * (0.04 + metallic * 0.18) * _Smoothness;
                color.a *= _GhostAlpha;
                return color;
            }
            ENDHLSL
        }
    }

    // Built-in/URP compatibility pass. The same material is used on every
    // analysis path; the active pipeline selects the compatible SubShader.
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _BaseColorMap;
            float4 _BaseColorMap_ST;
            sampler2D _NormalMap;
            sampler2D _MaskMap;
            fixed4 _BaseColor;
            fixed4 _GhostTint;
            fixed4 _Color;
            fixed _GhostAlpha;
            fixed _Cutoff;
            fixed _Metallic;
            fixed _Smoothness;
            fixed _Roughness;
            fixed4 _KimodoEvidenceKey;
            fixed4 _KimodoEvidenceFill;
            fixed4 _KimodoEvidenceRim;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _BaseColorMap);
                output.color = input.color;
                output.normalWS = UnityObjectToWorldNormal(input.normal);
                output.tangentWS = UnityObjectToWorldDir(input.tangent.xyz);
                output.bitangentWS = cross(output.normalWS, output.tangentWS) * input.tangent.w;
                output.positionWS = mul(unity_ObjectToWorld, input.vertex).xyz;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_BaseColorMap, input.uv) * _BaseColor * _GhostTint * input.color;
                clip(color.a - _Cutoff);
                fixed3 normalTS = UnpackNormal(tex2D(_NormalMap, input.uv));
                fixed3 normalWS = normalize(input.tangentWS * normalTS.x + input.bitangentWS * normalTS.y + input.normalWS * normalTS.z);
                fixed3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                fixed metallic = 0.0;
                fixed3 keyDir = normalize(_KimodoEvidenceKey.xyz);
                fixed3 fillDir = normalize(_KimodoEvidenceFill.xyz);
                fixed3 rimDir = normalize(_KimodoEvidenceRim.xyz);
                fixed diffuse = saturate(dot(normalWS, keyDir)) * _KimodoEvidenceKey.w * 0.16;
                diffuse += saturate(dot(normalWS, fillDir)) * _KimodoEvidenceFill.w * 0.10;
                diffuse += saturate(dot(normalWS, rimDir)) * _KimodoEvidenceRim.w * 0.08;
                fixed3 halfDir = normalize(keyDir + viewDir);
                fixed specular = pow(saturate(dot(normalWS, halfDir)), lerp(8.0, 96.0, _Smoothness));
                color.rgb *= 0.28 + diffuse * (0.72 + metallic * 0.2);
                color.rgb += specular * (0.04 + metallic * 0.18) * _Smoothness;
                color.a *= _GhostAlpha;
                return color;
            }
            ENDCG
        }
    }

    FallBack Off
}
