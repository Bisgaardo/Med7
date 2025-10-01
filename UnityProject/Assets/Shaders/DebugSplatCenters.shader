Shader "Debug/SplatCenters"
{
    Properties { }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            StructuredBuffer<uint> _VisibleIndices;

            struct Splat {
                float3 pos;
                float4 col;
                float3 cov0;
                float3 cov1;
                float3 cov2;
            };
            StructuredBuffer<Splat> _SplatData;

            float  _GlobalScale; // not used; keep interface identical

            struct VSIN { uint vid : SV_VertexID; uint iid : SV_InstanceID; };
            struct VSOUT {
                float4 pos : SV_Position;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR0;
            };

            // Make a tiny 6-vertex quad in **screen space** around the center.
            // This guarantees you see the center without relying on cov0/cov1.
            VSOUT vert(VSIN v)
            {
                VSOUT o;
                uint idx = _VisibleIndices[v.iid];
                Splat s  = _SplatData[idx];

                // center in clip space
                float4 clipC = mul(UNITY_MATRIX_VP, float4(s.pos, 1));
                float2 ndcC  = clipC.xy / max(1e-6, clipC.w);

                // small half size in NDC (≈ 2 pixels). Adjust if needed.
                float2 halfNDC = float2(2.0 / _ScreenParams.x, 2.0 / _ScreenParams.y);

                float2 corner;
                if      (v.vid == 0) corner = float2(-1,-1);
                else if (v.vid == 1) corner = float2(-1, 1);
                else if (v.vid == 2) corner = float2( 1, 1);
                else if (v.vid == 3) corner = float2(-1,-1);
                else if (v.vid == 4) corner = float2( 1, 1);
                else                 corner = float2( 1,-1);

                float2 ndc = ndcC + corner * halfNDC;

                // back to clip
                o.pos = float4(ndc * clipC.w, clipC.z, clipC.w);
                o.uv  = corner * 0.5 + 0.5;
                o.col = s.col; o.col.a = 1.0;
                return o;
            }

            float4 frag(VSOUT i) : SV_Target
            {
                float2 d = i.uv * 2 - 1;
                if (dot(d,d) > 1) discard;
                return float4(1,1,1,1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
