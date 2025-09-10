Shader "Custom/SplatSurface"
{
    Properties{
        _DepthBias("Depth Bias (m)", Float) = 0.001
        _SelfBias ("Self Overlap Bias (m)", Float) = 0.0004
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off
        ZWrite On
        ZTest LEqual
        Blend One OneMinusSrcAlpha     // premultiplied alpha

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
                float3 cov0; // world tangent  * sigmaT
                float3 cov1; // world bitangent * sigmaT
                float3 cov2; // world normal   * sigmaN
            };
            StructuredBuffer<Splat> _SplatData;

            // selection / focus
            StructuredBuffer<uint> _IDMask;
            int   _IDMaskCount;
            int   _ShowIDMask;
            float4 _SelectTint;
            float  _SelectBoost;
            float  _SelectScale;
            float  _DimOthers;

            float  _GlobalScale;
            float  _DepthBias;
            float  _SelfBias;

            struct VSIN  { uint vid : SV_VertexID; uint iid : SV_InstanceID; };
            struct VSOUT {
                float4 pos : SV_Position;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR0;
                nointerpolation uint idx : TEXCOORD1;
            };

            float2 Corner(uint v)
            {
                if      (v == 0) return float2(-1,-1);
                else if (v == 1) return float2(-1, 1);
                else if (v == 2) return float2( 1, 1);
                else if (v == 3) return float2(-1,-1);
                else if (v == 4) return float2( 1, 1);
                else             return float2( 1,-1);
            }

            // cheap per-splat hash → [0,1)
            float Hash01(uint n) { return frac(sin((n+1u)*12.9898)*43758.5453); }

            VSOUT vert(VSIN v)
            {
                VSOUT o;
                uint idx = _VisibleIndices[v.iid];
                Splat s  = _SplatData[idx];

                float sc = max(0.01, _GlobalScale);
                float3 ax = s.cov0 * sc;             // surface-aligned axes
                float3 ay = s.cov1 * sc;

                float3 N  = s.cov2;
                float  nL = max(1e-6, length(N));
                N /= nL;

                float  bias   = _DepthBias + Hash01(idx) * _SelfBias; // mesh lift + micro deconflict
                float3 center = s.pos + N * bias;

                float2 c  = Corner(v.vid);
                float3 wp = center + ax * c.x + ay * c.y;

                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1));
                o.uv  = c * 0.5 + 0.5;
                o.col = s.col;
                o.idx = idx;
                return o;
            }

            float4 frag(VSOUT i) : SV_Target
            {
                // analytic AA edge
                float2 d = i.uv * 2.0 - 1.0;
                float  r = length(d);
                float w    = fwidth(r) * 1.6;
                float mask = 1.0 - smoothstep(1.0 - w, 1.0, r);
                float alpha = exp(-2.0 * r * r) * mask;   // gentler falloff

            #ifdef _IDPASS
                uint id = i.idx + 1u;
                uint r8 = ( id       ) & 255u;
                uint g8 = ((id >> 8) ) & 255u;
                uint b8 = ((id >> 16)) & 255u;
                return float4(r8/255.0, g8/255.0, b8/255.0, 1.0);
            #else
                float4 col = i.col;

                if (_ShowIDMask != 0 && _IDMaskCount > 0)
                {
                    bool selected = false;
                    [loop] for (int k = 0; k < _IDMaskCount; k++)
                        { if (_IDMask[k] == i.idx) { selected = true; break; } }

                    if (!selected) col.rgb *= _DimOthers;
                    else { col.rgb = saturate(lerp(col.rgb, _SelectTint.rgb, 0.35));
                           alpha   = saturate(alpha * _SelectBoost); }
                }

                // premultiply for Blend One, OneMinusSrcAlpha
                col.rgb *= alpha;
                col.a    = alpha;

                // optional early out
                if (col.a <= 1e-4) discard;

                return col;
            #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
