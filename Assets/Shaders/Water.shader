Shader "WaveParticles/Water"
{
    Properties
    {
        _HeightMap("Height Map", 2D) = "black" {}
        _HeightScale("Height Scale", Float) = 1.0
        _Color("Color", Color) = (0.2,0.4,1.0,1)
    }
        SubShader
        {
            Tags { "RenderType" = "Opaque" }
            Pass
            {
                CGPROGRAM
                #pragma vertex vert
                #pragma fragment frag
                #include "UnityCG.cginc"

                sampler2D _HeightMap;
                float _HeightScale;
                fixed4 _Color;

                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv : TEXCOORD0;
                };

                struct v2f
                {
                    float4 vertex : SV_POSITION;
                    float2 uv : TEXCOORD0;
                };

                v2f vert(appdata v)
                {
                    v2f o;
                    float height = tex2Dlod(_HeightMap, float4(v.uv, 0, 0)).r;
                    v.vertex.y += height * _HeightScale;
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    o.uv = v.uv;
                    return o;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    return _Color;
                }
                ENDCG
            }
        }
}
