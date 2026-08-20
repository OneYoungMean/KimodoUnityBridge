Shader "Hidden/Kimodo/PoseDepthComposite"
{
    Properties
    {
        _MainTex ("Pose Color", 2D) = "black" {}
        _LayerDepth ("Pose Depth", 2D) = "white" {}
        _Color ("Pose Alpha", Color) = (1,1,1,1)
    }

    // URP fullscreen composition pass. It writes the sampled pose depth to
    // SV_Depth so later pose layers can be compared per pixel.
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" "RenderType" = "Transparent" }
        Pass
        {
            Name "PoseDepthCompositeURP"
            Cull Off
            ZWrite On
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _LayerDepth;
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            struct fragOutput { fixed4 color : SV_Target; float depth : SV_Depth; };
            v2f vert(appdata input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); output.uv = input.uv; return output; }
            fragOutput frag(v2f input)
            {
                fragOutput output;
                output.color = tex2D(_MainTex, input.uv) * _Color;
                clip(output.color.a - 0.001);
                output.depth = tex2D(_LayerDepth, input.uv).r;
                return output;
            }
            ENDCG
        }
    }

    // HDRP fullscreen composition pass. The pass is deliberately unlit and
    // only carries color plus the explicitly sampled hardware depth.
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Overlay" "RenderType" = "Transparent" }
        Pass
        {
            Name "PoseDepthCompositeHDRP"
            Cull Off
            ZWrite On
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _LayerDepth;
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            struct fragOutput { fixed4 color : SV_Target; float depth : SV_Depth; };
            v2f vert(appdata input) { v2f output; output.position = UnityObjectToClipPos(input.vertex); output.uv = input.uv; return output; }
            fragOutput frag(v2f input)
            {
                fragOutput output;
                output.color = tex2D(_MainTex, input.uv) * _Color;
                clip(output.color.a - 0.001);
                output.depth = tex2D(_LayerDepth, input.uv).r;
                return output;
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }

        Pass
        {
            Cull Off
            ZWrite On
            // The fullscreen blit quad has no relation to the pose's depth;
            // the sampled layer depth below is the value that must be written.
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _LayerDepth;
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            struct fragOutput
            {
                fixed4 color : SV_Target;
                float depth : SV_Depth;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fragOutput frag(v2f input)
            {
                fixed4 color = tex2D(_MainTex, input.uv) * _Color;
                clip(color.a - 0.001);

                fragOutput output;
                output.color = color;
                output.depth = tex2D(_LayerDepth, input.uv).r;
                return output;
            }
            ENDCG
        }
    }

    FallBack Off
}
