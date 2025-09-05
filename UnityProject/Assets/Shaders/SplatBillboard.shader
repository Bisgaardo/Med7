Shader "Hidden/SplatBillboard"
{
    SubShader
    {
        Tags {"Queue"="Transparent"}
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ _IDPASS

            struct Splat
            {
                float3 pos; float4 col; float3 cov0; float3 cov1; float3 cov2;
            };
            StructuredBuffer<Splat> _SplatData;
            StructuredBuffer<uint> _VisibleIndices;
            StructuredBuffer<uint> _IDMask;
            int _IDMaskCount;
            float4x4 _VP;
            float2 _Screen;
            int _ShowIDMask;

            struct Attributes { uint vertexID:SV_VertexID; uint instanceID:SV_InstanceID; };
            struct Varyings  { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float4 col:COLOR0; uint id:TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                uint idx = _VisibleIndices[v.instanceID];
                Splat s = _SplatData[idx];
                float4 clip = mul(_VP, float4(s.pos,1));
                float dist = max(clip.w, 1e-3);
                float2 radius = float2(s.cov0.x, s.cov1.y) / dist; // world->ndc approx
                float2 base = float2((v.vertexID & 1)?1:-1, (v.vertexID & 2)?1:-1);
                clip.xy += base * radius * clip.w;
                o.pos = clip;
                o.uv = base;
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
                float a = exp(-0.5 * dot(i.uv, i.uv));
                float4 col = i.col * a; // premultiplied
                if (_ShowIDMask == 1 && _IDMaskCount > 0)
                {
                    bool hit = false;
                    [loop] for (int k=0;k<_IDMaskCount;k++) if (_IDMask[k]==i.id) {hit=true; break;}
                    if (hit) col.rgb = lerp(col.rgb, float3(1,1,0), 0.5); // yellow tint
                }
                return col;
            #endif
            }
            ENDHLSL
        }
    }
}
