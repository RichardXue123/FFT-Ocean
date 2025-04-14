// Upgrade NOTE: upgraded instancing buffer 'Props' to new syntax.

Shader "Custom/WaveParticle"
{
    Properties
    {
        _Color("Color", Color) = (0.3,0.6,1.0,1.0)
    }

        SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
            };

            fixed4 _Color;

            UNITY_INSTANCING_BUFFER_START(Props)
                // 你可以在这里定义每实例变量，比如 _Height 之类
                UNITY_INSTANCING_BUFFER_END(Props)

                v2f vert(appdata v)
                {
                    v2f o;
                    UNITY_SETUP_INSTANCE_ID(v);
                    o.vertex = UnityObjectToClipPos(v.vertex);
                    o.color = _Color;
                    return o;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    return i.color;
                }
                ENDCG
            }
    }
}
