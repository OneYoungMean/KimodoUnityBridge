Shader "Hidden/Kimodo/PoseDepthComposite"
{
    Properties
    {
        _MainTex ("Pose Color", 2D) = "black" {}
        _LayerDepth ("Pose Depth", 2D) = "white" {}
        _Color ("Pose Alpha", Color) = (1,1,1,1)
    }

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
            #pragma vertex KimodoPoseDepthCompositeVert
            #pragma fragment KimodoPoseDepthCompositeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthComposite.hlsl"
            ENDCG
        }
    }

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
            #pragma vertex KimodoPoseDepthCompositeVert
            #pragma fragment KimodoPoseDepthCompositeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthComposite.hlsl"
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
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex KimodoPoseDepthCompositeVert
            #pragma fragment KimodoPoseDepthCompositeFrag
            #include "UnityCG.cginc"
            #include "KimodoPoseDepthComposite.hlsl"
            ENDCG
        }
    }

    FallBack Off
}
