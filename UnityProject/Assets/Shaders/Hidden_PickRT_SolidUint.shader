Shader "Hidden/PickRT_SolidUint"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag
            #pragma use_dxc

            struct v2f { float4 pos:SV_POSITION; };
            v2f vert(uint id:SV_VertexID){
                v2f o; float2 uv = float2((id<<1)&2, id&2);
                o.pos = float4(uv*2-1,0,1); return o;
            }
            uint frag(v2f i):SV_Target { return 123u; }
            ENDCG
        }
    }
}
