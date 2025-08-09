using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Assets.Scripts
{
    public class WaveParticleSystem : MonoBehaviour
    {


        // 基础配置参数
        [Header("基础参数")]
        [SerializeField]
        public int fixedFrameCnt;
        public int particleCnt;
        public List<WaveParticleRegion> waveParticleRegions;
        public WavesSettings wavesSettings;
        public SpectrumToParticlesConverter converter = new SpectrumToParticlesConverter();
        [Header("最大渲染数量相关参数")]
        [SerializeField] public int MAX_REGIONS = 3;
        [SerializeField] public int MAX_PARTICLES = 1000000;
        [Header("采样相关参数")]
        [SerializeField] public int N_omega = 8;
        [SerializeField] public int N_theta = 8;
        [SerializeField]
        [Tooltip("峰值周期")]
        public float Tp = 3.8f;//Tp
        [SerializeField]
        [Tooltip("有义波高")]
        public float Hs = 3.0f;//有义波高，默认3m
        [SerializeField] float oceanSize = 100f;


        [Header("Rendering")]
        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public ComputeShader SplatComputeShader;
        [SerializeField] public ComputeShader Int2FloatComputeShader;
        [SerializeField] public ComputeShader DebugComputeShader;
        [SerializeField] public ComputeShader Height2NormalComputeShader;
        [SerializeField] public RenderTexture heightMapTest;
        [SerializeField] public RenderTexture[] heightMap;
        [SerializeField] public RenderTexture[] normalMap;
        [SerializeField] public RenderTexture[] displacementMap;
        [SerializeField] public ComputeBuffer[] particleBuffers;
        [SerializeField] public ComputeBuffer[] particleDirBuffers;
        [SerializeField] public int resolution = 256;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);
        [SerializeField] public float blendRange = 0.2f;
        [SerializeField] public float blendStrength = 0.5f;
        //[SerializeField] float planeSize = 10f;
        [SerializeField] public NativeArray<float>[] heightMaps;

        [SerializeField] Material oceanMaterial;

        Vector4[] particleData;
        Vector2[] particleDirData;

        MeshUtils.Element OceanCenter;
        [Header("Debug")]
        public int[] debugOut = new int[4];
        public UnityEngine.UI.RawImage heightMapDisplay;
        public UnityEngine.UI.RawImage normalMapDisplay;
        public UnityEngine.UI.RawImage displacementMapDisplay;

        private Texture2D[] heightMapT2D;
        private Texture2D[] normalMapT2D;
        private Texture2D[] displacementMapT2D;

        static double[] p = {0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7};

        void Awake()
        {
            //particles = new List<WaveParticle>();
            //waveParticleRegions = new List<WaveParticleRegion>();
        }

        public void Start()
        {
            
            Debug.Log("Graphics API: " + SystemInfo.graphicsDeviceType);
            Debug.Log("WaveParticleSystem Start called!");
            particleCnt = 0;
            particleData = new Vector4[MAX_PARTICLES];
            particleDirData = new Vector2[MAX_PARTICLES];
            Debug.Log($"Generated {waveParticleRegions.Count()} wave particle regions");
            if (waveParticleRegions.Count == 0)
            {
                Debug.LogWarning("No particle regions.");
            }

            textureSize = new Vector2Int(resolution, resolution);
            // 初始化 heightMap与normalMap
            heightMap = new RenderTexture[MAX_REGIONS];
            normalMap = new RenderTexture[MAX_REGIONS];
            displacementMap = new RenderTexture[MAX_REGIONS];
            particleBuffers = new ComputeBuffer[MAX_REGIONS];
            particleDirBuffers = new ComputeBuffer[MAX_REGIONS];
            heightMapT2D = new Texture2D[MAX_REGIONS];
            normalMapT2D = new Texture2D[MAX_REGIONS];
            displacementMapT2D = new Texture2D[MAX_REGIONS];
            for (int i = 0; i < Math.Min(waveParticleRegions.Count, MAX_REGIONS); i++)
            {
                waveParticleRegions[i].particles = new NativeList<WaveParticle>(Allocator.Persistent);
                // HeightMap
                heightMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat);
                heightMap[i].enableRandomWrite = true;
                heightMap[i].Create();

                // NormalMap
                normalMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
                normalMap[i].enableRandomWrite = true;
                normalMap[i].Create();

                //DisplacementMap
                displacementMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
                displacementMap[i].enableRandomWrite = true;
                displacementMap[i].Create();

                heightMapT2D[i] = new Texture2D(resolution, resolution, TextureFormat.RFloat, false);
                normalMapT2D[i] = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false);
                displacementMapT2D[i] = new Texture2D(resolution, resolution, TextureFormat.RGFloat, false);

                particleBuffers[i] = new ComputeBuffer(MAX_PARTICLES, sizeof(float) * 4);
                particleDirBuffers[i] = new ComputeBuffer(MAX_PARTICLES, sizeof(float) * 2);
            }

            heightMaps = new NativeArray<float>[N_omega];
            for (int b = 0; b < N_omega; b++) {
                heightMaps[b] = new NativeArray<float>(resolution * resolution, Allocator.Persistent);
            }
                
            heightMapTest = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RInt);
            heightMapTest.enableRandomWrite = true;
            heightMapTest.Create();
            
            if (heightMapComputeShader != null)
            {
                Debug.Log("有绑定 ComputeShader。");
                if (heightMapComputeShader.HasKernel("CSMain")) { 
                    Debug.Log("有csmain");
                };
            }
            fixedFrameCnt = 0;
            converter.Initialize(N_omega);
            oceanMaterial.SetFloat("_ParticleHeightScale", 1.0f);

        }
        public void FixedUpdate()
        {
            fixedFrameCnt++;
            //Debug.Log("begin updating particles");
            float deltaTime = Time.deltaTime;
            //Debug.Log(deltaTime);
            //float time = Time.time;
            particleCnt = 0;
            oceanMaterial.SetInt("_RegionCount", waveParticleRegions.Count);
            oceanMaterial.SetFloat("_BlendRange",blendRange);
            oceanMaterial.SetFloat("_BlendStrength",blendStrength);
            
            Graphics.SetRenderTarget(heightMapTest);
            GL.Clear(false, true, Color.clear);
            Graphics.SetRenderTarget(null);
            for (int i = 0; i < waveParticleRegions.Count; i++)
            {
                var region = waveParticleRegions[i];
                // 按需生成新粒子（你的边缘逻辑）
                /*var edgeParticles = converter.GenerateParticlesFromSpectrum(
                            ws: wavesSettings,
                            regionCenter: region.center,
                            regionSize: region.size,
                            sampleCount: sampleCount);*/
                NativeList<WaveParticle> edgeParticles = converter.GenerateParticlesFromSpectrum(
                            ws: wavesSettings,
                            regionCenter: region.center,
                            regionSize: region.size,
                            N_omega: N_omega,
                            N_theta: N_theta,
                            deltaTime: deltaTime);
                waveParticleRegions[i].particles.AddRange(edgeParticles.AsArray());
                edgeParticles.Dispose();
                // 1. 更新粒子 最卡
                UpdateRegionParticles(i, deltaTime);

                // 2. 更新ComputeBuffer
                UpdateRegionParticleBuffer(i);

                // 3. 发送到ComputeShader
                //DispatchRegionComputeShader(i);

                int kernel = SplatComputeShader.FindKernel("SplatParticles");
                SplatComputeShader.SetBuffer(kernel, "_Particles", particleBuffers[i]);
                SplatComputeShader.SetInt("_ParticleCount", particleCnt);
                SplatComputeShader.SetTexture(kernel, "_HeightMap", heightMapTest);
                SplatComputeShader.SetInts("_TexSize", resolution, resolution);
                SplatComputeShader.SetVector("_RegionCenter", waveParticleRegions[i].center);
                SplatComputeShader.SetVector("_RegionSize", waveParticleRegions[i].size);
                int threadGroups = Mathf.CeilToInt((float)particleCnt / 64f); // 64和你的[numthreads(64,1,1)]一致
                SplatComputeShader.Dispatch(kernel, threadGroups, 1, 1);


                var debugResultBuffer = new ComputeBuffer(4, sizeof(int));
                int[] initVals = { int.MaxValue, int.MinValue, 0, 0 };
                debugResultBuffer.SetData(initVals);
                int debugKernel = DebugComputeShader.FindKernel("DebugHeightMap");
                DebugComputeShader.SetTexture(debugKernel, "_HeightMap", heightMapTest);  // 你的int纹理
                DebugComputeShader.SetInts("_TexSize", resolution, resolution);
                DebugComputeShader.SetBuffer(debugKernel, "_DebugResult", debugResultBuffer);
                DebugComputeShader.Dispatch(debugKernel, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);
                debugResultBuffer.GetData(debugOut);


                int kernel2 = Int2FloatComputeShader.FindKernel("IntToFloat");
                Int2FloatComputeShader.SetTexture(kernel2, "Source", heightMapTest);
                Int2FloatComputeShader.SetTexture(kernel2, "Dest", heightMap[i]);
                Int2FloatComputeShader.SetInts("TexSize", resolution, resolution);
                Int2FloatComputeShader.SetFloat("Scale", 1000f);
                Int2FloatComputeShader.Dispatch(kernel2, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                int kernel3 = Height2NormalComputeShader.FindKernel("CSMain");
                Height2NormalComputeShader.SetTexture(kernel3, "HeightTex", heightMap[i]);
                Height2NormalComputeShader.SetTexture(kernel3, "NormalTex", normalMap[i]);
                Height2NormalComputeShader.SetInts("TexSize", resolution, resolution);
                Height2NormalComputeShader.Dispatch(kernel3, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                oceanMaterial.SetTexture($"_ParticleHeightMap{i}", heightMap[i]);
                oceanMaterial.SetTexture($"_ParticleNormalMap{i}", normalMap[i]);
                oceanMaterial.SetVector($"_RegionCenter{i}", waveParticleRegions[i].center);
                oceanMaterial.SetVector($"_RegionSize{i}", waveParticleRegions[i].size);
            }


            //particleCnt = allParticles.Count();
            if (heightMapDisplay != null)
            { heightMapDisplay.texture = heightMap[0]; }
            if (normalMapDisplay != null)
            { normalMapDisplay.texture = normalMap[0]; }
            if (displacementMapDisplay != null)
            { displacementMapDisplay.texture = displacementMap[0]; }
            
        }

        void UpdateRegionParticles(int idx, float deltaTime)
        {
            var region = waveParticleRegions[idx];
            var particles = region.particles; // NativeList<WaveParticle>
            int count = particles.Length;
            if (count == 0) return;

            // 并行Job更新
            var updateJob = new WaveParticleUpdateJob
            {
                particles = particles.AsArray(), // NativeArray
                deltaTime = deltaTime,
                planeSize = region.size.x,
                oceanSize = oceanSize 
            };

            var handle = updateJob.Schedule(count, 64);
            handle.Complete();

            // 主线程遍历移除越界粒子（只能主线程做）
            for (int i = particles.Length - 1; i >= 0; i--)
            {
                var p = particles[i];
                if (!region.Contains(p.position, p.radius))
                    particles.RemoveAt(i);
            }

            particleCnt += particles.Length;
        }


        void UpdateRegionParticleBuffer(int idx)
        {
            int count = waveParticleRegions[idx].particles.Length;
            /*if (particleBuffers[idx] != null)
            {
                particleBuffers[idx].Release();
                particleDirBuffers[idx].Release();
            }
            if (count == 0)
            {
                particleBuffers[idx] = null;
                particleDirBuffers[idx] = null;
                return;
            }*/
            for (int i = 0; i < count; i++)
            {
                particleData[i] = waveParticleRegions[idx].particles[i].ToVector4();
                particleDirData[i] = waveParticleRegions[idx].particles[i].ToVector2();
            }
            //particleBuffers[idx] = new ComputeBuffer(count, sizeof(float) * 4);
            particleBuffers[idx].SetData(particleData, 0, 0, count);
            //particleDirBuffers[idx] = new ComputeBuffer(count, sizeof(float) * 2);
            //particleDirBuffers[idx].SetData(particleDirData, 0, 0, count);
            
        }
        void DispatchRegionComputeShader(int idx)
        {
            if (particleBuffers[idx] == null) return;
            int kernel = heightMapComputeShader.FindKernel("CSMain");
            heightMapComputeShader.SetTexture(kernel, "Result", heightMap[idx]);
            heightMapComputeShader.SetTexture(kernel, "NormalResult", normalMap[idx]);
            heightMapComputeShader.SetTexture(kernel, "DisplacementResult", displacementMap[idx]);
            heightMapComputeShader.SetBuffer(kernel, "Particles", particleBuffers[idx]);
            heightMapComputeShader.SetBuffer(kernel, "ParticlesDir", particleDirBuffers[idx]);
            heightMapComputeShader.SetInts("TextureSize", resolution, resolution);
            heightMapComputeShader.SetInt("ParticleCount", waveParticleRegions[idx].particles.Length);
            heightMapComputeShader.SetVector("RegionCenter", waveParticleRegions[idx].center);
            heightMapComputeShader.SetVector("RegionSize", waveParticleRegions[idx].size);

            int groupsX = Mathf.CeilToInt(resolution / 8f);
            int groupsY = Mathf.CeilToInt(resolution / 8f);
            heightMapComputeShader.Dispatch(kernel, groupsX, groupsY, 1);
        }

        private void OnDestroy()
        {
            foreach (var buf in particleBuffers)
                if (buf != null) buf.Release();
            foreach (var tex in heightMap)
                if (tex != null) tex.Release();
            foreach (var tex in normalMap)
                if (tex != null) tex.Release();
        }

        /// <summary>
        /// 计算带方向和深度修正的 JONSWAP 频谱密度 S(k,dir) 风浪Wind Wave用
        /// </summary>
        /// <param name="k">波数大小</param>
        /// <param name="dir">波数方向（单位向量）</param>
        /// <param name="windVec">风速矢量（含大小和方向）</param>
        /// <param name="g">重力加速度（9.81）</param>
        /// <param name="depth">水深</param>
        /// <param name="fetch">有效风区长度（可用海面长度代替）</param>
        public float JONSWAPSpectrum(
            float k,
            Vector2 dir,
            Vector2 windVec,
            float g,
            float depth,
            float fetch
        )
        {
            // 转成角频率 ω = sqrt(g k)
            float ω = Mathf.Sqrt(g * k);

            float U = windVec.magnitude;
            //Debug.Log($"U: {U}");

            // 计算谱无方向部分 Sjw(ω)
            float α = 0.076f * Mathf.Pow((U * U) / (g * fetch), 0.22f);
            //float ωp = 0.84f * g / Mathf.Max(U, 0.1f);
            float ωp = 22f * Mathf.Pow((g * g) / (U * fetch), 0.333333f);
            float γ = 3.3f;
            float σ = (ω <= ωp) ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow((ω - ωp), 2f) / (2f * σ * σ * ωp * ωp));
            float S0 = (α * g * g) / Mathf.Pow(ω, 5f)
                     * Mathf.Exp(-1.25f * Mathf.Pow(ωp / ω, 4f))
                     * Mathf.Pow(γ, r);
            //Debug.Log("S0: "+ S0);
            //return S0;

            // 有限深度 TMA 修正
            float ωh = ω * Mathf.Sqrt(depth / g);
            float TMA = ωh <= 1f
                ? 0.5f * ωh * ωh
                : (ωh < 2f
                   ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f)
                   : 1f);

            float S_deep = S0 * TMA;
            //Debug.Log($"S_deep: {S_deep}");
            //return S_deep;

            // 方向性修正 D(θ)
            //    θ = 波向 与 风向 夹角
            float θ = Vector2.SignedAngle(windVec.normalized, dir) * Mathf.Deg2Rad;
            //    一般用 cos^n 展开，指数 n 随 ω/ωp 而变化
            float μ = (ω <= ωp) ? 5f : -2.5f;
            float n = 16f * Mathf.Pow(ω / ωp, μ);
            float D = (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(Mathf.Cos(θ / 2f), n);
            //Debug.Log($"D: {D}");
            // 6. 转换到 S(k) = S(ω) · (dω/dk) = S_deep · (1/2) sqrt(g/k)
            float domega_dk = 0.5f * Mathf.Sqrt(g / k);
            //Debug.Log($"domega_dk: {domega_dk}");
            return S_deep * D * domega_dk;
        }
        /// <summary>
        /// 计算带方向和深度修正的 JONSWAPGlenn 频谱密度 S(k,dir) 涌浪Swell用
        /// </summary>
        /// <param name="k">波数大小</param>
        /// <param name="dir">波数方向（单位向量）</param>
        /// <param name="g">重力加速度（9.81）</param>
        /// <param name="depth">水深</param>
        public float JONSWAPGlennSpectrum(
            float k,
            Vector2 dir,
            WavesSettings ws
        )
        {
            // 转成角频率 ω = sqrt(g k)
            float ω = Mathf.Sqrt(ws.g * k);
            float f = ω / (2f * Mathf.PI);
            float fp = 1 / Tp;
            //float γ = 3.3f;
            float γjg = 9.5f * Mathf.Pow(Hs, 0.34f) * fp;
            float σ = (f <= fp) ? 0.07f : 0.09f;
            //float ωp = 0.84f * g / Mathf.Max(U, 0.1f);
            float cc = 1.15f + 0.1688f * γjg - 0.925f / (1.909f + γjg);
            float c = (5f * Hs * Hs) / (16f * fp) * Mathf.Pow(cc, -1f);
            float r = Mathf.Exp(-1 * (f - fp) * (f - fp) / (2 * σ * σ * fp * fp));
            float Sjg = c * Mathf.Pow(f / fp, -5f) * Mathf.Exp(-5.0f / 4.0f * Mathf.Pow(f / fp, -4f)) * Mathf.Pow(γjg, r);
            //return Sjg;

            float μ = f > fp ? -2.5f : 5.0f;
            float sw = 16.0f * Mathf.Pow(f / fp, μ);
            float U = Mathf.Sqrt(ws.swell.windSpeed.x * ws.swell.windSpeed.x + ws.swell.windSpeed.y * ws.swell.windSpeed.y);
            Vector2  swellDir = new Vector2(ws.swell.windSpeed.x / U, ws.swell.windSpeed.y / U);

            //应用方向夹角修正
            float θ = Mathf.Atan2(dir.y, dir.x) - Mathf.Atan2(swellDir.y, swellDir.x);
            if (Mathf.Abs(θ) > Mathf.PI)
            {
                θ = 2 * Mathf.PI - Mathf.Abs(θ);
            }
            float DirSpectrum = MyGammaDouble(sw + 1) / (2 * Mathf.Sqrt(Mathf.PI) * MyGammaDouble(sw + 0.5f)) * Mathf.Pow(Mathf.Cos(θ / 2), 2 * sw);
            //最后转换到sk
            float Sk = Sjg * DirSpectrum / (4.0f * Mathf.PI) * Mathf.Sqrt(ws.g / k) / k;
            return (float)Sk;
        }

        /// <summary>
        /// 用来计算 Γ(z)（Gamma 函数）的近似值,实现了一个 Lanczos 近似
        /// </summary>
        private float MyGammaDouble(float z)
        {
            int g = 7;
            if (z < 0.5)
                return Mathf.PI / (Mathf.Sin(Mathf.PI * z) * MyGammaDouble(1 - z));
            z -= 1;
            float x = (float)p[0];
            for (var i = 1; i < g + 2; i++)
                x += (float)p[i] / (z + i);
            float t = z + g + 0.5f;
            return Mathf.Sqrt(2 * Mathf.PI) * (Mathf.Pow(t, z + 0.5f)) * Mathf.Exp(-t) * x;
        }


        /// <summary>
        /// 采样扩展水面位置和法线
        /// </summary>
        public Vector3 SampleWaterSurfacePositionAndNormal(int regionIdx, Vector2 worldXZ, out Vector3 normal)
        {
            normal = Vector3.up; // 默认向上

            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count)
            {
                Debug.LogWarning("regionIdx越界");
                return new Vector3(worldXZ.x, 0, worldXZ.y);
            }
            var region = waveParticleRegions[regionIdx];

            // UV映射
            Vector2 uv = (worldXZ - region.center) / region.size + Vector2.one * 0.5f;
            uv.x = Mathf.Clamp01(uv.x);
            uv.y = Mathf.Clamp01(uv.y);

            // 采样高度
            float height = 0;
            if (heightMapT2D[regionIdx] != null)
                height = heightMapT2D[regionIdx].GetPixelBilinear(uv.x, uv.y).r;
            else
                Debug.LogWarning("未找到对应的heightMapT2D");

            // 采样横向分量
            Vector2 disp = Vector2.zero;
            if (displacementMapT2D != null && displacementMapT2D[regionIdx] != null)
            {
                Color dispColor = displacementMapT2D[regionIdx].GetPixelBilinear(uv.x, uv.y);
                disp = new Vector2(dispColor.r, dispColor.g);
                disp = disp * 2f - Vector2.one; // 变换回[-1,1]
                disp = Vector2.Scale(disp, region.size);
            }

            // 采样法线
            if (normalMapT2D != null && normalMapT2D[regionIdx] != null)
            {
                Color nCol = normalMapT2D[regionIdx].GetPixelBilinear(uv.x, uv.y);
                normal = new Vector3(nCol.r, nCol.g, nCol.b) * 2f - Vector3.one;
                normal.Normalize();
            }
            else
            {
                normal = Vector3.up;
            }

            return new Vector3(worldXZ.x + disp.x, region.center.y + height, worldXZ.y + disp.y);
        }



        /// <summary>
        /// 在 regionIdx 对应区域，从 pos 位置往外环形生成一圈波粒子，
        /// 粒子的振幅/速度与体积变化 deltaV 成正比，方向均匀覆盖一圈。
        /// </summary>
        public void GenerateWaveParticles(int regionIdx, Vector2 pos, float deltaV, int objSampleCount = 16)
        {
            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count) return;

            // deltaV 决定基础振幅、波动速度
            // 你可根据需要调整这些“物理参数”的线性系数
            float K1 = 0.1f;
            float baseAmplitude = deltaV * 0.03f; // 或直接 *某个缩放
            float baseSpeed = Mathf.Abs(deltaV) * 0.5f; // 越大越快
            float radius = baseSpeed * baseSpeed * Mathf.PI / wavesSettings.g; // 影响半径
            //Debug.Log("r:"+radius);

            // 一圈 objSampleCount 个方向
            for (int i = 0; i < objSampleCount; i++)
            {
                float angle = i * Mathf.PI * 2.0f / objSampleCount;
                Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

                var particle = new WaveParticle()
                {
                    position = pos + 2f * dir * radius, // 从采样点为绕一圈发出
                    height = baseAmplitude,
                    direction = dir.normalized,    // 朝外发射
                    speed = baseSpeed,             // 传播速度
                    radius = radius,               // 粒子影响半径
                    omega = baseSpeed / radius, // 可根据波速和半径自定义
                    k = 2 * Mathf.PI / radius     // 可按半径对应的k算
                };
                waveParticleRegions[regionIdx].particles.Add(particle);
            }
        }


    }


}
