Shader "Gaussian Splatting/Pick Splats"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma use_dxc

            #include "GaussianSplatting.hlsl"

            StructuredBuffer<uint> _OrderBuffer;
            StructuredBuffer<SplatViewData> _SplatViewData;

            struct v2f {
                float4 vertex : SV_POSITION;
                uint splatId  : TEXCOORD0;
                float2 pos    : TEXCOORD1;
            };

            v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
            {
                v2f o = (v2f)0;

                instID = _OrderBuffer[instID];
                SplatViewData view = _SplatViewData[instID];

                float4 centerClipPos = view.pos;
                if (centerClipPos.w <= 0) {
                    o.vertex = asfloat(0x7fc00000); // NaN discard
                } else {
                    uint idx = vtxID;
                    float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
                    quadPos *= 2;

                    o.pos = quadPos;

                    float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
                    o.vertex = centerClipPos;
                    o.vertex.xy += deltaScreenPos * centerClipPos.w;

                    o.splatId = instID; // real splat ID
                }
                FlipProjectionIfBackbuffer(o.vertex);
                return o;
            }

            uint frag(v2f i) : SV_Target {
                return i.splatId; // write splatId into R32Uint buffer
            }
            ENDCG
        }
    }
}
