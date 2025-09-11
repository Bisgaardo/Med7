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
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // === EXACTLY matches C# TriSplat (108 bytes) ===
            struct TriSplat {
                float3 v0; float3 v1; float3 v2;        // 36
                float2 uv0; float2 uv1; float2 uv2;     // 24
                float4 col0; float4 col1; float4 col2;  // 48
            };
            StructuredBuffer<TriSplat> _TriSplatData;

            // Optional texture path (off by default)
            sampler2D _BaseMap;
            float _HasTex;       // 0 = solid color path
            float _AlphaCutoff;  // e.g. 0.3

            struct VSOut {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR0;
            };

            VSOut vert(uint id : SV_VertexID, uint inst : SV_InstanceID)
            {
                TriSplat s = _TriSplatData[inst];

                float3 p   = (id==0)? s.v0 : (id==1)? s.v1 : s.v2;
                float2 uv  = (id==0)? s.uv0: (id==1)? s.uv1: s.uv2;
                float4 col = (id==0)? s.col0:(id==1)? s.col1: s.col2;

                VSOut o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1));
                o.uv  = uv;
                o.col = col;
                return o;
            }

            float4 frag(VSOut i) : SV_Target
            {
                // solid color path (default)
                if (_HasTex < 0.5) return float4(i.col.rgb, 1);

                // textured cutout path
                float4 tex = tex2D(_BaseMap, i.uv);
                float cutoff = (_AlphaCutoff > 0) ? _AlphaCutoff : 0.3;
                if (tex.a < cutoff) discard;
                return tex * float4(i.col.rgb, 1);
            }
            ENDHLSL
        }
    }
}
