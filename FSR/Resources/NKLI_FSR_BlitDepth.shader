Shader "Hidden/NKLI_FSR_BlitDepth"
{
    Properties
    {

    }
        SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

#if UNITY_UV_STARTS_AT_TOP
                //o.uv.y = 1.0 - v.uv.y;
#endif

                return o;
            }

            sampler2D _CameraDepthTexture;

            float frag (v2f i) : SV_Target
            {
                return tex2D(_CameraDepthTexture, i.uv).r;
            }
            ENDCG
        }
    }
}
