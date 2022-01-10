Shader "NKLI/StereoAppend"
{
    Properties{
        _MainTex("", 2D) = "white" {}
    }
        CGINCLUDE
#include "UnityCG.cginc"

        sampler2D _MainTex;

    struct v2f
    {
        float4 vertex : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata_base v)
    {
        v2f o;
        o.vertex = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    ENDCG
        SubShader{
            ZTest Always Cull Off ZWrite Off

            //Blit Left
            Pass
            {
                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag

                fixed4 frag(v2f i) : SV_Target
                {
                    clip(0.5 - i.uv.x);
                    fixed4 c = tex2D(_MainTex, float2(i.uv.x * 2, i.uv.y));
                    return c;
                }
                ENDCG
            }

        //Blit Right
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            fixed4 frag(v2f i) : SV_Target
            {
                clip(i.uv.x - 0.5);
                fixed4 c = tex2D(_MainTex, float2(i.uv.x * 2- 1, i.uv.y));
                return c;
            }
            ENDCG
        }

    }
        FallBack "Diffuse"
}