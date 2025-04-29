Shader "WaveParticles/Water_2"
{
    Properties
    {
        _HeightMap("Height Map", 2D) = "black" {}
        _HeightScale("Height Scale", Float) = 1.0
        _OceanColorShallow("Ocean Shallow Color", Color) = (0.2,0.5,0.8,1)
        _OceanColorDeep("Ocean Deep Color", Color) = (0.0,0.1,0.4,1)
        _SpecularColor("Specular Color", Color) = (1,1,1,1)
        _Glossiness("Glossiness", Range(1,256)) = 32
        _FresnelPower("Fresnel Power", Range(0,5)) = 1.0

            // 额外气泡效果开关
            _BubbleThreshold("Bubble Threshold", Range(0,1)) = 0.5
            _BubbleScale("Bubble Scale", Range(0,5)) = 1.0
    }

        SubShader
        {
            Tags { "RenderType" = "Opaque" }
            LOD 300

            Pass
            {
                CGPROGRAM
                #pragma target 4.5
                #pragma vertex vert
                #pragma fragment frag
                #include "UnityCG.cginc"

                // 从 C# 脚本上传过来的粒子位置 Buffer
                RWStructuredBuffer<float3> _ParticleBuffer;
                RWStructuredBuffer<float3> _ParticleBuffer;

                sampler2D _HeightMap;
                float    _HeightScale;
                fixed4   _OceanColorShallow;
                fixed4   _OceanColorDeep;
                fixed4   _SpecularColor;
                float    _Glossiness;
                float    _FresnelPower;
                float    _BubbleThreshold;
                float    _BubbleScale;

                struct appdata
                {
                    float4 vertex : POSITION;
                    float2 uv     : TEXCOORD0;
                };

                struct v2f
                {
                    float4 pos         : SV_POSITION;
                    float2 uv          : TEXCOORD0;
                    float3 worldPos    : TEXCOORD1;
                    float3 worldNormal : TEXCOORD2;
                    float3 viewDir     : TEXCOORD3;
                };

                v2f vert(appdata v)
                {
                    v2f o;

                    // 1. 读取高度图
                    float h = tex2Dlod(_HeightMap, float4(v.uv,0,0)).r * _HeightScale;
                    float3 worldPos = v.vertex.xyz;
                    worldPos.y += h;

                    // 2. 读取第一个粒子示例位置（或循环叠加更多）
                    float3 p0 = _ParticleBuffer[0];
                    // 你可以用 p0 来影响 vertex 进一步偏移或计算涡度，示例里只做简单叠加
                    // worldPos += (_BubbleScale * saturate(1 - distance(worldPos.xz, p0.xz)/_BubbleThreshold)) * float3(0,1,0);

                    // 3. 计算法线：从高度图近邻采样
                    float eps = 1.0 / 256;  // 与你的高度图分辨率一致
                    float hl = tex2D(_HeightMap, v.uv + float2(-eps,0)).r * _HeightScale;
                    float hr = tex2D(_HeightMap, v.uv + float2(+eps,0)).r * _HeightScale;
                    float hd = tex2D(_HeightMap, v.uv + float2(0,-eps)).r * _HeightScale;
                    float hu = tex2D(_HeightMap, v.uv + float2(0,+eps)).r * _HeightScale;
                    float3 n = normalize(float3(hl - hr, 2 * eps * _HeightScale, hu - hd));

                    float4 worldPos4 = float4(worldPos,1);
                    o.pos = UnityObjectToClipPos(worldPos4);
                    o.uv = v.uv;
                    o.worldPos = mul(unity_ObjectToWorld, worldPos4).xyz;
                    o.worldNormal = normalize(mul((float3x3)unity_ObjectToWorld, n));
                    o.viewDir = normalize(_WorldSpaceCameraPos.xyz - o.worldPos);
                    return o;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    // 淡入深色海水
                    float facing = saturate(dot(i.worldNormal, i.viewDir));
                    fixed4 color = lerp(_OceanColorShallow, _OceanColorDeep, facing);

                    // Blinn-Phong 漫反射 + 镜面
                    fixed3 L = normalize(_WorldSpaceLightPos0.xyz);
                    float NdotL = saturate(dot(i.worldNormal, L));
                    fixed3 diff = color.rgb * _LightColor0.rgb * NdotL;

                    fixed3 H = normalize(L + i.viewDir);
                    float spec = pow(saturate(dot(i.worldNormal, H)), _Glossiness);
                    fixed3 specCol = _SpecularColor.rgb * spec * _LightColor0.rgb;

                    // 菲涅尔
                    float fresnel = pow(1 - saturate(dot(i.worldNormal, i.viewDir)), _FresnelPower);
                    fixed3 sky = UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, reflect(-i.viewDir, i.worldNormal)).rgb;
                    fixed3 refl = lerp(diff + specCol, sky, fresnel);

                    return fixed4(refl, 1);
                }

                ENDCG
            }
        }
            FallBack "Diffuse"
}
