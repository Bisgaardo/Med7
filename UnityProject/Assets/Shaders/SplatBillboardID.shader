Shader "Custom/SplatBillboardID"
{
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            struct Splat { float3 pos; float4 col; float3 cov0; float3 cov1; float3 cov2; };
            StructuredBuffer<Splat> _SplatData;
            StructuredBuffer<uint>  _VisibleIndices;

            float4x4 _VP;
            int      _DebugForceRed;

            struct Attributes { uint vertexID:SV_VertexID; uint instanceID:SV_InstanceID; };
            struct Varyings   { float4 pos:SV_POSITION; uint id:TEXCOORD0; };

            float2 CornerFromTriID(uint vID)
            {
                uint m = vID % 6;
                if (m == 0) return float2(-1,-1);
                if (m == 1) return float2(+1,-1);
                if (m == 2) return float2(+1,+1);
                if (m == 3) return float2(-1,-1);
                if (m == 4) return float2(+1,+1);
                return           float2(-1,+1);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                uint idx = _VisibleIndices[v.instanceID];
                Splat s  = _SplatData[idx];

                float4 clip = mul(_VP, float4(s.pos, 1));
                float dist  = max(clip.w, 1e-3);

                // match color pass footprint
                float2 radius = float2(s.cov0.x, s.cov1.y) / dist;

                float2 base = CornerFromTriID(v.vertexID);
                clip.xy += base * radius * clip.w;

                o.pos = clip;
                o.id  = idx;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                if (_DebugForceRed == 1) return float4(1,0,0,1);

                uint id = i.id;
                float r = (id      & 255) / 255.0;
                float g = ((id>>8) & 255) / 255.0;
                float b = ((id>>16)& 255) / 255.0;
                return float4(r,g,b,1);
            }
            ENDHLSL
        }
    }
}
