Shader "Ocean/Ocean_PBR"
{
    Properties
    {
        // 基础色与粗糙度控制
        _Color("Albedo Color", Color) = (0.05,0.2,0.3,1)
        _Roughness("Distant Roughness", Range(0,1)) = 0.1
        _RoughnessScale("Roughness Distance Scale", Range(0,0.02)) = 0.002
        _MaxGloss("Max Gloss (Near)", Range(0,1)) = 0.95

        // FFT 相关贴图与尺度
        _Displacement_c0("FFT Displacement (xyz)", 2D) = "black" {}
        _Derivatives_c0("FFT Derivatives (xyzw)", 2D) = "black" {}
        _LengthScale("FFT Length Scale", Float) = 100.0

            // 区域/波粒子混合
            _RegionCount("Region Count", Int) = 0

            _ParticleHeightMap0("Particle Height 0 (R)", 2D) = "black" {}
            _ParticleNormalMap0("Particle Normal 0 (XYZ)", 2D) = "black" {}
            _RegionCenter0("Region Center 0 (x,y)", Vector) = (0,0,0,0)
            _RegionSize0("Region Size 0 (w,h)", Vector) = (0,0,0,0)

            _ParticleHeightMap1("Particle Height 1 (R)", 2D) = "black" {}
            _ParticleNormalMap1("Particle Normal 1 (XYZ)", 2D) = "black" {}
            _RegionCenter1("Region Center 1 (x,y)", Vector) = (0,0,0,0)
            _RegionSize1("Region Size 1 (w,h)", Vector) = (0,0,0,0)

            _ParticleHeightMap2("Particle Height 2 (R)", 2D) = "black" {}
            _ParticleNormalMap2("Particle Normal 2 (XYZ)", 2D) = "black" {}
            _RegionCenter2("Region Center 2 (x,y)", Vector) = (0,0,0,0)
            _RegionSize2("Region Size 2 (w,h)", Vector) = (0,0,0,0)

            _ParticleHeightScale("Particle Height Scale", Float) = 1.0
            _BlendRange("Region Edge Blend Range (0~1 of size)", Range(0,1)) = 0.2
            _BlendStrength("Region-FFT Blend Strength", Range(0,1)) = 0.5

                // 切换：强制仅用 FFT（忽略区域/粒子）
                _UseFFTOnly("Use FFT Only", Float) = 0
    }

        SubShader
            {
                // 透明队列以便与场景水下交叠时排序更灵活；RenderType 用 Opaque 以沿用 Standard 光照
                Tags { "Queue" = "Transparent" "RenderType" = "Opaque" }
                LOD 200

                CGPROGRAM
                #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
                #pragma target 4.0

                struct Input
                {
                    float2 worldUV;          // 世界空间 xz（用于采样）
                    float3 viewVector;       // 世界视向
                    float3 worldNormal;      // Surface 期望的世界法线（Unity 自动）
                    float4 screenPos;        // 深度/屏幕特效可用
                    INTERNAL_DATA
                };

            // ====== 纹理与参数 ======
            sampler2D _Displacement_c0;
            sampler2D _Derivatives_c0;
            float _LengthScale;

            int _RegionCount;
            sampler2D _ParticleHeightMap0, _ParticleHeightMap1, _ParticleHeightMap2;
            sampler2D _ParticleNormalMap0, _ParticleNormalMap1, _ParticleNormalMap2;
            float4 _RegionCenter0, _RegionCenter1, _RegionCenter2;
            float4 _RegionSize0, _RegionSize1, _RegionSize2;
            float  _ParticleHeightScale;

            float  _BlendRange;
            float  _BlendStrength;
            float  _UseFFTOnly;

            fixed4 _Color;
            float  _Roughness, _RoughnessScale, _MaxGloss;

            // ====== 工具函数 ======
            bool InRegion(float2 pos, float4 center, float4 size)
            {
                float2 halfSize = size.xy * 0.5;
                float2 mn = center.xy - halfSize;
                float2 mx = center.xy + halfSize;
                return (pos.x >= mn.x && pos.x <= mx.x && pos.y >= mn.y && pos.y <= mx.y);
            }

            // 返回命中的 region 索引，并输出其 UV（0~1）
            int GetRegionIndex(float2 pos, out float2 regionUV)
            {
                if (_RegionCount > 0 && InRegion(pos, _RegionCenter0, _RegionSize0))
                {
                    regionUV = (pos - _RegionCenter0.xy) / _RegionSize0.xy + 0.5; return 0;
                }
                if (_RegionCount > 1 && InRegion(pos, _RegionCenter1, _RegionSize1))
                {
                    regionUV = (pos - _RegionCenter1.xy) / _RegionSize1.xy + 0.5; return 1;
                }
                if (_RegionCount > 2 && InRegion(pos, _RegionCenter2, _RegionSize2))
                {
                    regionUV = (pos - _RegionCenter2.xy) / _RegionSize2.xy + 0.5; return 2;
                }
                regionUV = 0; return -1;
                }

            // 计算距离边界的混合系数（越靠近边界越大），blendRange 为相对尺寸（0~1）
            float ComputeRegionBlend(float2 pos, float4 center, float4 size, float blendRange)
            {
                float2 halfSize = size.xy * 0.5;
                float2 boxMin = center.xy - halfSize;
                float2 boxMax = center.xy + halfSize;

                float2 dMin = pos - boxMin;
                float2 dMax = boxMax - pos;
                float2 insideDist = min(dMin, dMax);               // 距离各条边界的距离
                float2 blendDist = max(size.xy * blendRange, 1e-5); // 防除零

                float2 edgeBlend = saturate(1.0 - insideDist / blendDist);
                return max(edgeBlend.x, edgeBlend.y);              // 靠近任一边就混
            }

            float3 WorldToTangentNormalVector(Input IN, float3 worldN)
            {
                float3 t2w0 = WorldNormalVector(IN, float3(1,0,0));
                float3 t2w1 = WorldNormalVector(IN, float3(0,1,0));
                float3 t2w2 = WorldNormalVector(IN, float3(0,0,1));
                float3x3 t2w = float3x3(t2w0, t2w1, t2w2);
                return normalize(mul(t2w, worldN));
            }

            float pow5(float x) { return x * x * x * x * x; }

            // ====== 顶点：位移 ======
            void vert(inout appdata_full v, out Input o)
            {
                UNITY_INITIALIZE_OUTPUT(Input, o);

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float2 worldXZ = worldPos.xz;

                o.worldUV = worldXZ;
                o.viewVector = _WorldSpaceCameraPos - worldPos;

                // 采样 FFT 位移高度（使用 y 分量，与你现有贴图保持一致）
                float fftHeight = tex2Dlod(_Displacement_c0, float4(worldXZ / _LengthScale, 0, 0)).y;

                // 一键仅用 FFT
                if (_UseFFTOnly > 0.5)
                {
                    float3 disp = float3(0, fftHeight, 0);
                    v.vertex.xyz += mul(unity_WorldToObject, disp);
                    return;
                }

                // 区域高度（R 通道 * scale）
                float2 regionUV;
                int    ri = GetRegionIndex(worldXZ, regionUV);

                float regionHeight = 0;
                float blend = 0;

                if (ri == 0) {
                    regionHeight = tex2Dlod(_ParticleHeightMap0, float4(regionUV,0,0)).r * _ParticleHeightScale;
                    blend = ComputeRegionBlend(worldXZ, _RegionCenter0, _RegionSize0, _BlendRange);
                }
                else if (ri == 1) {
                    regionHeight = tex2Dlod(_ParticleHeightMap1, float4(regionUV,0,0)).r * _ParticleHeightScale;
                    blend = ComputeRegionBlend(worldXZ, _RegionCenter1, _RegionSize1, _BlendRange);
                }
                else if (ri == 2) {
                    regionHeight = tex2Dlod(_ParticleHeightMap2, float4(regionUV,0,0)).r * _ParticleHeightScale;
                    blend = ComputeRegionBlend(worldXZ, _RegionCenter2, _RegionSize2, _BlendRange);
                }

                float finalH = (ri >= 0) ? lerp(regionHeight, fftHeight, saturate(blend * _BlendStrength))
                                         : fftHeight;

                float3 dispWS = float3(0, finalH, 0);
                v.vertex.xyz += mul(unity_WorldToObject, dispWS);
            }

            // ====== 像素：法线/BRDF ======
            void surf(Input IN, inout SurfaceOutputStandard o)
            {
                float2 worldXZ = IN.worldUV;

                // 1) FFT 法线（由导数重建）
                float4 deriv = tex2D(_Derivatives_c0, worldXZ / _LengthScale);
                float2 slope = float2(deriv.x / (1 + deriv.z), deriv.y / (1 + deriv.w));
                float3 fftN = normalize(float3(-slope.x, 1, -slope.y));

                if (_UseFFTOnly > 0.5)
                {
                    o.Normal = WorldToTangentNormalVector(IN, fftN);
                }
                else
                {
                    // 2) Region 法线（如命中）
                    float2 regionUV;
                    int ri = GetRegionIndex(worldXZ, regionUV);

                    float3 regionN = fftN;  // 兜底
                    bool   hasRegionN = false;

                    if (ri == 0) { regionN = normalize(tex2D(_ParticleNormalMap0, regionUV).xyz * 2 - 1); hasRegionN = true; }
                    else if (ri == 1) { regionN = normalize(tex2D(_ParticleNormalMap1, regionUV).xyz * 2 - 1); hasRegionN = true; }
                    else if (ri == 2) { regionN = normalize(tex2D(_ParticleNormalMap2, regionUV).xyz * 2 - 1); hasRegionN = true; }

                    float blend = 0;
                    if (ri == 0) blend = ComputeRegionBlend(worldXZ, _RegionCenter0, _RegionSize0, _BlendRange);
                    else if (ri == 1) blend = ComputeRegionBlend(worldXZ, _RegionCenter1, _RegionSize1, _BlendRange);
                    else if (ri == 2) blend = ComputeRegionBlend(worldXZ, _RegionCenter2, _RegionSize2, _BlendRange);

                    float w = (hasRegionN ? saturate(blend * _BlendStrength) : 1.0);
                    float3 worldN = (hasRegionN ? normalize(lerp(regionN, fftN, w)) : fftN);

                    o.Normal = WorldToTangentNormalVector(IN, worldN);
                }

                // 3) 简化的 PBR：颜色+F0 近似（用 Fresnel 做一点自发光以提亮掠射）
                float3 V = normalize(IN.viewVector);
                float3 N = normalize(WorldNormalVector(IN, o.Normal));
                float  fresnel = pow5(saturate(1 - dot(N, V)));

                float distanceGloss = lerp(1 - _Roughness, _MaxGloss, 1 / (1 + length(IN.viewVector) * _RoughnessScale));
                o.Smoothness = saturate(distanceGloss);
                o.Metallic = 0.0;

                float3 baseColor = _Color.rgb;
                o.Albedo = baseColor;
                o.Emission = baseColor * (1 - fresnel); // 掠射暗、正面亮 → 近似水的能量再分布
            }
            ENDCG
            }

            FallBack "Diffuse"
}
