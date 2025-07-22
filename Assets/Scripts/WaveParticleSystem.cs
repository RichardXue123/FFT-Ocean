using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts
{
    public class WaveParticleSystem : MonoBehaviour
    {
        [Header("Regions & Settings")]
        [SerializeField] public int MAX_REGIONS = 1;
        public List<WaveParticleRegion> waveParticleRegions;
        public int maxParticleCount;
        public WavesSettings wavesSettings;
        [SerializeField] public int sampleCount = 100;

        [Header("Compute Shader")]
        [SerializeField] ComputeShader waveParticleCompute;

        [Header("Rendering")]
        [SerializeField] int resolution = 512;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(512, 512);
        [SerializeField] RenderTexture[] heightMap;
        [SerializeField] RenderTexture[] normalMap;
        [SerializeField] RenderTexture[] displacementMap;
        [SerializeField] Material oceanMaterial;

        // Per-region GPU buffers
        ComputeBuffer[] particleBuffers;
        ComputeBuffer[] spawnCounterBuffers;
        ComputeBuffer[] freeListBuffers;
        ComputeBuffer aliveCountBuffer;
        int countKernel;

        int initKernel, spawnKernel, updateKernel, renderKernel;

        public int frameCnt;
        public int particleCnt;

        // 基础配置参数
        [Header("Wave Parameters")]
        [Tooltip("峰值周期")]
        public float Tp = 3.8f;//Tp
        [SerializeField]
        [Tooltip("有义波高")]
        public float Hs = 3.0f;//有义波高，默认3m
        [SerializeField] float oceanSize = 100f;
        [SerializeField] float fetchSize = 1000000f;


        [Header("Blend Rendering")]
        //[SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public float blendRange = 0.2f;
        [SerializeField] public float blendStrength = 0.5f;



        MeshUtils.Element OceanCenter;

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
            particleCnt = 0;
            Debug.Log("WaveParticleSystem Start called!");
            Debug.Log($"Generated {waveParticleRegions.Count()} wave particle regions");

            int regionCount = waveParticleRegions.Count;
            particleBuffers = new ComputeBuffer[regionCount];
            spawnCounterBuffers = new ComputeBuffer[regionCount];
            freeListBuffers = new ComputeBuffer[regionCount];

            // Find kernels once
            initKernel = waveParticleCompute.FindKernel("InitParticles");
            spawnKernel = waveParticleCompute.FindKernel("SpawnFromSpectrum");
            updateKernel = waveParticleCompute.FindKernel("UpdateParticles");
            renderKernel = waveParticleCompute.FindKernel("RenderParticles");
            countKernel = waveParticleCompute.FindKernel("CountAliveParticles");

            // Allocate RenderTextures

            // 分配一个长度为 1 的 buffer
            aliveCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            heightMap = new RenderTexture[regionCount];
            normalMap = new RenderTexture[regionCount];
            displacementMap = new RenderTexture[regionCount];
            heightMapT2D = new Texture2D[MAX_REGIONS];
            normalMapT2D = new Texture2D[MAX_REGIONS];
            displacementMapT2D = new Texture2D[MAX_REGIONS];
            for (int i = 0; i < Math.Min(waveParticleRegions.Count, MAX_REGIONS); i++)
            {
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
            }

            textureSize = new Vector2Int(resolution, resolution);

            // Allocate and init per-region buffers
            for (int i = 0; i < regionCount; i++)
            {
                // 1) 粒子数据池
                particleBuffers[i] = new ComputeBuffer(maxParticleCount,
                    sizeof(float) * (2 + 2 + 1 + 1 + 1 + 1) + sizeof(int));
                // float2 pos, float2 dir, float height, float radius, float speed, float waveNumber, int alive

                // 2) spawnCounterBuffer[3]
                spawnCounterBuffers[i] = new ComputeBuffer(3, sizeof(uint));
                // 初始都设为 0
                spawnCounterBuffers[i].SetData(new uint[] { 0, 0, 0 });

                // 3) freeListBuffer 存放空闲槽位
                freeListBuffers[i] = new ComputeBuffer(MAX_REGIONS, sizeof(uint));

                // 初始化 GPU 粒子池：所有 alive=0
                waveParticleCompute.SetBuffer(initKernel, "particleBuffer", particleBuffers[i]);
                waveParticleCompute.SetBuffer(initKernel, "spawnCounterBuffer", spawnCounterBuffers[i]);
                waveParticleCompute.SetBuffer(initKernel, "freeListBuffer", freeListBuffers[i]);
                waveParticleCompute.SetInt("maxParticleCount", maxParticleCount);
                int groups = Mathf.CeilToInt(MAX_REGIONS / 256f);
                waveParticleCompute.Dispatch(initKernel, groups, 1, 1);
            }

            if (waveParticleCompute != null)
            {
                Debug.Log("有绑定 waveParticleCompute。");
            }
            frameCnt = 0;
            // 禁用 FFT 级联 keyword
            oceanMaterial.SetFloat("_ParticleHeightScale", 1.0f);

        }
        public void Update()
        {
            frameCnt++;
            float dt = Time.deltaTime;
            float t = Time.time;
            int regionCount = waveParticleRegions.Count;
            for (int i = 0; i < regionCount; i++)
            {
                var region = waveParticleRegions[i];

                // -------------------------
                // 1) SpawnFromSpectrum
                // -------------------------
                // 设置 buffers
                //waveParticleCompute.SetBuffer(spawnKernel, "particleBuffer", particleBuffers[i]);
                waveParticleCompute.SetBuffer(spawnKernel, "spawnCounterBuffer", spawnCounterBuffers[i]);
                waveParticleCompute.SetBuffer(spawnKernel, "freeListBuffer", freeListBuffers[i]);
                // 设置常量
                waveParticleCompute.SetInt("maxParticleCount", maxParticleCount);
                waveParticleCompute.SetFloat("deltaTime", dt);
                waveParticleCompute.SetFloat("globalTime", t);
                waveParticleCompute.SetFloats("regionCenter", region.center.x, region.center.y);
                waveParticleCompute.SetFloats("regionSize", region.size.x, region.size.y);
                waveParticleCompute.SetInt("sampleCount", sampleCount);
                waveParticleCompute.SetFloat("g", wavesSettings.g);
                waveParticleCompute.SetFloat("depth", wavesSettings.depth);
                waveParticleCompute.SetFloat("fetch", wavesSettings.local.fetch);
                waveParticleCompute.SetFloats("windSpeed",
                    wavesSettings.local.windSpeed.x,
                    wavesSettings.local.windSpeed.y);
                waveParticleCompute.SetFloat("peakOmega", wavesSettings.spectrums[0].peakOmega);
                // Dispatch
                int spawnGroups = Mathf.CeilToInt(sampleCount / 256f);
                waveParticleCompute.Dispatch(spawnKernel, spawnGroups, 1, 1);

                // -------------------------
                // 2) UpdateParticles
                // -------------------------
                //waveParticleCompute.SetBuffer(updateKernel, "particleBuffer", particleBuffers[i]);
                waveParticleCompute.SetBuffer(updateKernel, "spawnCounterBuffer", spawnCounterBuffers[i]);
                waveParticleCompute.SetBuffer(updateKernel, "freeListBuffer", freeListBuffers[i]);
                waveParticleCompute.SetInt("maxParticleCount", maxParticleCount);
                waveParticleCompute.SetFloat("deltaTime", dt);
                waveParticleCompute.SetFloats("regionCenter", region.center.x, region.center.y);
                waveParticleCompute.SetFloats("regionSize", region.size.x, region.size.y);
                int updateGroups = Mathf.CeilToInt(maxParticleCount/ 256f);
                waveParticleCompute.Dispatch(updateKernel, updateGroups, 1, 1);

                // -------------------------
                // 3) RenderParticles
                // -------------------------
                //waveParticleCompute.SetBuffer(renderKernel, "particleBuffer", particleBuffers[i]);
                waveParticleCompute.SetInt("maxParticleCount", maxParticleCount);
                waveParticleCompute.SetInts("TextureSize", resolution, resolution);
                waveParticleCompute.SetFloats("regionCenter", region.center.x, region.center.y);
                waveParticleCompute.SetFloats("regionSize", region.size.x, region.size.y);
                waveParticleCompute.SetTexture(renderKernel, "Result", heightMap[i]);
                waveParticleCompute.SetTexture(renderKernel, "NormalResult", normalMap[i]);
                waveParticleCompute.SetTexture(renderKernel, "DisplacementResult", displacementMap[i]);
                int gx = Mathf.CeilToInt(resolution / 8f);
                int gy = gx;
                waveParticleCompute.Dispatch(renderKernel, gx, gy, 1);

                oceanMaterial.SetTexture($"_ParticleHeightMap{i}", heightMap[i]);
                oceanMaterial.SetTexture($"_ParticleNormalMap{i}", normalMap[i]);
                oceanMaterial.SetTexture($"_ParticleDisplacementMap{i}", displacementMap[i]);
                oceanMaterial.SetVector($"_RegionCenter{i}", waveParticleRegions[i].center);
                oceanMaterial.SetVector($"_RegionSize{i}", waveParticleRegions[i].size);
            }

            // 1) 每帧先把 aliveCountBuffer 清零
            aliveCountBuffer.SetData(new uint[] { 0u });

            // 2) 绑定参数并 Dispatch
            //waveParticleCompute.SetBuffer(countKernel, "particleBuffer", particleBuffers[0]);
            waveParticleCompute.SetBuffer(countKernel, "aliveCountBuffer", aliveCountBuffer);
            waveParticleCompute.SetInt("maxParticleCount", maxParticleCount);
            int groups = Mathf.CeilToInt(maxParticleCount / 256f);
            waveParticleCompute.Dispatch(countKernel, groups, 1, 1);

            // 3) 读回结果（注意这会 stall CPU，但数量很小开销可接受）
            uint[] result = new uint[1];
            aliveCountBuffer.GetData(result);
            particleCnt = (int)result[0];

            //particleCnt = allParticles.Count();
            if (heightMapDisplay != null)
            { heightMapDisplay.texture = heightMap[0]; }
            if (normalMapDisplay != null)
            { normalMapDisplay.texture = normalMap[0]; }
            if (displacementMapDisplay != null)
            { displacementMapDisplay.texture = displacementMap[0]; }
            
        }

        private void OnDestroy()
        {
            foreach (var buf in particleBuffers) buf?.Release();
            foreach (var buf in spawnCounterBuffers) buf?.Release();
            foreach (var buf in freeListBuffers) buf?.Release();
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



        public void AddWaveParticle(int regionIdx, Vector2 pos, float amplitude, Vector2 dir)
        {
            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count) return;
            var particle = new WaveParticle()
            {
                position = pos,
                height = amplitude,
                direction = dir.normalized,
                speed = 5.0f,
            };
        }

    }


}
