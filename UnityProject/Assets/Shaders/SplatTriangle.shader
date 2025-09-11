Shader "Custom/SplatTriangle"
{
    SubShader
    {
        // keep it simple/compatible
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        Cull Off
        Blend One OneMinusSrcAlpha

        Pass
        {
  Properties
{
    _AlphaCutoff("Alpha Cutoff", Range(0,1)) = 0.3
}

HLSLPROGRAM
#pragma target 4.5
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"

struct TriSplat {
    float3 v0;
    float3 v1;
    float3 v2;
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float4 col0;
    float4 col1;
    float4 col2;
    int    matID;  // NEW - must exist in both C# + HLSL
};




StructuredBuffer<TriSplat> _TriSplatData;

TEXTURE2D_ARRAY(_BaseMaps);
SAMPLER(sampler_BaseMaps);
float _AlphaCutoff;

struct VSOUT {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
    float4 col : COLOR0;
    int    mid : TEXCOORD1;
};

VSOUT vert(uint id : SV_VertexID, uint inst : SV_InstanceID)
{
    TriSplat s = _TriSplatData[inst];
    float3 wp = (id == 0) ? s.v0 : (id == 1) ? s.v1 : s.v2;
    float2 uv = (id == 0) ? s.uv0 : (id == 1) ? s.uv1 : s.uv2;
    float4 c  = (id == 0) ? s.c0  : (id == 1) ? s.c1  : s.c2;

    VSOUT o;
    o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1));
    o.uv  = uv;
    o.col = c;
    o.mid = s.matID;
    return o;
}

float4 frag(VSOUT i) : SV_Target
{
    float4 tex = SAMPLE_TEXTURE2D_ARRAY(_BaseMaps, sampler_BaseMaps, i.uv, i.mid);
    if (tex.a < _AlphaCutoff) discard;
    return tex * i.col;
}
ENDHLSL

        }
    }
}
