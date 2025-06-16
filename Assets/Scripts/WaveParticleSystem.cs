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
        //粒子数组
        private List<WaveParticle> particles;
        private ComputeBuffer particleBuffer;
        //private ComputeBuffer particleBuffer2;
        public int frameCnt;

        // 基础配置参数
        [Header("Wave Parameters")]
        public Vector2 windSpeed = new Vector2(5,0);
        public Vector2 swellSpeed = new Vector2(20, 0);
        //[Range(0, 20)] public float windSpeed = 5f;
        [SerializeField] float waterDepth = 100f;
        [SerializeField] public float maxRadius= 20f;
        [SerializeField] public int layerCnt = 1;
        [SerializeField] public int sampleStep = 100;
        [SerializeField] public float gravity = 9.81f;
        [SerializeField]
        [Tooltip("峰值周期")]
        public float Tp = 3.8f;//Tp
        [SerializeField]
        [Tooltip("有义波高")]
        public float Hs = 3.0f;//有义波高，默认3m
        [SerializeField] float oceanSize = 10f;
        [SerializeField] float fetchSize = 1000000f;


        [Header("Rendering")]
        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public RenderTexture heightMap;
        [SerializeField] public int resolution = 256;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);
        [SerializeField] float planeSize = 10f;

        [SerializeField] Material oceanMaterial;

        MeshUtils.Element OceanCenter;

        public UnityEngine.UI.RawImage heightMapDisplay;

        static double[] p = {0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7};

        void Awake()
        {
            particles = new List<WaveParticle>();
        }

        public void Start()
        {
            Debug.Log("WaveParticleSystem Start called!");
            //InitializeSimpleWave();
            particles.Clear();
            //particles = GenerateParticles(particleCnt, windSpeed);
            //particles = GenerateParticlesTest(count: 1, windSpeed: 10.0f);
            particles = GenerateParticlesBySpectrum();
            Debug.Log($"Generated {particles.Count} basic wave particles");
            // 初始化 ComputeBuffer
            particleBuffer = new ComputeBuffer(particles.Count, sizeof(float) * 4);

            textureSize = new Vector2Int(resolution, resolution);
            // 初始化 heightMap
            heightMap = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.RFloat);
            heightMap.enableRandomWrite = true;

            //采样模式改为双/三线性过滤
            heightMap.filterMode = FilterMode.Trilinear;  // 或者 Bilinear
            heightMap.wrapMode = TextureWrapMode.Clamp;

            heightMap.Create();

            if (heightMapComputeShader != null)
            {
                Debug.Log("有绑定 ComputeShader。");
                if (heightMapComputeShader.HasKernel("CSMain")) { 
                    Debug.Log("有csmain");
                };
            }
            frameCnt = 0;
            //instanceMaterial.SetTexture("_HeightMap", heightMap);
            //oceanMaterial.SetTexture("_HeightMap", heightMap);
            //oceanMaterial = new Material(oceanMaterial); // 克隆材质
            oceanMaterial.EnableKeyword("ONLY_CLOSE");
            // 禁用 FFT 级联 keyword
            oceanMaterial.DisableKeyword("MID");
            oceanMaterial.DisableKeyword("CLOSE");
            oceanMaterial.SetTexture("_ParticleHeightMap", heightMap);
            oceanMaterial.SetFloat("_ParticleHeightScale", 1.0f);

            // 1) 先生成高分辨率网格
            Mesh planeMesh = MeshUtils.CreateHighResPlaneMesh(
                resolution, resolution,
                planeSize, planeSize
            );

            // 2) 实例化到场景下，并返回 Transform/MeshRenderer 供后续使用
            OceanCenter = MeshUtils.InstantiateElement(
                "HighResPlane",
                planeMesh,
                oceanMaterial,
                this.transform    // 挂到当前 GameObject 下面
            );

        }
        public void Update()
        {
            frameCnt++;
            if (heightMapDisplay != null)
            { heightMapDisplay.texture = heightMap; }

            float deltaTime = Time.deltaTime;
            float time = Time.time;
            // 更新粒子位置和速度
            for (int i = 0; i < particles.Count; i++)
            {
                particles[i].Update(deltaTime,planeSize,oceanSize);
                //particles[i].UpdateParticle(time);
            }
            UpdateParticleBuffer();
            //Graphics.DrawMeshInstanced(ballMesh, 0, ballMaterial, GetMatrices(), particles.Count);

            // 发送到 ComputeShader
            int kernel = heightMapComputeShader.FindKernel("CSMain");
            heightMapComputeShader.SetTexture(kernel, "Result", heightMap);
            heightMapComputeShader.SetBuffer(kernel, "Particles", particleBuffer);
            heightMapComputeShader.SetInts("TextureSize", textureSize.x, textureSize.y);
            heightMapComputeShader.SetInt("ParticleCount", particles.Count);
            heightMapComputeShader.SetFloat("time", Time.time);
            heightMapComputeShader.SetFloat("OceanSize", oceanSize);
            heightMapComputeShader.SetFloat("PlaneSize", planeSize);

            int groupsX = Mathf.CeilToInt(textureSize.x / 8f);
            int groupsY = Mathf.CeilToInt(textureSize.y / 8f);
            heightMapComputeShader.Dispatch(kernel, groupsX, groupsY, 1);

            // 将高度图传递给水面材质
            //instanceMaterial.SetTexture("_HeightMap", heightMap);
        }

        private void OnDestroy()
        {
            if (particleBuffer != null)
            {
                particleBuffer.Release();
                particleBuffer = null;
            }
        }

        List<WaveParticle> GenerateParticlesBySpectrum()
        {
            
            var list = new List<WaveParticle>();

            // 根据分辨率与海洋大小 确定波矢量k取值范围 不应过高：多余细节；不应过低：影响太多全局
            float lambdaMax = 2 * oceanSize ;        // 最长波长
            float lambdaMin = oceanSize / resolution;  // 最短波长
            float kMin = 2 * Mathf.PI / lambdaMax;
            float kMax = 2 * Mathf.PI / lambdaMin;
            Debug.Log($"kMin: {kMin}, kMax: {kMax}");

            // 测试 计算谱能量 S
            /*for (float testk = 0.001f; testk < 0.1f; testk += 0.001f)
            {
                float testS = JONSWAPSpectrum(testk, dir, windSpeed, gravity, waterDepth, fetchSize);
                float testA = Mathf.Sqrt(2f * testS);
                float testR = Mathf.PI / testk;
                //Debug.Log($"testk: {testk}, testS: {testS}, testA: {testA}, testR: {testR}");
            }*/

            //TODO: K的采样，要在x,y方向完成
            //计算采样K范围：0.5Kp - 2.5 Kp
            float ωp = 22f * Mathf.Pow((gravity * gravity) / (windSpeed.magnitude * fetchSize), 0.333333f);
            float kp = ωp * ωp / gravity;
            float kMin_sample = 0.5f * kp;
            float kMax_sample = 2.5f * kp;

            for (int s = 0; s < sampleStep; s++) {
                // 采样角度方向 θ
                float theta = UnityEngine.Random.Range(0f, 2f * Mathf.PI);
                Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                // k 长度采样
                float k = UnityEngine.Random.Range(kMin, kMax);

                //wind sea 风浪计算
                Debug.Log($"k: {k}");

                float S = JONSWAPSpectrum(k, dir, windSpeed, gravity, waterDepth, fetchSize);
                Debug.Log($"S: {S}");
                // 振幅 A = sqrt(2 S)
                float A = Mathf.Sqrt(2f * S);//* dk
                Debug.Log($"A: {A}");
                // 波粒子半径及速度
                float radius = Mathf.PI / k;
                Debug.Log($"radius: {radius}");
                float phaseSpeed = Mathf.Sqrt(gravity / k);

                // 沿K方向，在海面上铺排1排、同频正/负波粒子
                // 1. 波长相关尺寸
                float spacing = 2f * radius; // 粒子间间距

                // 2. 计算沿方向 dir 和垂直方向 perp 各需要多少排
                int numStepsDir = Mathf.CeilToInt(oceanSize / spacing);
                int numStepsPerp = Mathf.CeilToInt(oceanSize / spacing);

                // 3. 构建方向向量（单位化）
                Vector2 dirUnit = dir.normalized;
                Vector2 perpUnit = new Vector2(-dir.y, dir.x).normalized;

                // 4. 中心偏移
                Vector2 center = Vector2.zero;

                for (int i = -numStepsDir / 2; i <= numStepsDir / 2; i++)
                {
                    for (int j = -numStepsPerp / 2; j <= numStepsPerp / 2; j++)
                    {
                        Vector2 pos = center + i * spacing * dirUnit + j * spacing * perpUnit;

                        // 限制在海洋区域内
                        if (Mathf.Abs(pos.x) > oceanSize * 0.5f || Mathf.Abs(pos.y) > oceanSize * 0.5f)
                            continue;

                        // 正波粒子
                        var p = new WaveParticle
                        {
                            position = pos,
                            direction = dirUnit,
                            baseHeight = A,
                            phase = 0,
                            angularFrequency = Mathf.Sqrt(gravity * k),
                            waveNumber = k,
                            radius = radius,
                            speed = phaseSpeed
                        };
                        list.Add(p);

                        // 负波粒子（可选）
                        var q = p.GetNegative(planeSize, oceanSize);
                        list.Add(q);
                    }
                }

                /*int rows = Mathf.CeilToInt(oceanSize / (2f * radius));
                Vector2 perp = new Vector2(-dir.y, dir.x); // 垂直风向
                for (int row = -rows; row <= rows; row++)
                {
                    Vector2 basePos = perp * row * 2f * radius;
                    // 如果超出边界就跳过
                    if (Mathf.Abs(basePos.x) > oceanSize * 0.5f || Mathf.Abs(basePos.y) > oceanSize * 0.5f)
                        continue;

                    // 正波粒子
                    var pPos = basePos;
                    var p = new WaveParticle
                    {
                        position = pPos,
                        direction = dir,
                        baseHeight = A,
                        phase = 0,
                        angularFrequency = Mathf.Sqrt(gravity * k),
                        waveNumber = k,
                        radius = radius,
                        speed = phaseSpeed
                    };
                    list.Add(p);

                    // 负波粒子
                    var q = p.GetNegative(planeSize, oceanSize);
                    list.Add(q);
                }*/

            }

            // swell
            /*float k2 = 0.03f;
            Debug.Log($"k2: {k2}");

            //得到对应速度。此处测试先以速度等于风向
            Vector2 dir2 = swellSpeed.normalized;

            // 计算谱能量 S
            for (float testk = 0.001f; testk < 0.5f; testk += 0.001f)
            {
                float testS = JONSWAPGlennSpectrum(testk, dir2, gravity);
                float testA = Mathf.Sqrt(2f * testS);
                float testR = Mathf.PI / testk;
                //Debug.Log($"testk: {testk}, testS: {testS}, testA: {testA}, testR: {testR}");
            }

            float S2 = JONSWAPGlennSpectrum(k2, dir2, gravity);
            Debug.Log($"S2: {S2}");
            float A2 = Mathf.Sqrt(2f * S2);//* dk
            Debug.Log($"A2: {A2}");
            // 波粒子半径及速度
            float radius2 = Mathf.PI / k2;
            Debug.Log($"radius: {radius2}");

            int rows2 = Mathf.CeilToInt(oceanSize / (2f * radius2));
            Vector2 perp2 = new Vector2(-dir2.y, dir2.x); // 垂直风向
            for (int row = -rows2; row <= rows2; row++)
            {
                Vector2 basePos = perp2 * row * 2f * radius2;
                // 如果超出边界就跳过
                if (Mathf.Abs(basePos.x) > oceanSize * 0.5f || Mathf.Abs(basePos.y) > oceanSize * 0.5f)
                    continue;

                // 正波粒子
                var pPos = basePos;
                var p = new WaveParticle
                {
                    position = pPos,
                    direction = dir2,
                    baseHeight = A2,
                    phase = 0,
                    angularFrequency = Mathf.Sqrt(gravity * k2),
                    waveNumber = k2,
                    radius = radius2,
                    speed = phaseSpeed
                };
                //list.Add(p);

                // 负波粒子
                //var q = p.GetNegative(planeSize, oceanSize);
                //list.Add(q);
            }*/

            return list;
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
            //Debug.Log($"fetch: { fetch},α: { α}, ω: { ω}, ωp: {ωp}");
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
            float g
        )
        {
            // 转成角频率 ω = sqrt(g k)
            float ω = Mathf.Sqrt(g * k);
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
            float U = Mathf.Sqrt(swellSpeed.x * swellSpeed.x + swellSpeed.y * swellSpeed.y);
            Vector2  swellDir = new Vector2(swellSpeed.x / U, swellSpeed.y / U);

            //应用方向夹角修正
            float θ = Mathf.Atan2(dir.y, dir.x) - Mathf.Atan2(swellDir.y, swellDir.x);
            if (Mathf.Abs(θ) > Mathf.PI)
            {
                θ = 2 * Mathf.PI - Mathf.Abs(θ);
            }
            float DirSpectrum = MyGammaDouble(sw + 1) / (2 * Mathf.Sqrt(Mathf.PI) * MyGammaDouble(sw + 0.5f)) * Mathf.Pow(Mathf.Cos(θ / 2), 2 * sw);
            //最后转换到sk
            float Sk = Sjg * DirSpectrum / (4.0f * Mathf.PI) * Mathf.Sqrt(g / k) / k;
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
        /// windSpeed 含义：x,y 分别是风向分量 * 风速大小
        /// </summary>
        List<WaveParticle> GenerateParticlesNew()
        {
            float g = 9.81f;
            float windSp = windSpeed.magnitude;       // 风速大小
            Vector2 windDir = windSpeed.normalized;   // 风向
            float omegaP = 0.84f * g / windSp;
            float alpha = 0.076f * Mathf.Pow(windSp * windSp / g, 0.22f);
            float gamma = 3.3f;

            float omegaMin = 0.2f * omegaP;
            float omegaMax = 2f * omegaP;
            float dΩ = (omegaMax - omegaMin) / sampleStep;

            var list = new List<WaveParticle>();

            // 分 layerCnt 层生成粒子
            for (int L = 0; L < layerCnt; L++)
            {
                // 随机在 sampleStep 份里选一个索引
                //0-sampleStep的随机数
                int idx = UnityEngine.Random.Range(0, sampleStep);
                // 归一化到 [0,1]
                float t = idx / (float)(sampleStep);
                // 插值得到频率 ω
                float ωc = Mathf.Lerp(omegaMin, omegaMax, t);
                ωc = 0.6f * omegaP ;
                Debug.Log(" ωc:" + ωc);
                // JONSWAP 计算谱密度 S，之后再算振幅 A
                float S = JONSWAP(ωc, omegaP, alpha, gamma, g, waterDepth);
                Debug.Log("S:" + S);
                float dOmega = (omegaMax - omegaMin) / sampleStep;
                float A = Mathf.Sqrt(2 * S);
                Debug.Log("A:" + A);
                // 波数 / 波长 / 半径 / 速度
                float k = ωc * ωc / g;
                float λ = 2f * Mathf.PI / k;
                float r = Mathf.Min(λ * 0.5f, maxRadius);
                float v = Mathf.Sqrt(g / k);

                // 计算需要铺多少排才能覆盖海域
                int rows = Mathf.CeilToInt(oceanSize / r);
                Vector2 perp = new Vector2(-windDir.y, windDir.x);

                for (int i = -rows / 2; i <= rows / 2; i++)
                {
                    Vector2 basePos = windDir * (i * r);
                    for (int j = -rows / 2; j <= rows / 2; j++)
                    {
                        Vector2 pos = basePos + perp * (j * r);
                        // 限制区域
                        if (Mathf.Abs(pos.x) > oceanSize / 2 || Mathf.Abs(pos.y) > oceanSize / 2)
                            continue;

                        // 正相位
                        var p = new WaveParticle();
                        p.position = pos;
                        p.direction = windDir;
                        p.baseHeight = A;
                        p.phase = 0;
                        p.angularFrequency = ωc;
                        p.waveNumber = k;
                        p.radius = r;
                        p.speed = v;
                        list.Add(p);

                        // 负相位
                        //var n = p.GetNegative();
                        //list.Add(n);
                    }
                }
            }
            Debug.Log("Generated particles count: " + list.Count());
            return list;
        }


        List<WaveParticle> GenerateParticles(int count, float windSpeed) //JONSWAP 适用 风速: 3m/s-20m/s
        {
            float g = 9.81f;
            float omegaP = 0.84f * g / windSpeed;
            float alpha = 0.076f * Mathf.Pow(windSpeed * windSpeed / g, 0.22f);
            float gamma = 3.3f;

            float omegaMin = omegaP * 0.2f;
            float omegaMax = omegaP * 5f;
            float dOmega = (omegaMax - omegaMin) / count;

            var list = new List<WaveParticle>(count);
            for (int i = 0; i < count; i++)
            {
                float ω = omegaMin + (i + 0.5f) * dOmega;
                float S = JONSWAP(ω, omegaP, alpha, gamma, g, waterDepth);
                float A = Mathf.Sqrt(2 * S * dOmega);
                //Debug.Log("高度：" + A);
                // 构造粒子
                var p = new WaveParticle();
                p.position = UnityEngine.Random.insideUnitCircle * 5f;
                p.direction = UnityEngine.Random.insideUnitCircle.normalized;
                p.baseHeight = A;
                p.phase = UnityEngine.Random.value * Mathf.PI * 2f;
                p.angularFrequency = ω;
                p.waveNumber = ω * ω / g;
                //p.radius = 2 * Mathf.PI / p.waveNumber;
                p.radius = Math.Min(2 * Mathf.PI / p.waveNumber,maxRadius);
                p.speed = Mathf.Sqrt(g / p.waveNumber);
                list.Add(p);

            }
            return list;
        }

        /// <summary>
        /// 带深度修正的 JONSWAP 频谱
        /// omega      — 当前频率
        /// omegaP     — 峰频
        /// alpha, γ   — JONSWAP 参数
        /// g          — 重力加速度
        /// depth      — 水体深度
        /// </summary>
        static float JONSWAP(float omega, float omegaP, float alpha, float gamma, float g, float depth)
        {
            // 1) 先算一个 r 项
            float sigma = omega <= omegaP ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow(omega - omegaP, 2) /
                                (2 * sigma * sigma * omegaP * omegaP));

            // 2) 基本的深水谱部分
            float baseSpectrum =
                alpha * g * g
                * Mathf.Pow(omega, -5)
                * Mathf.Exp(-1.25f * Mathf.Pow(omegaP / omega, 4))
                * Mathf.Pow(gamma, r);

            // 3) 加上 TMA 修正
            float tma = TMACorrection(omega, g, depth);

            return baseSpectrum * tma;
        }

        /// <summary>
        /// TMA 修正项：考虑有限深度对频谱的修正
        /// </summary>
        static float TMACorrection(float omega, float g, float depth)
        {
            // ωh = omega * sqrt(depth / g)
            float omegaH = omega * Mathf.Sqrt(depth / g);
            if (omegaH <= 1.0f)
                return 0.5f * omegaH * omegaH;
            else if (omegaH < 2.0f)
                return 1.0f - 0.5f * (2.0f - omegaH) * (2.0f - omegaH);
            else
                return 1.0f;
        }


        List<WaveParticle> GenerateParticlesTest(int count, float windSpeed)
        {
            float g = 9.81f;
            float omegaP = 0.84f * g / windSpeed;
            float alpha = 0.076f * Mathf.Pow(windSpeed * windSpeed / g, 0.22f);
            float gamma = 3.3f;

            float omegaMin = omegaP * 0.2f;
            float omegaMax = omegaP * 5f;
            float dOmega = (omegaMax - omegaMin) / count;

            var list = new List<WaveParticle>(count);
            for (int i = 0; i < count; i++)
            {
                float ω = omegaMin + (i + 0.5f) * dOmega;
                float S = JONSWAP(ω, omegaP, alpha, gamma, g, waterDepth);
                float A = Mathf.Sqrt(2 * S * dOmega);
                Debug.Log("高度：" + A);
                // 构造粒子
                var p = new WaveParticle();
                p.position = UnityEngine.Random.insideUnitCircle * 5f;
                p.direction = UnityEngine.Random.insideUnitCircle.normalized;
                p.baseHeight = A;
                p.phase = UnityEngine.Random.value * Mathf.PI * 2f;
                p.angularFrequency = ω;
                p.waveNumber = ω * ω / g;
                p.radius = 2 * Mathf.PI / p.waveNumber * 0.25f; //TODO： radius与波长的关系
                p.speed = Mathf.Sqrt(g / p.waveNumber) ;
                list.Add(p);
            }
            return list;
        }

        void UpdateParticleBuffer()
        {
            Vector4[] particleData = new Vector4[particles.Count];
            Vector4[] particleData2 = new Vector4[particles.Count];
            for (int i = 0; i < particles.Count; i++)
            {
                particleData[i] = particles[i].ToVector4();
            }

            particleBuffer.SetData(particleData);
            //instanceMaterial.SetBuffer("_ParticleBuffer", particleBuffer);

            //打印粒子信息
            Vector4[] debugData = new Vector4[particles.Count];
            particleBuffer.GetData(debugData);
            int p_cnt = 0;
            int p_cnt2 = 0;
            float highest = 0.0f;
            for (int i = 0; i < debugData.Count(); i++)
            {
                if (debugData[i].z < 0.1)
                {
                    continue;
                }
                if (debugData[i].z > highest)
                {
                    highest = debugData[i].z;
                }
                if (debugData[i].z > 0.1)
                {
                    if (debugData[i].w > oceanSize / 10)
                    {
                        p_cnt2++;
                    }
                }
                //Debug.Log($"Particle {i}: {debugData[i]}");
                p_cnt++;
            }
            //Debug.Log($"Total big particles: {p_cnt2}");
            //Debug.Log($"Highest particle height: {highest}");

        }
        Matrix4x4[] GetMatrices()
        {
            Matrix4x4[] matrices = new Matrix4x4[particles.Count];
            for (int i = 0; i < particles.Count; i++)
            {
                Vector3 pos = new Vector3(particles[i].position.x, particles[i].height, particles[i].position.y);
                matrices[i] = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one * 0.1f);
            }
            return matrices;
        }
    }


}
