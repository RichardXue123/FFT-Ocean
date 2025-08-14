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

            // 以下三张纹理仍保留属性以兼容材质，但不会被采样使用
            [HideInInspector]_Displacement_c0("Displacement C0", 2D) = "black" {}
            [HideInInspector]_Derivatives_c0("Derivatives C0", 2D) = "black" {}
            [HideInInspector]_Turbulence_c0("Turbulence C0", 2D) = "white" {}
    }

        SubShader
    {
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

    // ===== 波粒子相关 =====
    int _RegionCount;
    sampler2D _ParticleHeightMap0, _ParticleHeightMap1, _ParticleHeightMap2;
    sampler2D _ParticleNormalMap0, _ParticleNormalMap1, _ParticleNormalMap2;
    float4 _RegionCenter0, _RegionCenter1, _RegionCenter2;
    float4 _RegionSize0, _RegionSize1, _RegionSize2;
    float _ParticleHeightScale;

    // 兼容保留
    float LengthScale0;
    float _LOD_scale = 1.0f;
    float _SSSBase;
    float _SSSScale;

    float _BlendRange;
    float _BlendStrength;

    // ===== 通用函数 =====
    bool InRegion(float2 pos, float4 center, float4 size)
    {
        float2 halfSize = size.xy * 0.5;
        float2 minv = center.xy - halfSize;
        float2 maxv = center.xy + halfSize;
        return (pos.x >= minv.x && pos.x <= maxv.x && pos.y >= minv.y && pos.y <= maxv.y);
    }

    int GetRegionIndex(float2 pos, out float2 regionUV)
    {
        if (_RegionCount > 0 && InRegion(pos, _RegionCenter0, _RegionSize0)) {
            regionUV = (pos - _RegionCenter0.xy) / _RegionSize0.xy + 0.5;
            return 0;
        }
        if (_RegionCount > 1 && InRegion(pos, _RegionCenter1, _RegionSize1)) {
            regionUV = (pos - _RegionCenter1.xy) / _RegionSize1.xy + 0.5;
            return 1;
        }
        if (_RegionCount > 2 && InRegion(pos, _RegionCenter2, _RegionSize2)) {
            regionUV = (pos - _RegionCenter2.xy) / _RegionSize2.xy + 0.5;
            return 2;
        }
        regionUV = float2(0,0);
        return -1;
    }

    float ComputeRegionBlend(float2 pos, float4 center, float4 size, float blendRange)
    {
        float2 halfSize = size.xy * 0.5;
        float2 boxMin = center.xy - halfSize;
        float2 boxMax = center.xy + halfSize;

        float2 deltaToMin = pos - boxMin;
        float2 deltaToMax = boxMax - pos;
        float2 insideDist = min(deltaToMin, deltaToMax);

        float2 blendDist = max(blendRange * size.xy, 1e-5); // 避免除零
        float2 edgeBlend = saturate(1.0 - insideDist / blendDist);

        return max(edgeBlend.x, edgeBlend.y);
    }

    void vert(inout appdata_full v, out Input o)
    {
        UNITY_INITIALIZE_OUTPUT(Input, o);
        float3 worldPos = mul(unity_ObjectToWorld, v.vertex);
        float2 worldPosXZ = worldPos.xz;

        o.worldUV = worldPosXZ;
        o.viewVector = _WorldSpaceCameraPos - worldPos;

        // ===== 仅使用波粒子位移，高度以 Region 为准；非 Region 区域保持平面 =====
        float2 regionUV;
        int regionIdx = GetRegionIndex(worldPosXZ, regionUV);

        float3 displacement = 0;
        if (regionIdx == 0) {
            displacement.y = tex2Dlod(_ParticleHeightMap0, float4(regionUV, 0, 0)).r * _ParticleHeightScale;
        }
        else if (regionIdx == 1) {
            displacement.y = tex2Dlod(_ParticleHeightMap1, float4(regionUV, 0, 0)).r * _ParticleHeightScale;
        }
        else if (regionIdx == 2) {
            displacement.y = tex2Dlod(_ParticleHeightMap2, float4(regionUV, 0, 0)).r * _ParticleHeightScale;
        }
        // else: displacement 为 0，平水面

        // SSS/LOD 等与位移相关的量清零/默认
        o.lodScales = float4(0,0,1,0);

        // 将世界位移转换到物体空间叠加
        v.vertex.xyz += mul(unity_WorldToObject, displacement);
    }

    fixed4 _Color, _FoamColor, _SSSColor;
    float _SSSStrength;
    float _Roughness, _RoughnessScale, _MaxGloss;
    float _FoamBiasLOD0, _FoamBiasLOD1, _FoamBiasLOD2, _FoamScale, _ContactFoam;
    sampler2D _CameraDepthTexture;
    sampler2D _FoamTexture;

    float3 WorldToTangentNormalVector(Input IN, float3 normal) {
        float3 t2w0 = WorldNormalVector(IN, float3(1,0,0));
        float3 t2w1 = WorldNormalVector(IN, float3(0,1,0));
        float3 t2w2 = WorldNormalVector(IN, float3(0,0,1));
        float3x3 t2w = float3x3(t2w0, t2w1, t2w2);
        return normalize(mul(t2w, normal));
    }

    float pow5(float f) { return f * f * f * f * f; }

    void surf(Input IN, inout SurfaceOutputStandard o)
    {
        float2 worldPosXZ = IN.worldUV;

        // ===== 仅使用波粒子法线；非 Region 用平面法线 =====
        float2 regionUV;
        int regionIdx = GetRegionIndex(worldPosXZ, regionUV);

        // 不在任何 Region → 直接透明
        if (regionIdx < 0) {
            clip(-1); // 丢弃像素
            return;
        }

        float3 worldNormal = float3(0,1,0);
        if (regionIdx == 0) {
            worldNormal = normalize(tex2D(_ParticleNormalMap0, regionUV).xyz * 2 - 1);
        }
        else if (regionIdx == 1) {
            worldNormal = normalize(tex2D(_ParticleNormalMap1, regionUV).xyz * 2 - 1);
        }
        else if (regionIdx == 2) {
            worldNormal = normalize(tex2D(_ParticleNormalMap2, regionUV).xyz * 2 - 1);
        }
        // 不再采样 _Derivatives_c0/_Displacement_c0，FFT 完全禁用

        o.Normal = WorldToTangentNormalVector(IN, worldNormal);

        float3 viewDir = normalize(IN.viewVector);
        float3 H = normalize(-worldNormal + _WorldSpaceLightPos0);
        float ViewDotH = pow5(saturate(dot(viewDir, -H))) * 30 * _SSSStrength;

        float fresnel = pow5(saturate(1 - dot(worldNormal, viewDir)));

        float distanceGloss = lerp(1 - _Roughness, _MaxGloss, 1 / (1 + length(IN.viewVector) * _RoughnessScale));
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
