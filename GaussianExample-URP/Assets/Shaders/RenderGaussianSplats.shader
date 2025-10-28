// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
// #pragma use_dxc   // <- optional for BiRP; safe to omit if it causes issues

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<uint> _SplatIDBuffer;   // stable IDs, bound from C#

// ---- NEW: palette (use legacy BiRP types) ----
sampler2D _LabelPaletteTex;
int _LabelPaletteCount;

struct v2f
{
    half4 col    : COLOR0;
    float2 pos   : TEXCOORD0;
    float4 vertex: SV_POSITION;
    uint id      : TEXCOORD1;   // stable splat ID
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
    SplatViewData view = _SplatViewData[instID];
    float4 centerClipPos = view.pos;
    bool behindCam = centerClipPos.w <= 0;
    if (behindCam)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
    }
    else
    {
        o.col.r = f16tof32(view.color.x >> 16);
        o.col.g = f16tof32(view.color.x);
        o.col.b = f16tof32(view.color.y >> 16);
        o.col.a = f16tof32(view.color.y);

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;

        // Stable ID fetch
        o.id = _SplatIDBuffer[instID];

        // is this splat selected?
        if (_SplatBitsValid != 0)
        {
            uint wordIdx = instID / 32;
            uint bitIdx = instID & 31;
            uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
            if (selVal & (1 << bitIdx))
            {
                o.col.a = -1;				
            }
        }
    }
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

// ---- NEW: palette helper (fallback to hash if no palette set) ----
float3 LabelToColor(uint label)
{
    if (_LabelPaletteCount > 1)
    {
        float u = (label % _LabelPaletteCount) / max(1.0, (float)(_LabelPaletteCount - 1));
        return tex2D(_LabelPaletteTex, float2(u, 0.5)).rgb;
    }
    return HashLabelToColor(label);
}

half4 frag (v2f i) : SV_Target
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);

    if (_RenderMode == 5) // ColorByLabel
    {
        int label = _SplatLabels[i.id];
        i.col.rgb = (label >= 0) ? LabelToColor(label) : float3(0.5, 0.5, 0.5);
    }
    else if (i.col.a >= 0)
    {
        alpha = saturate(alpha * i.col.a);
    }
    else
    {
        // "selected" splat: magenta outline, increase opacity, magenta tint
        half3 selectedColor = half3(1,0,1);
        if (alpha > 7.0/255.0)
        {
            if (alpha < 10.0/255.0)
            {
                alpha = 1;
                i.col.rgb = selectedColor;
            }
            alpha = saturate(alpha + 0.3);
        }
        i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
    }

    if (alpha < 1.0/255.0)
        discard;

    return half4(i.col.rgb * alpha, alpha);
}
ENDCG
        }
    }
}
