Shader "Custom/SplatBillboard"
{
    Properties
    {
        _EdgeSigma ("Edge Sigma at Quad Edge", Float) = 3.0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" }
        Pass
        {
            ZWrite Off
            ZTest  LEqual
            Blend  One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            struct Splat
            {
                float3 pos; float4 col; float3 cov0; float3 cov1; float3 cov2;
            };

            StructuredBuffer<Splat> _SplatData;
            StructuredBuffer<uint>  _VisibleIndices;

            // Selection data (provided by C#)
            StructuredBuffer<uint> _IDMask;
            int   _IDMaskCount;
            int   _ShowIDMask;

            // Camera
            float4x4 _VP;

            // Selection tuning (provided by C# SetIDMask)
            float4 _SelectTint;   // color for selected
            float  _SelectBoost;  // alpha boost for selected
            float  _SelectScale;  // scale factor for selected splat radius
            float  _DimOthers;    // 1=no dim; <1 dims non-selected

            // Edge shaping
            float _EdgeSigma;     // how many sigmas at the quad edge (default 3)

            struct Attributes {
                uint vertexID   : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 col   : COLOR0;
                float  sel   : TEXCOORD1; // 1 if selected, 0 otherwise
            };

            // Map 6 triangle vertices to the 4 quad corners:
            // tri1: (0,1,2) -> (-1,-1), (+1,-1), (-1,+1)
            // tri2: (3,4,5) -> (+1,-1), (+1,+1), (-1,+1)
            float2 QuadCornerFromTri(uint vid)
            {
                uint cornerIndex = (vid <= 2) ? vid : (vid == 3 ? 1 : (vid == 4 ? 3 : 2));
                // corner map: 0:(-1,-1), 1:(+1,-1), 2:(-1,+1), 3:(+1,+1)
                float2 c = float2((cornerIndex & 1) ? 1 : -1, (cornerIndex & 2) ? 1 : -1);
                return c;
            }

            Varyings vert (Attributes v)
            {
                Varyings o;

                uint  idx = _VisibleIndices[v.instanceID];
                Splat s   = _SplatData[idx];

                float4 clip = mul(_VP, float4(s.pos, 1));
                float  dist = max(clip.w, 1e-3);

                // Simple radius from diagonal covariance (approx)
                float2 radius = float2(s.cov0.x, s.cov1.y) / dist;

                // Is this instance selected?
                bool isSel = false;
                if (_ShowIDMask == 1 && _IDMaskCount > 0)
                {
                    [loop] for (int k = 0; k < _IDMaskCount; k++)
                    {
                        if (_IDMask[k] == idx) { isSel = true; break; }
                    }
                }

                // Enlarge selected splats a bit (visual feedback)
                if (isSel) radius *= max(_SelectScale, 1.0);

                float2 base = QuadCornerFromTri(v.vertexID);   // proper 6-vert mapping

                // Offset clip-space XY by radius * clip.w (clip units)
                clip.xy += base * radius * clip.w;

                o.pos = clip;
                o.uv  = base;   // -1..1 at edges
                o.col = s.col;
                o.sel = isSel ? 1.0 : 0.0;
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                // Scale UV so that |uv|=1 corresponds to EdgeSigma sigmas
                float sigma = max(_EdgeSigma, 0.1);
                float r2 = dot(i.uv, i.uv) * (sigma * sigma);
                // Gaussian alpha with edge near ~exp(-0.5 * sigma^2)
                float a = exp(-0.5 * r2);

                float4 col = i.col * a; // premultiplied RGBA

                bool hasSel = (_ShowIDMask == 1 && _IDMaskCount > 0);

                if (hasSel)
                {
                    if (i.sel > 0.5) // selected
                    {
                        // Boost and tint selected
                        float  alpha   = saturate(col.a * max(_SelectBoost, 1.0));
                        float3 baseRGB = (alpha > 1e-4) ? (col.rgb / alpha) : col.rgb;
                        float3 tinted  = lerp(baseRGB, _SelectTint.rgb, 0.85);
                        col = float4(tinted * alpha, alpha);
                    }
                    else
                    {
                        // Dim everything else (but keep it visible)
                        col.rgb *= _DimOthers;
                        col.a   *= _DimOthers;
                    }
                }

                return col;
            }
            ENDHLSL
        }
    }
}
