Shader "Hidden/Kimodo/PoseDepthEncode"
{
    // URP replacement/capture pass. Keep this variant first so SRP selects it
    // instead of the Built-in fallback below.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Name "PoseDepthEncodeURP"
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask R
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 position : SV_POSITION; };
            v2f vert(appdata input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); return output; }
            float frag(v2f input) : SV_Target { return input.position.z; }
            ENDCG
        }
    }

    // HDRP replacement/capture pass. It intentionally shares the same
    // hardware-depth encoding; no lighting or pipeline material features are
    // needed for this auxiliary render.
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Name "PoseDepthEncodeHDRP"
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask R
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 position : SV_POSITION; };
            v2f vert(appdata input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); return output; }
            float frag(v2f input) : SV_Target { return input.position.z; }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask R

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                return output;
            }

            float frag(v2f input) : SV_Target
            {
                // SV_POSITION.z is already the camera's hardware depth value.
                // Keep the platform's reversed-Z convention unchanged so the
                // compositor's depth test agrees with Unity's camera.
                return input.position.z;
            }
            ENDCG
        }
    }

    FallBack Off
}
