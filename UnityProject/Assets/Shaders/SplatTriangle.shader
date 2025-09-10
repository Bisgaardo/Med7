Shader "Custom/SplatTriangle"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        Blend One OneMinusSrcAlpha
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct TriSplat {
                float3 v0;
                float3 v1;
                float3 v2;
                float4 c0;
                float4 c1;
                float4 c2;
            };
            StructuredBuffer<TriSplat> _TriSplatData;

            struct VSIN { uint id : SV_VertexID; uint iid : SV_InstanceID; };
            struct VSOUT { float4 pos : SV_Position; float4 col : COLOR0; float2 bc : TEXCOORD0; };

            VSOUT vert(VSIN v)
            {
                TriSplat s = _TriSplatData[v.iid];
                float3 p;
                float4 c;
                if (v.id == 0) { p = s.v0; c = s.c0; }
                else if (v.id == 1) { p = s.v1; c = s.c1; }
                else { p = s.v2; c = s.c2; }
                VSOUT o;
                o.pos = UnityObjectToClipPos(float4(p,1));
                o.col = c;
                return o;
            }

            float4 frag(VSOUT i) : SV_Target
            {
                // For now just alpha = 1.0 → can add Gaussian window later
                return i.col;
            }
            ENDHLSL
        }
    }
}
