// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Pick IDs Uint"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #pragma use_dxc
            #pragma multi_compile_instancing

            // Keep path consistent with your repo
            #include "Assets/UnityGaussianSplatting/package/Shaders/GaussianSplatting.hlsl"

            // 🔹 mirror main pass bindings
            StructuredBuffer<uint>          _OrderBuffer;     // SAME as main
            StructuredBuffer<SplatViewData> _SplatViewData;   // SAME as main
            StructuredBuffer<uint>          _SplatRawIds;     // stable raw ids

            float2 _PickRTSize; // set from C#

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 pos    : TEXCOORD0;
                uint   id     : TEXCOORD1;
            };

            v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
            {
                v2f o = (v2f)0;

                // 🔹 use the same indirection as the main pass
                instID = _OrderBuffer[instID];

                SplatViewData view = _SplatViewData[instID];
                float4 centerClipPos = view.pos;
                if (centerClipPos.w <= 0)
                {
                    o.vertex = asfloat(0x7fc00000); // discard
                    return o;
                }

                // quad corners in [-1,1]^2
                uint   idx     = vtxID;
                float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;

                // expand using PICK-RT size (not _ScreenParams)
                float2 deltaScreenPos =
                    (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _PickRTSize;

                o.vertex = centerClipPos;
                o.vertex.xy += deltaScreenPos * centerClipPos.w;
                o.pos = quadPos;

                // raw id (stable)
                o.id = _SplatRawIds[instID];

                FlipProjectionIfBackbuffer(o.vertex);
                return o;
            }

            // Write UINT id+1 into R32_UINT RT
            uint frag(v2f i) : SV_Target
            {
                return i.id + 1u;
            }
            ENDCG
        }
    }
}
