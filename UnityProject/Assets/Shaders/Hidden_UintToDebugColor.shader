Shader "Hidden/UintToDebugColor"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off Blend Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma use_dxc

            // Blit sets _MainTex; we declare it as UINT so we can Load()
            Texture2D<uint> _MainTex;
            float4 _TargetSize; // (width, height, 0, 0)

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert (uint id : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.uv  = uv;
                o.pos = float4(uv * 2 - 1, 0, 1);
                return o;
            }

            float4 hashColor(uint v)
            {
                // simple hash to get a stable debug color per id
                uint h = v * 1664525u + 1013904223u;
                float r = ((h >>  0) & 255) / 255.0;
                float g = ((h >>  8) & 255) / 255.0;
                float b = ((h >> 16) & 255) / 255.0;
                return float4(r,g,b,1);
            }

            float4 frag (v2f i) : SV_Target
            {
                int2 pix = int2(i.uv * _TargetSize.xy + 0.5);
                uint v = _MainTex.Load(int3(pix, 0));
                if (v == 0u) return float4(0,0,0,1);     // background black
                return hashColor(v);                     // colored by rawId+1
            }
            ENDCG
        }
    }
}
