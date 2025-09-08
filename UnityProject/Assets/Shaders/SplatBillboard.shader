Shader "Custom/SplatBillboard"
{
    SubShader
    {
        Tags { "Queue"="Transparent" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile __ _IDPASS

            struct Splat
            {
                float3 pos;
                float4 col;
                float3 cov0;
                float3 cov1;
                float3 cov2;
            };

            StructuredBuffer<Splat> _SplatData;
            StructuredBuffer<uint>  _VisibleIndices;
            StructuredBuffer<uint>  _IDMask;
            int _IDMaskCount;

            float4x4 _VP;
            float2   _Screen;
            int      _ShowIDMask;

            struct Attributes { uint vertexID : SV_VertexID; uint instanceID : SV_InstanceID; };
            struct Varyings   { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; uint id : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;

                // Which splat instance are we drawing?
                uint idx = _VisibleIndices[v.instanceID];
                Splat s  = _SplatData[idx];

                // Base clip position of the splat center
                float4 clip = mul(_VP, float4(s.pos, 1));
                float  dist = max(clip.w, 1e-3);

                // Very simple “radius” from diagonal covariance terms, scaled by distance (NDC approx)
                float2 radius = float2(s.cov0.x, s.cov1.y) / dist;

                // We draw a quad as TWO TRIANGLES (6 vertices). Map vertexID 0..5 to the 4 corners:
                // corners (in [-1,1] billboard space): 0=(-1,-1), 1=(1,-1), 2=(1,1), 3=(-1,1)
                // triangles: (0,1,2) and (0,2,3)
                uint vid = v.vertexID % 6;
                float2 base;
                if      (vid == 0) base = float2(-1, -1);
                else if (vid == 1) base = float2( 1, -1);
                else if (vid == 2) base = float2( 1,  1);
                else if (vid == 3) base = float2(-1, -1);
                else if (vid == 4) base = float2( 1,  1);
                else               base = float2(-1,  1);

                // Expand quad in clip space (offset in NDC scaled by w)
                clip.xy += base * radius * clip.w;

                o.pos = clip;
                o.uv  = base;       // used for Gaussian falloff
                o.col = s.col;
                o.id  = idx;
                return o;
            }

float4 frag(Varyings i) : SV_Target
{
#ifdef _IDPASS
    uint id = i.id;
    return float4((id & 255)/255.0, ((id>>8)&255)/255.0, ((id>>16)&255)/255.0, 1);
#else
    // Kill pixels outside the unit circle → no square corners
    float r2 = dot(i.uv, i.uv);
    if (r2 > 1.0) discard;

    // Gaussian falloff (tweak k for softness)
    const float k = 0.6;        // 0.3 = softer, 1.0 = sharper
    float a = exp(-k * r2);

    float4 col = i.col * a;     // premultiplied alpha
    if (_ShowIDMask == 1 && _IDMaskCount > 0)
    {
        bool hit = false;
        [loop] for (int k=0;k<_IDMaskCount;k++) if (_IDMask[k]==i.id) {hit=true; break;}
        if (hit) col.rgb = lerp(col.rgb, float3(1,1,0), 0.5);
    }
    return col;
#endif
}

            ENDHLSL
        }
    }
}
