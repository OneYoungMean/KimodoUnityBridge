sampler2D _MainTex;
sampler2D _LayerDepth;
fixed4 _Color;

struct KimodoPoseDepthCompositeAppData
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct KimodoPoseDepthCompositeV2F
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

struct KimodoPoseDepthCompositeOutput
{
    fixed4 color : SV_Target;
    float depth : SV_Depth;
};

KimodoPoseDepthCompositeV2F KimodoPoseDepthCompositeVert(KimodoPoseDepthCompositeAppData input)
{
    KimodoPoseDepthCompositeV2F output;
    output.position = UnityObjectToClipPos(input.vertex);
    output.uv = input.uv;
    return output;
}

KimodoPoseDepthCompositeOutput KimodoPoseDepthCompositeFrag(KimodoPoseDepthCompositeV2F input)
{
    KimodoPoseDepthCompositeOutput output;
    output.color = tex2D(_MainTex, input.uv) * _Color;
    clip(output.color.a - 0.001);
    output.depth = tex2D(_LayerDepth, input.uv).r;
    return output;
}
