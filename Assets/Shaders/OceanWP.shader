Shader "Ocean/OceanWP"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _SSSColor("SSS Color", Color) = (1,1,1,1)
        _SSSStrength("SSSStrength", Range(0,1)) = 0.2
        _SSSScale("SSS Scale", Range(0.1,50)) = 4.0
        _SSSBase("SSS Base", Range(-5,1)) = 0
        _LOD_scale("LOD_scale", Range(1,10)) = 0
        _MaxGloss("Max Gloss", Range(0,1)) = 0
        _Roughness("Distant Roughness", Range(0,1)) = 0
        _RoughnessScale("Roughness Scale", Range(0, 0.01)) = 0.1
        _FoamColor("Foam Color", Color) = (1,1,1,1)
        _FoamTexture("Foam Texture", 2D) = "grey" {}
        _FoamBiasLOD0("Foam Bias LOD0", Range(0,7)) = 1
        _FoamBiasLOD1("Foam Bias LOD1", Range(0,7)) = 1
        _FoamBiasLOD2("Foam Bias LOD2", Range(0,7)) = 1
        _FoamScale("Foam Scale", Range(0,20)) = 1 // 原本是1 注释掉泡沫效果
        _ContactFoam("Contact Foam", Range(0,1)) = 1 // 原本是1 注释掉泡沫效果
        _ParticleHeightMap("Wave Particle Height Map", 2D) = "black" {} //加入新的波粒子高度图

        [Header(Cascade 0)]
        [HideInInspector]_Displacement_c0("Displacement C0", 2D) = "black" {}
        [HideInInspector]_Derivatives_c0("Derivatives C0", 2D) = "black" {}
        [HideInInspector]_Turbulence_c0("Turbulence C0", 2D) = "white" {}
    }
        SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma multi_compile _ MID CLOSE ONLY_CLOSE
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 4.0


        struct Input
        {
            float2 worldUV;
            float4 lodScales;
            float3 viewVector;
            float3 worldNormal;
            float4 screenPos;
            INTERNAL_DATA
        };

        sampler2D _Displacement_c0;
        sampler2D _Derivatives_c0;
        sampler2D _Turbulence_c0;

        //波粒子高度图参数
        sampler2D _ParticleHeightMap;
        float _ParticleHeightScale;

        float LengthScale0;
        float LengthScale1;
        float LengthScale2;
        float _LOD_scale;
        float _SSSBase;
        float _SSSScale;

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float3 worldPos = mul(unity_ObjectToWorld, v.vertex);
            float4 worldUV = float4(worldPos.xz, 0, 0);
            o.worldUV = worldUV.xy;

            //o.viewVector = _WorldSpaceCameraPos.xyz - mul(unity_ObjectToWorld, v.vertex).xyz;
            //float viewDist = length(o.viewVector);
            
            float3 displacement = 0;

            #if defined(ONLY_CLOSE)
            float2 particleUV = worldUV / 250 + 0.5; // 映射到 [0,1] 范围
            float height = tex2Dlod(_ParticleHeightMap, float4(particleUV, 0, 0)).r;
            displacement += float3(0, height * _ParticleHeightScale, 0);
            //或者加lod_c2混合控制
            //displacement += float3(0, height * _ParticleHeightScale, 0) * lod_c2;
            #endif

            v.vertex.xyz += mul(unity_WorldToObject,displacement);
        }

        fixed4 _Color, _FoamColor, _SSSColor;
        float _SSSStrength;
        float _Roughness, _RoughnessScale, _MaxGloss;
        float _FoamBiasLOD0, _FoamBiasLOD1, _FoamBiasLOD2, _FoamScale, _ContactFoam;
        sampler2D _CameraDepthTexture;
        sampler2D _FoamTexture;

        float3 WorldToTangentNormalVector(Input IN, float3 normal) {
            float3 t2w0 = WorldNormalVector(IN, float3(1, 0, 0));
            float3 t2w1 = WorldNormalVector(IN, float3(0, 1, 0));
            float3 t2w2 = WorldNormalVector(IN, float3(0, 0, 1));
            float3x3 t2w = float3x3(t2w0, t2w1, t2w2);
            return normalize(mul(t2w, normal));
        }

        float pow5(float f)
        {
            return f * f * f * f * f;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {

            #if defined(ONLY_CLOSE)
            //o.Albedo = float3(1, 0, 0); // 全红色 test
            //return;
            #endif
            float4 derivatives = tex2D(_Derivatives_c0, IN.worldUV / LengthScale0);

            float2 slope = float2(derivatives.x / (1 + derivatives.z),
                derivatives.y / (1 + derivatives.w));
            float3 worldNormal = normalize(float3(-slope.x, 1, -slope.y));

            //此处置为0即可注释泡沫效果
            float jacobian = 0;

            o.Albedo = lerp(0, _FoamColor, jacobian);
            float distanceGloss = lerp(1 - _Roughness, _MaxGloss, 1 / (1 + length(IN.viewVector) * _RoughnessScale));
            o.Smoothness = lerp(distanceGloss, 0, jacobian);
            o.Metallic = 0;

            float3 viewDir = normalize(IN.viewVector);
            float3 H = normalize(-worldNormal + _WorldSpaceLightPos0);
            float ViewDotH = pow5(saturate(dot(viewDir, -H))) * 30 * _SSSStrength;
            fixed3 color = lerp(_Color, saturate(_Color + _SSSColor.rgb * ViewDotH * IN.lodScales.w), IN.lodScales.z);

            float fresnel = dot(worldNormal, viewDir);
            fresnel = saturate(1 - fresnel);
            fresnel = pow5(fresnel);

            o.Emission = lerp(color * (1 - fresnel), 0, jacobian);
        }
        ENDCG
    }
        FallBack "Diffuse"
}