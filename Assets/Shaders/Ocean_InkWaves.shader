Shader "Ocean/Ocean_InkWaves"
{
    Properties
    {
        // —— 海面位移 / 法线（与你的海浪一致，做了简化命名）——
        _Displacement_c0("FFT Displacement (xyz)", 2D) = "black" {}
        _Derivatives_c0 ("FFT Derivatives (xyzw)", 2D) = "black" {}
        _LengthScale    ("FFT Length Scale", Float) = 100.0

        _RegionCount ("Region Count", Int) = 0
        _ParticleHeightMap0("Particle Height 0 (R)", 2D) = "black" {}
        _ParticleNormalMap0("Particle Normal 0 (XYZ)", 2D) = "black" {}
        _RegionCenter0("Region Center 0 (x,y)", Vector) = (0,0,0,0)
        _RegionSize0  ("Region Size 0 (w,h)", Vector) = (0,0,0,0)

        _ParticleHeightMap1("Particle Height 1 (R)", 2D) = "black" {}
        _ParticleNormalMap1("Particle Normal 1 (XYZ)", 2D) = "black" {}
        _RegionCenter1("Region Center 1 (x,y)", Vector) = (0,0,0,0)
        _RegionSize1  ("Region Size 1 (w,h)", Vector) = (0,0,0,0)

        _ParticleHeightMap2("Particle Height 2 (R)", 2D) = "black" {}
        _ParticleNormalMap2("Particle Normal 2 (XYZ)", 2D) = "black" {}
        _RegionCenter2("Region Center 2 (x,y)", Vector) = (0,0,0,0)
        _RegionSize2  ("Region Size 2 (w,h)", Vector) = (0,0,0,0)

        _ParticleHeightScale("Particle Height Scale", Float) = 1.0
        _BlendRange   ("Region Edge Blend Range", Range(0,1)) = 0.2
        _BlendStrength("Region-FFT Blend Strength", Range(0,1)) = 0.5
        _UseFFTOnly   ("Use FFT Only", Float) = 0

        // —— 水墨主体着色 —— 
        _MainTex("Base (optional)", 2D) = "white" {}
        _Color  ("Tint", Color) = (0.05,0.2,0.3,1)
        _InkPow ("Ink Exponent", Range(0.5,8)) = 2.5      // 视角对比：大=更易重墨
        _BlackRamp("Black Ink Ramp/Texture", 2D) = "gray" {} // U:墨阶, V:纸纹变化
        _GrayRamp ("Gray  Ink Ramp/Texture", 2D) = "gray" {} // U:墨阶
        _DepthLUT ("Depth Fade 1D (x-axis)", 2D) = "white" {} // 线性深度→留白
        _InkSplit ("Ink Split Threshold", Range(0,1)) = 0.25

        // —— 描边 —— 
        _Outline   ("Outline Max Width (world/view-scaled)", Float) = 0.02
        _OutlineZBias("Outline Z Bias (view normal z)", Range(-1,1)) = -0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }  // 双Pass都不透明；排序默认

        // ============ 公用 CG ============ 
        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _Displacement_c0, _Derivatives_c0;
        float _LengthScale;

        int _RegionCount;
        sampler2D _ParticleHeightMap0, _ParticleHeightMap1, _ParticleHeightMap2;
        sampler2D _ParticleNormalMap0, _ParticleNormalMap1, _ParticleNormalMap2;
        float4 _RegionCenter0, _RegionCenter1, _RegionCenter2;
        float4 _RegionSize0,   _RegionSize1,   _RegionSize2;
        float  _ParticleHeightScale;
        float  _BlendRange, _BlendStrength, _UseFFTOnly;

        sampler2D _MainTex, _BlackRamp, _GrayRamp, _DepthLUT;
        float4 _Color;
        float  _InkPow, _InkSplit;

        float  _Outline, _OutlineZBias;

        struct AppData {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv     : TEXCOORD0;
        };

        struct Vary {
            float4 posCS  : SV_POSITION;
            float2 uv     : TEXCOORD0;
            float3 nWS    : TEXCOORD1;
            float3 vWS    : TEXCOORD2; // view vector (world)
            float2 zwCS   : TEXCOORD3; // for depth linearization
        };

        // —— Region 判定 & 混合 —— 
        bool InRegion(float2 p, float4 c, float4 s) {
            float2 h = s.xy * 0.5;
            float2 mn = c.xy - h, mx = c.xy + h;
            return all( float2(p.x>=mn.x && p.x<=mx.x, p.y>=mn.y && p.y<=mx.y) );
        }
        int GetRegionIndex(float2 p, out float2 uvR) {
            if(_RegionCount>0 && InRegion(p,_RegionCenter0,_RegionSize0)){ uvR=(p-_RegionCenter0.xy)/_RegionSize0.xy+0.5; return 0; }
            if(_RegionCount>1 && InRegion(p,_RegionCenter1,_RegionSize1)){ uvR=(p-_RegionCenter1.xy)/_RegionSize1.xy+0.5; return 1; }
            if(_RegionCount>2 && InRegion(p,_RegionCenter2,_RegionSize2)){ uvR=(p-_RegionCenter2.xy)/_RegionSize2.xy+0.5; return 2; }
            uvR=0; return -1;
        }
        float ComputeRegionBlend(float2 p, float4 c, float4 s, float br) {
            float2 h=s.xy*0.5, mn=c.xy-h, mx=c.xy+h;
            float2 d=min(p-mn, mx-p);
            float2 b=max(s.xy*br, 1e-5);
            float2 e=saturate(1.0 - d/b);
            return max(e.x,e.y);
        }

        // —— 顶点位移（世界空间 y 位移），并输出需要的 varyings —— 
        Vary VertDisplace(AppData v)
        {
            Vary o;
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float2 xz = wp.xz;

            // FFT 高度
            float fftH = tex2Dlod(_Displacement_c0, float4(xz / _LengthScale, 0, 0)).y;

            // Region 混合（只在 _UseFFTOnly <= 0.5 时生效）
            float finalH = fftH;
            if (_UseFFTOnly <= 0.5)
            {
                float2 uvR; int ri = GetRegionIndex(xz, uvR);
                if (ri >= 0) {
                    float hR = (ri == 0 ? tex2Dlod(_ParticleHeightMap0, float4(uvR, 0, 0)).r :
                        (ri == 1 ? tex2Dlod(_ParticleHeightMap1, float4(uvR, 0, 0)).r :
                            tex2Dlod(_ParticleHeightMap2, float4(uvR, 0, 0)).r)) * _ParticleHeightScale;
                    float b = (ri == 0 ? ComputeRegionBlend(xz, _RegionCenter0, _RegionSize0, _BlendRange) :
                        (ri == 1 ? ComputeRegionBlend(xz, _RegionCenter1, _RegionSize1, _BlendRange) :
                            ComputeRegionBlend(xz, _RegionCenter2, _RegionSize2, _BlendRange)));
                    finalH = lerp(hR, fftH, saturate(b * _BlendStrength));
                }
            }

            // 应用位移
            wp.y += finalH;

            // 输出
            o.posCS = mul(UNITY_MATRIX_VP, float4(wp, 1));
            o.uv = v.uv;
            o.vWS = _WorldSpaceCameraPos.xyz - wp;

            // —— 法线：优先 FFT 法线 —— 
            float4 deriv = tex2Dlod(_Derivatives_c0, float4(xz / _LengthScale, 0, 0));
            float2 slope = float2(deriv.x / (1 + deriv.z), deriv.y / (1 + deriv.w));
            float3 nFFT = normalize(float3(-slope.x, 1, -slope.y));

            if (_UseFFTOnly > 0.5)
            {
                // 只用 FFT：直接用 FFT 法线，别再进 region 混合
                o.nWS = nFFT;
                o.zwCS = o.posCS.zw;
                return o;
            }

            // —— 只有在允许时才做 region 法线混合 —— 
            float3 nR = nFFT; bool hasR = false; float2 uvR2; int ri2 = GetRegionIndex(xz, uvR2);
            if (ri2 >= 0) {
                nR = normalize((ri2 == 0 ? tex2Dlod(_ParticleNormalMap0, float4(uvR2, 0, 0)).xyz :
                    (ri2 == 1 ? tex2Dlod(_ParticleNormalMap1, float4(uvR2, 0, 0)).xyz :
                        tex2Dlod(_ParticleNormalMap2, float4(uvR2, 0, 0)).xyz)) * 2 - 1);
                hasR = true;
            }
            float b2 = (ri2 == 0 ? ComputeRegionBlend(xz, _RegionCenter0, _RegionSize0, _BlendRange) :
                (ri2 == 1 ? ComputeRegionBlend(xz, _RegionCenter1, _RegionSize1, _BlendRange) :
                    (ri2 == 2 ? ComputeRegionBlend(xz, _RegionCenter2, _RegionSize2, _BlendRange) : 0)));
            float w = (hasR ? saturate(b2 * _BlendStrength) : 1.0);
            o.nWS = normalize(hasR ? lerp(nR, nFFT, w) : nFFT);

            o.zwCS = o.posCS.zw;
            return o;
        }

        ENDCG

        // -------- Pass 1：描边（背面外扩，黑色） --------
        Pass
        {
            Cull Front
            ZWrite On
            Lighting Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct VOut { float4 posCS:SV_POSITION; };

            VOut vert(AppData v)
            {
                // 先做海面位移，拿到世界空间位置
                Vary base = VertDisplace(v);

                // 把位移后的顶点转到视空间，拿视空间“法线方向”
                float4 posVS = mul(UNITY_MATRIX_V, mul(unity_ObjectToWorld, v.vertex));
                float3 nVS   = mul(UNITY_MATRIX_IT_MV, float4(v.normal,0)).xyz;
                nVS.z = _OutlineZBias;                 // 稳定向屏幕外的分量
                // 基于深度（视距）缩放描边：近粗远细（可按需改写为常量宽度）
                float depthScale = saturate((-posVS.z/posVS.w)*0.01);
                float width = min(_Outline, _Outline * depthScale);

                posVS += float4(normalize(nVS),0) * width;
                VOut o; o.posCS = mul(UNITY_MATRIX_P, posVS);
                return o;
            }

            fixed4 frag() : SV_Target { return fixed4(0,0,0,1); }
            ENDCG
        }

        // -------- Pass 2：主体（水墨着色） --------
        Pass
        {
            Cull Back
            ZWrite On
            // Lighting Off（我们自定义“水墨”着色，不走 Standard）

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            Vary vert(AppData v){ return VertDisplace(v); }

            fixed4 frag(Vary i) : SV_Target
            {
                // 1) 基底（可选）：主纹理*色调 → 灰度
                fixed3 baseCol = tex2D(_MainTex, i.uv).rgb * _Color.rgb;
                float  gray    = dot(baseCol, float3(0.33,0.33,0.33));

                // 2) 视角项（vdotn）→ 墨阶
                float3 V = normalize(i.vWS);
                float3 N = normalize(i.nWS);
                float  vdotn = saturate(dot(V, N));
                float  f = pow(vdotn, _InkPow);  // 大=正面更亮，小=掠射更重墨

                // 3) 深度 → 留白
                float depth01 = Linear01Depth(i.zwCS.x / i.zwCS.y);   // 0(近)~1(远)
                float  depthMask = tex2D(_DepthLUT, float2(depth01, 0.5)).r;

                // 4) Ramp/纸纹采样
                fixed4 inkCol;
                if (f < _InkSplit)
                {
                    float2 uvRamp = float2(f * (1.0/_InkSplit), (i.uv.x + i.uv.y)*0.5); // 黑墨：U=放大，V=纸纹
                    inkCol = tex2D(_BlackRamp, uvRamp);
                }
                else
                {
                    float t = (f - _InkSplit) / (1.0 - _InkSplit);
                    float2 uvRamp = float2(t, 0.5);
                    inkCol = tex2D(_GrayRamp, uvRamp);
                }

                // 5) 组合：灰度 * 墨色阶 * 深度留白
                inkCol.rgb *= depthMask;
                float3 outCol = gray.xxx * inkCol.rgb;

                return fixed4(outCol, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
