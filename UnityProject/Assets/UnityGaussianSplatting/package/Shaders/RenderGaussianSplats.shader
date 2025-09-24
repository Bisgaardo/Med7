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
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #pragma require  compute
            #pragma use_dxc
            #pragma multi_compile_instancing

            // Use the exact project include
            #include "Assets/UnityGaussianSplatting/package/Shaders/GaussianSplatting.hlsl"

            // Buffers/types already defined by your pipeline:
            StructuredBuffer<uint>          _OrderBuffer;
            StructuredBuffer<SplatViewData> _SplatViewData;
            ByteAddressBuffer               _SplatSelectedBits;
            uint                            _SplatBitsValid;

            // Segmentation (what we add)
            StructuredBuffer<int>  _SplatSegments;  // -1 = unassigned, >=0 = painted
            int                    _SegmentsBound;  // set to 1 by C#

            // Stable raw IDs (provided by C# and pick pass)
            StructuredBuffer<uint> _SplatRawIds;

            struct v2f
            {
                half4 col     : COLOR0;
                float2 pos    : TEXCOORD0;
                float4 vertex : SV_POSITION;
                uint   splatId: TEXCOORD1;
            };

            v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
            {
                v2f o = (v2f)0;

                instID = _OrderBuffer[instID];

                SplatViewData view = _SplatViewData[instID];
                float4 centerClipPos = view.pos;

                if (centerClipPos.w <= 0)
                {
                    o.vertex = asfloat(0x7fc00000);
                    return o;
                }

                // unpack color exactly like original
                o.col.r = f16tof32(view.color.x >> 16);
                o.col.g = f16tof32(view.color.x);
                o.col.b = f16tof32(view.color.y >> 16);
                o.col.a = f16tof32(view.color.y);

                // ellipse quad
                uint   idx     = vtxID;
                float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
                quadPos *= 2;
                o.pos = quadPos;

                float2 deltaScreenPos =
                    (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
                o.vertex = centerClipPos;
                o.vertex.xy += deltaScreenPos * centerClipPos.w;

                // selection outline (unchanged)
                if (_SplatBitsValid)
                {
                    uint wordIdx = instID / 32;
                    uint bitIdx  = instID & 31;
                    uint selVal  = _SplatSelectedBits.Load(wordIdx * 4);
                    if (selVal & (1u << bitIdx))
                        o.col.a = -1;
                }

                // stable id for segmentation lookup
                o.splatId = _SplatRawIds[instID];

                FlipProjectionIfBackbuffer(o.vertex);
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float power = -dot(i.pos, i.pos);
                half  alpha = exp(power);
                if (alpha < 1.0/255.0) discard;

                if (i.col.a >= 0)
                {
                    alpha = saturate(alpha * i.col.a);
                }
                else
                {
                    half3 sel = half3(1,0,1);
                    if (alpha > 7.0/255.0)
                    {
                        if (alpha < 10.0/255.0) { alpha = 1; i.col.rgb = sel; }
                        alpha = saturate(alpha + 0.3);
                    }
                    i.col.rgb = lerp(i.col.rgb, sel, 0.5);
                }

                half3 finalColor = i.col.rgb;

                // Apply segmentation: paint pure red if marked
                if (_SegmentsBound != 0)
                {
                    int segId = _SplatSegments[i.splatId];
                    if (segId >= 0)
                    {
                        finalColor = float3(1, 0, 0); // red
                    }
                }

                return half4(finalColor * alpha, alpha);
            }
            ENDCG
        }
    }
}
