Shader "Ocean/OceanFFT"
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

            // —— 以下粒子相关属性保留以兼容材质，但不会被使用 ——
            _RegionCount("Region Count", Int) = 1
            _ParticleHeightMap0("Wave Particle Height Map 0", 2D) = "black" {}
            _ParticleNormalMap0("Wave Particle Normal Map 0", 2D) = "black" {}
            _ParticleHeightMap1("Wave Particle Height Map 1", 2D) = "black" {}
            _ParticleNormalMap1("Wave Particle Normal Map 1", 2D) = "black" {}
            _ParticleHeightMap2("Wave Particle Height Map 2", 2D) = "black" {}
            _ParticleNormalMap2("Wave Particle Normal Map 2", 2D) = "black" {}
            _RegionCenter0("Region Center 0", Vector) = (0,0,0,0)
            _RegionSize0("Region Size 0", Vector) = (0,0,0,0)
            _RegionCenter1("Region Center 1", Vector) = (0,0,0,0)
            _RegionSize1("Region Size 1", Vector) = (0,0,0,0)
            _RegionCenter2("Region Center 2", Vector) = (0,0,0,0)
            _RegionSize2("Region Size 2", Vector) = (0,0,0,0)
            _BlendRange("Blend Range", Range(0,1)) = 0.2
            _BlendStrength("Blend Strength", Range(0,1)) = 0.5

                // —— FFT 贴图（实际使用） ——
                [HideInInspector]_Displacement_c0("Displacement C0", 2D) = "black" {}
                [HideInInspector]_Derivatives_c0("Derivatives C0", 2D) = "black" {}
                [HideInInspector]_Turbulence_c0("Turbulence C0", 2D) = "white" {}
    }

        SubShader
    {
        // 原文件是 Queue=Transparent 但 RenderType=Opaque，这里沿用以保持排序表现
        Tags { "Queue" = "Transparent" "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
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

    // ===== FFT 贴图 =====
    sampler2D _Displacement_c0;
    sampler2D _Derivatives_c0;
    sampler2D _Turbulence_c0;

    // ===== 仍保留的参数（与 SSS/LOD 等计算相关） =====
    float LengthScale0;
    float _LOD_scale;
    float _SSSBase;
    float _SSSScale;

    void vert(inout appdata_full v, out Input o)
    {
        UNITY_INITIALIZE_OUTPUT(Input, o);
        float3 worldPos = mul(unity_ObjectToWorld, v.vertex);
        float2 worldPosXZ = worldPos.xz;

        o.worldUV = worldPosXZ;
        o.viewVector = _WorldSpaceCameraPos - worldPos;

        // ===== 仅使用 FFT 位移 =====
        float3 displacement = 0;
        displacement += tex2Dlod(_Displacement_c0, float4(worldPosXZ / LengthScale0, 0, 0));

        // SSS 相关（沿用原先逻辑）
        float largeWavesBias = displacement.y;
        o.lodScales = float4(0, 0, 1,
            max(displacement.y - largeWavesBias * 0.8 - _SSSBase, 0) / _SSSScale);

        // 应用位移（从世界到物体空间）
        v.vertex.xyz += mul(unity_WorldToObject, displacement);
    }

    // ===== 材质参数 =====
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

    float pow5(float f) { return f * f * f * f * f; }

    void surf(Input IN, inout SurfaceOutputStandard o)
    {
        float2 worldPosXZ = IN.worldUV;

        // ===== 仅使用 FFT 法线（由导数贴图推导） =====
        float4 derivatives = tex2D(_Derivatives_c0, worldPosXZ / LengthScale0);
        float2 slope = float2(derivatives.x / (1 + derivatives.z),
                              derivatives.y / (1 + derivatives.w));
        float3 worldNormal = normalize(float3(-slope.x, 1, -slope.y));

        o.Normal = WorldToTangentNormalVector(IN, worldNormal);

        float3 viewDir = normalize(IN.viewVector);
        float3 H = normalize(-worldNormal + _WorldSpaceLightPos0);
        float ViewDotH = pow5(saturate(dot(viewDir, -H))) * 30 * _SSSStrength;

        float fresnel = pow5(saturate(1 - dot(worldNormal, viewDir)));

        float distanceGloss = lerp(1 - _Roughness, _MaxGloss,
                                   1 / (1 + length(IN.viewVector) * _RoughnessScale));
        o.Smoothness = distanceGloss;
        o.Metallic = 0;

        float3 color = _Color.rgb;
        o.Albedo = color;
        o.Emission = color * (1 - fresnel);
    }

    ENDCG
    }
        FallBack "Diffuse"
}
