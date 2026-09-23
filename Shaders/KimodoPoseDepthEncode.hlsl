struct KimodoPoseDepthEncodeAppData
{
    float4 vertex : POSITION;
};

struct KimodoPoseDepthEncodeV2F
{
    float4 position : SV_POSITION;
};

KimodoPoseDepthEncodeV2F KimodoPoseDepthEncodeVert(KimodoPoseDepthEncodeAppData input)
{
    KimodoPoseDepthEncodeV2F output;
    output.position = UnityObjectToClipPos(input.vertex);
    return output;
}

float KimodoPoseDepthEncodeFrag(KimodoPoseDepthEncodeV2F input) : SV_Target
{
    return input.position.z;
}
