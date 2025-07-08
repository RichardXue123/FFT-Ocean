﻿Shader "Ocean/OceanFFT"
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
        _FoamScale("Foam Scale", Range(0,20)) = 1
        _ContactFoam("Contact Foam", Range(0,1)) = 1
        _ParticleHeightMap("Wave Particle Height Map", 2D) = "black" {} //加入新的波粒子高度图
        _ParticleNormalMap("Wave Particle Height Map", 2D) = "black" {} //加入新的波粒子法线图


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
        #pragma multi_compile _ MID CLOSE
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

        sampler2D _ParticleNormalMap;

        float LengthScale0;
        float _LOD_scale = 1.0f;
        float _SSSBase;
        float _SSSScale;

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float3 worldPos = mul(unity_ObjectToWorld, v.vertex);
            float4 worldUV = float4(worldPos.xz, 0, 0);
            o.worldUV = worldUV.xy;

            o.viewVector = _WorldSpaceCameraPos - worldPos;
            //float viewDist = length(o.viewVector);

            float3 displacement = 0;

            float2 worldPosXZ = worldUV;
            // Mask 中心 100x100 世界坐标区域（即 x,z ∈ [-50,50]）
            if (abs(worldPosXZ.x) < 50 && abs(worldPosXZ.y) < 50)
            {
                float2 particleUV = worldUV / 100 + 0.5; // 映射到 [0,1] 范围
                float height = tex2Dlod(_ParticleHeightMap, float4(particleUV, 0, 0)).r;
                displacement += float3(0, height * _ParticleHeightScale, 0);

                // ✅ 设置 lodScales 用于 SSS 效果计算
                o.lodScales = float4(0, 0, 1, max(height * _ParticleHeightScale - _SSSBase, 0) / _SSSScale);
            }
            else
            {
                displacement += tex2Dlod(_Displacement_c0, worldUV / LengthScale0);
                float largeWavesBias = displacement.y;
                o.lodScales = float4(0, 0, 1, max(displacement.y - largeWavesBias * 0.8 - _SSSBase, 0) / _SSSScale);
            }
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
            float2 worldPosXZ = IN.worldUV;

            bool inParticleZone = abs(worldPosXZ.x) < 50 && abs(worldPosXZ.y) < 50;

            float3 worldNormal;

            if (abs(worldPosXZ.x) < 50 && abs(worldPosXZ.y) < 50)
            {
                // 1. 映射 UV
                float2 uv = worldPosXZ / 100 + 0.5;

                // 2. 贴图步长
                float texelSize = 1.0 / 512.0;

                // 3. 采样上下左右四个点
                float hC = tex2D(_ParticleHeightMap, uv).r;
                float hL = tex2D(_ParticleHeightMap, uv + float2(-texelSize, 0)).r;
                float hR = tex2D(_ParticleHeightMap, uv + float2(texelSize, 0)).r;
                float hD = tex2D(_ParticleHeightMap, uv + float2(0, -texelSize)).r;
                float hU = tex2D(_ParticleHeightMap, uv + float2(0, texelSize)).r;

                // 4. 计算 dx, dz 方向向量
                float particleRegionWidth = 100; // 你的实际区域宽度
                float3 dx = float3(2 * texelSize * particleRegionWidth, hR - hL, 0);
                float3 dz = float3(0, hU - hD, 2 * texelSize * particleRegionWidth);

                // 5. 交叉乘得法线
                float3 normal = normalize(cross(dz, dx));
                //float3 normal = tex2D(_ParticleNormalMap, uv).xyz;
                //float3 normal = tex2D(_ParticleNormalMap, uv).xyz * 2 - 1;
                worldNormal = normal;
            }
            else
            {
                // 非粒子区域：使用 FFT 法线贴图
                float4 derivatives = tex2D(_Derivatives_c0, IN.worldUV / LengthScale0);
                float2 slope = float2(derivatives.x / (1 + derivatives.z), derivatives.y / (1 + derivatives.w));
                worldNormal = normalize(float3(-slope.x, 1, -slope.y));

                //float2 uv = IN.worldUV / LengthScale0;
                //float texelSize = 1.0 / 512.0; // 如果你的 FFT 贴图是 512x512

                //float hC = tex2D(_Displacement_c0, uv).y;
                //float hL = tex2D(_Displacement_c0, uv + float2(-texelSize, 0)).y;
                //float hR = tex2D(_Displacement_c0, uv + float2(texelSize, 0)).y;
                //float hD = tex2D(_Displacement_c0, uv + float2(0, -texelSize)).y;
                //float hU = tex2D(_Displacement_c0, uv + float2(0, texelSize)).y;

                //float3 dx = float3(2 * texelSize * LengthScale0, hR - hL, 0);
                //float3 dz = float3(0, hU - hD, 2 * texelSize * LengthScale0);

                //worldNormal = normalize(cross(dz, dx));
            }

            o.Normal = WorldToTangentNormalVector(IN, worldNormal);

            // 外观设置（不包含泡沫）
            float3 viewDir = normalize(IN.viewVector);
            float3 H = normalize(-worldNormal + _WorldSpaceLightPos0);
            float ViewDotH = pow5(saturate(dot(viewDir, -H))) * 30 * _SSSStrength;

            float fresnel = pow5(saturate(1 - dot(worldNormal, viewDir)));

            float distanceGloss = lerp(1 - _Roughness, _MaxGloss, 1 / (1 + length(IN.viewVector) * _RoughnessScale));
            o.Smoothness = distanceGloss;
            o.Metallic = 0;

            //float3 color = lerp(_Color, saturate(_Color + _SSSColor.rgb * ViewDotH * IN.lodScales.w), IN.lodScales.z);
            //float3 color = _Color + _SSSColor.rgb * ViewDotH * 0.2;
            float3 color = _Color;
            o.Albedo = color;
            o.Emission = color * (1 - fresnel);
        }
        ENDCG
    }
        FallBack "Diffuse"
}