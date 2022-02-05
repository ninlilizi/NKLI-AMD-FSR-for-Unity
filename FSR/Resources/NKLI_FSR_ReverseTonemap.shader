Shader "Hidden/NKLI_FSR_ReverseTonemap"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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

            // We want these defined early
			#define A_GPU 1
			#define A_HLSL 1
            #define A_HALF 1

			#include "NKLI_FSR/ffx_a.cginc"
			#include "NKLI_FSR/ffx_fsr1.cginc"

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
                return o;
            }

            sampler2D _MainTex;

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample
                float4 col = tex2D(_MainTex, i.uv);

                // Convert gamma 2.0 to linear
                col.rgb = pow(col.rgb, 2.0);

                // Reverse tone-map
				FsrSrtmInvH(col.rgb);

                // Write
                return col;
            }
            ENDCG
    }
    }
}
