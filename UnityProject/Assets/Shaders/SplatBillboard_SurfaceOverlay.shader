Shader "Custom/SplatBillboard_SurfaceOverlay"
{
    Properties { }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        ZTest Always                   // <- ignore scene depth (overlay)
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile _ _IDPASS
            #include "UnityCG.cginc"

            StructuredBuffer<uint> _VisibleIndices;

            struct Splat {
                float3 pos;
                float4 col;
                float3 cov0; // world tangent * sigmaT
                float3 cov1; // world bitangent * sigmaT
                float3 cov2; // world normal  * sigmaN (unused here)
            };
            StructuredBuffer<Splat> _SplatData;

            StructuredBuffer<uint> _IDMask;
            int   _IDMaskCount;
            int   _ShowIDMask;

            float4x4 _VP;
            float2   _Screen;

            float4 _SelectTint;
            float  _SelectBoost;
            float  _SelectScale;
            float  _DimOthers;

            float  _GlobalScale;

            struct VSIN { uint vid : SV_VertexID; uint iid : SV_InstanceID; };
            struct VSOUT {
                float4 pos : SV_Position;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR0;
                nointerpolation float splatIndex : TEXCOORD1;
            };

            bool InMask(uint id)
            {
                [loop] for (int i = 0; i < _IDMaskCount; i++) if (_IDMask[i] == id) return true;
                return false;
            }

            float2 Corner(uint vid)
            {
                if      (vid == 0) return float2(-1,-1);
                else if (vid == 1) return float2(-1, 1);
                else if (vid == 2) return float2( 1, 1);
                else if (vid == 3) return float2(-1,-1);
                else if (vid == 4) return float2( 1, 1);
                else               return float2( 1,-1);
            }

            VSOUT vert(VSIN v)
            {
                VSOUT o;
                uint idx = _VisibleIndices[v.iid];
                Splat s  = _SplatData[idx];

                float scale = max(0.01, _GlobalScale);
                float3 axisX = s.cov0 * scale; // surface-aligned (no camera)
                float3 axisY = s.cov1 * scale;

                float2 c = Corner(v.vid);
                float3 wp = s.pos + axisX * c.x + axisY * c.y;

                o.pos = mul(_VP, float4(wp, 1));
                o.uv  = c * 0.5 + 0.5;
                o.col = s.col;
                o.splatIndex = (float)idx;
                return o;
            }

            float4 frag(VSOUT i) : SV_Target
            {
                float2 d  = i.uv * 2 - 1;
                float  r2 = dot(d, d);
                if (r2 > 1) discard;

                float alpha = exp(-2.5 * r2);

            #ifdef _IDPASS
                uint id = (uint)round(i.splatIndex) + 1u;
                uint r = (id      ) & 255u;
                uint g = (id >> 8 ) & 255u;
                uint b = (id >> 16) & 255u;
                return float4(r/255.0, g/255.0, b/255.0, 1.0);
            #else
                float4 col = i.col;

                if (_ShowIDMask != 0 && _IDMaskCount > 0)
                {
                    uint idx = (uint)round(i.splatIndex);
                    bool selected = InMask(idx);
                    if (!selected) col.rgb *= _DimOthers;
                    else {
                        col.rgb = saturate(lerp(col.rgb, _SelectTint.rgb, 0.35));
                        alpha   = saturate(alpha * _SelectBoost);
                    }
                }

                col.a *= alpha;
                return col;
            #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
