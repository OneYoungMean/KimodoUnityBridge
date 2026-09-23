Shader "Hidden/Kimodo/PoseDepthEncode"
{
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
            #pragma vertex KimodoPoseDepthEncodeVert
            #pragma fragment KimodoPoseDepthEncodeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthEncode.hlsl"
            ENDCG
        }
    }

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
            #pragma vertex KimodoPoseDepthEncodeVert
            #pragma fragment KimodoPoseDepthEncodeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthEncode.hlsl"
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
            #pragma vertex KimodoPoseDepthEncodeVert
            #pragma fragment KimodoPoseDepthEncodeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthEncode.hlsl"
            ENDCG
        }
    }

    FallBack Off
}
