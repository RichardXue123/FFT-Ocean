using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using System.Globalization;
using Debug = UnityEngine.Debug;

namespace Assets.Scripts
{
    public class WaveParticleSystem : MonoBehaviour
    {

        [Header("Object")]
        List<SolidHydrodynamics> solids;

        // 基础配置参数
        [Header("初始化播种")]
        [SerializeField] bool seedOnStart = true;   // 是否在 Start() 首帧播种

        [Header("基础参数")]
        [SerializeField]
        public int fixedFrameCnt;
        public int particleCnt;
        public List<WaveParticleRegion> waveParticleRegions;
        public WavesSettings wavesSettings;
        public SpectrumToParticlesConverter converter = new();

        [Header("最大渲染数量相关参数")]
        [SerializeField] public int MAX_REGIONS = 3;
        [SerializeField] public int MAX_PARTICLES = 1000000;
        [Header("采样相关参数")]
        [SerializeField] public int N_omega = 8;
        [SerializeField] public int N_theta = 8;

        [Header("buckets相关参数")]
        private RenderTexture[] heightSlicesInt;   // Tex2DArray: RInt
        private RenderTexture[] heightSlicesFloat; // Tex2DArray: RFloat (滤波输入/输出)
        private RenderTexture[] heightSlicesTmp; // 滤波 ping-pong 用
        //private RenderTexture[] displacementSlices; // 横向分量 Tex2DArray：RGFloat
        //private RenderTexture[] velocityXSlicesInt; // 横向分量 X RFloat
        //private RenderTexture[] velocityYSlicesInt; // 横向分量 Y RFloat

        private ComputeBuffer[][] particleBuffersPerBucket; // [region][bucket]
        private ComputeBuffer[][] particleVelBuffersPerBucket; // [region][bucket]
        public int[] bucketCounts;                         // 复用计数缓存
        public float[] bucketRadii;                        // 每桶代表半径(世界单位)
        public float[] bucketOmega;
        public float[] deltaOmega;
        private ComputeBuffer radiiBuf;

        [Header("Rendering")]
        [SerializeField] public ComputeShader SplatBucketsCS;
        [SerializeField] public ComputeShader Int2FloatArrayCS;
        [SerializeField] public ComputeShader SeparableFilterCS;   // 横/纵两个kernel
        [SerializeField] public ComputeShader ReduceSlicesCS;      // 合并 slices → RFloat
        [SerializeField] public ComputeShader Height2NormalComputeShader;
        [SerializeField] public RenderTexture[] heightMap;
        [SerializeField] public RenderTexture[] normalMap;
        //[SerializeField] public RenderTexture[] displacementMap;
        //[SerializeField] public ComputeBuffer[] particleBuffers;
        //[SerializeField] public ComputeBuffer[] particleDirBuffers;
        [SerializeField] public int resolution = 256;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);
        [SerializeField] public float blendRange = 0.2f;
        [SerializeField] public float blendStrength = 0.5f;
        [SerializeField] public float oceanSize = 100f;
        [SerializeField] public float useFFTOnly = 0f;
        //[SerializeField] public NativeArray<float>[] heightMaps;

        [SerializeField] Material oceanMaterial;

        //Vector4[] particleData;
        //Vector2[] particleVelData;

        [Header("Wake Generation")]
        [SerializeField] public float wakeGenerationScale = 1.0f; // 调节生成波浪的强度
        [SerializeField] public float wakeParticleLife = 5.0f;    // 尾迹粒子寿命
        [SerializeField] public float wakeMinParticleSpeed = 3f; // 尾迹粒子最小速度，避免慢粒子堆积
        [SerializeField] public float wakeAmplitudeHalfLife = 0.75f; // 尾迹振幅半衰期(秒)：越小衰减越快
        [SerializeField] public float wakeMinShipHorizontalSpeed = 0.1f; // 船水平速度低于该值时不生成尾迹，避免静止抖动夸张/堆积
        private int wakeTargetBucketIdx = -1;                     // 尾迹粒子放入哪个 bucket
        private float wakeTargetRadius = 1.0f;                    // 尾迹粒子半径

        [Header("Wake Debug")]
        [SerializeField] bool logWakeStatsEachFrame = false;

        [Header("Debug")]
        float curTime;
        float prevTime;
        float perTime;
        // 在类里缓存一个可复用的2D RT
        RenderTexture _slicePreviewRT;

        //public int[] debugOut = new int[4];
        /*public UnityEngine.UI.RawImage heightMapDisplay;
        public UnityEngine.UI.RawImage normalMapDisplay;
        public UnityEngine.UI.RawImage displacementMapDisplay;*/

        private Texture2D[] heightMapT2D;
        private Texture2D[] normalMapT2D;
        private Texture2D[] displacementMapT2D;

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
            //particleData = new Vector4[MAX_PARTICLES];
            //particleVelData = new Vector2[MAX_PARTICLES];
            Debug.Log($"Generated {waveParticleRegions.Count()} wave particle regions");

            solids = new List<SolidHydrodynamics>(FindObjectsOfType<SolidHydrodynamics>());
            Debug.Log($"Found {solids.Count} SolidHydrodynamics objects in the scene.");

            if (waveParticleRegions.Count == 0)
            {
                Debug.LogWarning("No particle regions.");
            }

            textureSize = new Vector2Int(resolution, resolution);
            // 初始化
            heightSlicesInt = new RenderTexture[MAX_REGIONS];
            heightSlicesFloat = new RenderTexture[MAX_REGIONS];
            heightSlicesTmp = new RenderTexture[MAX_REGIONS];
            heightMap = new RenderTexture[MAX_REGIONS];
            normalMap = new RenderTexture[MAX_REGIONS];
            /*velocityXSlicesInt = new RenderTexture[MAX_PARTICLES];
            velocityYSlicesInt = new RenderTexture[MAX_PARTICLES];
            displacementMap = new RenderTexture[MAX_REGIONS];*/

            for (int i = 0; i < Math.Min(waveParticleRegions.Count, MAX_REGIONS); i++)
            {
                //waveParticleRegions[i].particles = new NativeList<WaveParticle>(Allocator.Persistent);

                waveParticleRegions[i].InitBuckets(N_omega);

                // Tex2DArray: RInt（粒子 splat 的累加目标）
                heightSlicesInt[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RInt)
                {
                    volumeDepth = N_omega,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    enableRandomWrite = true
                };
                heightSlicesInt[i].Create();

                // Tex2DArray: RFloat（滤波前/后的工作贴图）
                heightSlicesFloat[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat)
                {
                    volumeDepth = N_omega,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    enableRandomWrite = true
                };
                heightSlicesFloat[i].Create();

                heightSlicesTmp[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat)
                {
                    volumeDepth = N_omega,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    enableRandomWrite = true
                };
                heightSlicesTmp[i].Create();

/*                velocityXSlicesInt[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RInt)
                {
                    volumeDepth = N_omega,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    enableRandomWrite = true
                };
                velocityXSlicesInt[i].Create();

                velocityYSlicesInt[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RInt)
                {
                    volumeDepth = N_omega,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    enableRandomWrite = true
                };
                velocityYSlicesInt[i].Create();

                displacementMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RGFloat)
                {
                    enableRandomWrite = true
                };
                displacementMap[i].Create();*/

                heightMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true }; heightMap[i].Create();
                normalMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true }; normalMap[i].Create();
                //displacementMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true }; displacementMap[i].Create();

                // 每个 region 为每个桶准备粒子 buffer（复用，容量 MAX_PARTICLES）
                if (particleBuffersPerBucket == null)
                    particleBuffersPerBucket = new ComputeBuffer[MAX_REGIONS][];
                particleBuffersPerBucket[i] = new ComputeBuffer[N_omega];

                if (particleVelBuffersPerBucket == null)
                    particleVelBuffersPerBucket = new ComputeBuffer[MAX_REGIONS][];
                particleVelBuffersPerBucket[i] = new ComputeBuffer[N_omega];

                for (int b = 0; b < N_omega; b++) {
                    //particleBuffersPerBucket[i][b] = new ComputeBuffer(MAX_PARTICLES, sizeof(float) * 4);
                    //particleVelBuffersPerBucket[i][b] = new ComputeBuffer(MAX_PARTICLES, sizeof(float) * 2);
                    particleBuffersPerBucket[i][b] =
                        new ComputeBuffer(
                            MAX_PARTICLES,
                            sizeof(float) * 4,
                            ComputeBufferType.Structured,
                            ComputeBufferMode.SubUpdates);

                    particleVelBuffersPerBucket[i][b] =
                        new ComputeBuffer(
                            MAX_PARTICLES,
                            sizeof(float) * 2,
                            ComputeBufferType.Structured,
                            ComputeBufferMode.SubUpdates);
                }

                if (normalMapT2D == null) normalMapT2D = new Texture2D[MAX_REGIONS];
                if (normalMapT2D[i] == null || normalMapT2D[i].width != resolution)
                {
                    normalMapT2D[i] = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false);
                }

                if (heightMapT2D == null) heightMapT2D = new Texture2D[MAX_REGIONS];
                if (heightMapT2D[i] == null || heightMapT2D[i].width != resolution)
                {
                    heightMapT2D[i] = new Texture2D(resolution, resolution, TextureFormat.RFloat, false);
                }

            }

            fixedFrameCnt = 0;

            // 根据网格物理尺寸自动计算频率范围
            if (converter.UseFixedFrequencyRange)
            {
                // λ_max = oceanSize → ω_min = √(g·k_min) = √(g·2π/λ_max)
                float lambda_max = oceanSize;
                float k_min = 2f * Mathf.PI / lambda_max;
                converter.FixedOmegaMin = Mathf.Sqrt(wavesSettings.g * k_min);

                // λ_min = oceanSize/resolution × 2 (奈奎斯特采样) → ω_max = √(g·2π/λ_min)
                float lambda_min = oceanSize / resolution * 2f;
                float k_max = 2f * Mathf.PI / lambda_min;
                converter.FixedOmegaMax = Mathf.Sqrt(wavesSettings.g * k_max);

                Debug.Log($"[WaveParticleSystem] 自动计算频率范围: " +
                         $"λ∈[{lambda_min:F2}m, {lambda_max:F2}m] → " +
                         $"ω∈[{converter.FixedOmegaMin:F3}, {converter.FixedOmegaMax:F3}] rad/s");
            }

            converter.Initialize(wavesSettings,N_omega,N_theta);
            bucketCounts = new int[N_omega];

            bucketOmega = converter._omegas;
            bucketRadii = converter._radii;
            deltaOmega = converter._deltaOmegas;
            radiiBuf = new ComputeBuffer(N_omega, sizeof(float));
            radiiBuf.SetData(bucketRadii);

            // Wake 的默认（fallback）桶：当动态选桶不可用时使用
            if (bucketRadii != null && bucketRadii.Length > 0)
            {
                wakeTargetBucketIdx = Mathf.Clamp(N_omega / 2, 0, bucketRadii.Length - 1);
                wakeTargetRadius = bucketRadii[wakeTargetBucketIdx];
                Debug.Log($"[WaveParticleSystem] Wake Fallback Bucket: {wakeTargetBucketIdx}, Radius: {wakeTargetRadius:F2}m");
            }

            oceanMaterial.SetFloat("_ParticleHeightScale", 1.0f);

            //测试用
            _slicePreviewRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
            _slicePreviewRT.Create();

            // 第一帧初始化

            if (seedOnStart)
            {
                for (int r = 0; r < Mathf.Min(waveParticleRegions.Count, MAX_REGIONS); r++)
                {
                    SeedRegionOnceFromSpectrum(r);
                }
            }

        }
        public void Update()
        {
            if (fixedFrameCnt == 0)
            {
                curTime = Time.time;
                prevTime = Time.time;
            }
            fixedFrameCnt++;
            float dt = Time.deltaTime;
            if (fixedFrameCnt % 1000 ==0) {
                curTime = Time.time;
                perTime = (curTime - prevTime) / 1000.0f;
                //Debug.Log("time per frame: " + perTime);
                Debug.Log("Average FPS:" + 1.0f/perTime);
                prevTime = curTime;
            }
            particleCnt = 0;

            UpdateRegionCentersFromSolids(Time.deltaTime);

            // ---- 固体->流体 ----
            ProcessSolidWakes(dt);

            oceanMaterial.SetInt("_RegionCount", waveParticleRegions.Count);
            oceanMaterial.SetFloat("_BlendRange", blendRange);
            oceanMaterial.SetFloat("_BlendStrength", blendStrength);
            oceanMaterial.SetFloat("_UseFFTOnly", useFFTOnly);

            //var sw = Stopwatch.StartNew();      // 等同于 new Stopwatch(); sw.Start();

            for (int r = 0; r < waveParticleRegions.Count; r++)
            {
                //sw.Restart();
                var region = waveParticleRegions[r];

                // 0) 按需生成边界粒子（你原来的逻辑）
                using (var edgeParticles = converter.GenerateParticlesFromSpectrumPerFrame(
                    ws: wavesSettings,
                    regionCenter: region.center,
                    regionSize: region.size,
                    N_omega: N_omega,
                    N_theta: N_theta,
                    deltaTime: dt,
                    allocator: Allocator.TempJob))
                {
                    AddEdgeParticlesToBuckets(r, edgeParticles.AsArray());
                }

                // 1) 更新粒子（分桶并行） 2) 剔除越界粒子
                UpdateRegionParticlesBuckets(r, dt);

                // 3) 统计并上传每桶粒子（只传有效段）
                UpdateRegionParticleBufferBuckets(r);

                // 4) 清零该 region 的 RInt array
                ClearArraySlices(heightSlicesInt[r], N_omega);
                //ClearArraySlices(heightSlicesTmp[r], N_omega);      // Horizontal 前清零 tmp
                //ClearArraySlices(heightSlicesFloat[r], N_omega);    // Vertical 前清零目标

                // 5) Splat（每桶一次，写入对应 slice）
                int kS = SplatBucketsCS.FindKernel("SplatBucket");
                SplatBucketsCS.SetTexture(kS, "_HeightMapArray", heightSlicesInt[r]);
                SplatBucketsCS.SetInts("_TexSize", resolution, resolution);
                SplatBucketsCS.SetVector("_RegionCenter", region.center);
                SplatBucketsCS.SetVector("_RegionSize", region.size);
                
                // Splat Vel for interact
                //int kSV = SplatBucketsCS.FindKernel("SplatVelocityBucket");
                //SplatBucketsCS.SetTexture(kSV, "_VelocityXMapArray", velocityXSlicesInt[r]);
                //SplatBucketsCS.SetTexture(kSV, "_VelocityYMapArray", velocityYSlicesInt[r]);
                //SplatBucketsCS.SetInts("_TexSize", resolution, resolution);

                for (int b = 0; b < N_omega; b++)
                {
                    int count = bucketCounts[b];
                    if (count == 0) continue;

                    SplatBucketsCS.SetBuffer(kS,"_Particles", particleBuffersPerBucket[r][b]);
                    //SplatBucketsCS.SetBuffer(kSV, "_Particles", particleBuffersPerBucket[r][b]);
                    //SplatBucketsCS.SetBuffer(kSV, "_Velocities", particleVelBuffersPerBucket[r][b]);

                    SplatBucketsCS.SetInt("_ParticleCount", count);

                    int rp = Mathf.CeilToInt(bucketRadii[b] / (region.size.x / resolution));
                    SplatBucketsCS.SetInt("_RadiusPixels", rp);
                    SplatBucketsCS.SetInt("_Slice", b);

                    SplatBucketsCS.Dispatch(kS, Mathf.CeilToInt(count / 64f), 1, 1);
                    //SplatBucketsCS.Dispatch(kSV, Mathf.CeilToInt(count / 64f), 1, 1);
                    particleCnt += count;
                }

                // 6) Int2Float（array 版本）
                int kI2F = Int2FloatArrayCS.FindKernel("IntToFloatArray");
                Int2FloatArrayCS.SetTexture(kI2F, "Source", heightSlicesInt[r]);
                Int2FloatArrayCS.SetTexture(kI2F, "Dest", heightSlicesFloat[r]);
                Int2FloatArrayCS.SetInts("TexSize", resolution, resolution);
                Int2FloatArrayCS.SetFloat("Scale", 1000f);
                Int2FloatArrayCS.SetInt("SliceCount", N_omega);
                Int2FloatArrayCS.Dispatch(kI2F, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                // int2float for vel
                //int kV2F = Int2FloatArrayCS.FindKernel("IntToFloatArrayVelocity");
                //Int2FloatArrayCS.SetTexture(kV2F, "SourceVelX", velocityXSlicesInt[r]);
                //Int2FloatArrayCS.SetTexture(kV2F, "SourceVelY", velocityYSlicesInt[r]);
                //Int2FloatArrayCS.SetTexture(kV2F, "DestVel", displacementMap[r]); // RGFloat
                //Int2FloatArrayCS.Dispatch(kV2F, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                // 7) 可分离滤波：一次 dispatch 处理所有 slices（Z 维）
                int kH = SeparableFilterCS.FindKernel("Horizontal");
                int kV = SeparableFilterCS.FindKernel("Vertical");

                // X: floatArray -> tmp
                SeparableFilterCS.SetTexture(kH, "InputArray", heightSlicesFloat[r]);
                SeparableFilterCS.SetTexture(kH, "OutputArray", heightSlicesTmp[r]);

                // 绑定半径表（每 slice 不同）
                SeparableFilterCS.SetBuffer(kH, "RadiiWorld", radiiBuf);
                SeparableFilterCS.SetBuffer(kV, "RadiiWorld", radiiBuf);

                // Y: tmp -> floatArray
                SeparableFilterCS.SetTexture(kV, "InputArray", heightSlicesTmp[r]);
                SeparableFilterCS.SetTexture(kV, "OutputArray", heightSlicesFloat[r]);

                SeparableFilterCS.SetInts("TexSize", resolution, resolution);
                SeparableFilterCS.SetVector("RegionSize", region.size);
                SeparableFilterCS.SetInt("SliceCount", N_omega);

                int gx = Mathf.CeilToInt(resolution / 8f);
                int gy = Mathf.CeilToInt(resolution / 8f);

                // Horizontal 一次跑完所有 slices
                SeparableFilterCS.Dispatch(kH, gx, gy, N_omega);

                // 若要在 H 和 V 之间调试，可在此调用 EnqueueDebugSliceMax(heightSlicesTmp[r], b, ...)

                // Vertical 一次跑完所有 slices
                SeparableFilterCS.Dispatch(kV, gx, gy, N_omega);

                // 8) 合并 slices → heightMap[r]（float）
                int kR = ReduceSlicesCS.FindKernel("SumSlices");
                ReduceSlicesCS.SetTexture(kR, "_HeightMapArray", heightSlicesFloat[r]);
                ReduceSlicesCS.SetTexture(kR, "_HeightMapOut", heightMap[r]);
                ReduceSlicesCS.SetInts("_TexSize", resolution, resolution);
                ReduceSlicesCS.SetInt("_SliceCount", N_omega);
                ReduceSlicesCS.Dispatch(kR, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                // 9) 法线
                int kN = Height2NormalComputeShader.FindKernel("CSMain");
                Height2NormalComputeShader.SetTexture(kN, "HeightTex", heightMap[r]);
                Height2NormalComputeShader.SetTexture(kN, "NormalTex", normalMap[r]);
                Height2NormalComputeShader.SetInts("TexSize", resolution, resolution);
                Height2NormalComputeShader.SetVector("RegionSize", region.size);   // ← 新增
                Height2NormalComputeShader.Dispatch(kN, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                // 10) 传材质
                oceanMaterial.SetTexture($"_ParticleHeightMap{r}", heightMap[r]);
                oceanMaterial.SetTexture($"_ParticleNormalMap{r}", normalMap[r]);
                oceanMaterial.SetVector($"_RegionCenter{r}", region.center);
                oceanMaterial.SetVector($"_RegionSize{r}", region.size);

                // 11) 更新texture2d for interact

                // 11) Fluid → Solid: apply hydrodynamic forces
                foreach (var solid in solids)
                {
                    int id = solid.regionId;

                    // region 数据
                    //var region = waveParticleRegions[id];

                    // 注意：你的最终 height map 在 heightMap[id] 数组中，而不是 region.heightMapFloat！
                    Texture heightTex = heightMap[id];

                    Vector2 windXZ = wavesSettings.local.windSpeed;
                    Vector3 windVel = new Vector3(windXZ.x, 0, windXZ.y);

                    solid.ComputeForces(
                        heightTex,
                        waterDensity: 1000f,
                        cdWater: solid.CdWater,
                        airDensity: 1.2f,
                        cdAir: solid.CdAir,
                        windVelocity: windVel,
                        resolution,         // height map resolution
                        waveParticleRegions[id].size.x,      // regionSize（你区域是正方形，用 x 就行）
                        waveParticleRegions[id].center - waveParticleRegions[id].size * 0.5f // regionMin
                    );

                    // solid 排水量
                    // float Q_vert = solid.totalVertFlux;
                    // float Q_horz = solid.totalHorzFlux;

                    // Q_vert = solid.smoothedVertFlux;
                    // Q_horz = solid.smoothedHorzFlux;

                    // // 取刚体在水面上的投影位置
                    // var rb = solid.rb;
                    // Vector2 posXZ = new Vector2(rb.worldCenterOfMass.x, rb.worldCenterOfMass.z);

                    // GenerateWaveParticles(
                    //     solid.regionId,
                    //     posXZ,
                    //     Q_vert,
                    //     Q_horz,
                    //     rb.velocity,
                    //     Time.deltaTime,
                    //     dirSampleCount: 32   // 可自己调
                    // );
                }

                //SyncRegionTexturesToCPU(r);

                // 12) debug 打印波粒子区域RMS
                //PhysicsVerify.CalcRMS(heightMapT2D[0],false);

            }

            // 看第0个region的第b个bucket
            //ShowBucketSlice(0, 0, heightMapDisplay);  // 直接复用已有的 RawImage 显示

            //DebugSliceStats(0, 0);
            //DebugSliceStats(0, 1);
            //DebugSliceStats(0, 2);
            //DebugSliceStats(0, 3);
        }

        void SeedRegionOnceFromSpectrum(int regionIdx)
        {
            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count) return;

            var region = waveParticleRegions[regionIdx];

            // 生成整域粒子（一次性）
            using (var initParticles = converter.GenerateParticlesFromSpectrum(
                       ws: wavesSettings,
                       regionCenter: region.center,
                       regionSize: region.size,
                       N_omega: N_omega,
                       N_theta: N_theta,
                       allocator: Allocator.TempJob)) // TempJob 即可
            {
                // 先统计各 bucket 的数量 → 预留容量，避免 Add 过程中多次扩容
                var counts = new int[N_omega];
                var arr = initParticles.AsArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    int b = Mathf.Clamp(arr[i].bucketNum, 0, N_omega - 1);
                    counts[b]++;
                }
                for (int b = 0; b < N_omega; b++)
                {
                    // buckets[b] 现容量不足则提升（只增不减）
                    if (region.buckets[b].Capacity < counts[b])
                        region.buckets[b].Capacity = counts[b];
                }

                // 把粒子放入对应 bucket
                for (int i = 0; i < arr.Length; i++)
                {
                    var p = arr[i];
                    int b = Mathf.Clamp(p.bucketNum, 0, N_omega - 1);
                    region.buckets[b].AddNoResize(p); // 容量已预留，避免分配
                }
            }
        }

        // 把新生成的粒子直接丢进对应的 bucket（在 FixedUpdate 里生成完后调用）
        void AddEdgeParticlesToBuckets(int idx, NativeArray<WaveParticle> newParticles)
        {
            // 可选：确保 buckets 已初始化
            if (waveParticleRegions[idx].buckets == null || waveParticleRegions[idx].buckets.Length != N_omega)
                waveParticleRegions[idx].InitBuckets(N_omega);

            for (int i = 0; i < newParticles.Length; i++)
            {
                var p = newParticles[i];
                int b = Mathf.Clamp(p.bucketNum, 0, N_omega - 1);
                waveParticleRegions[idx].buckets[b].Add(p);
            }
        }

        /*void UpdateRegionParticlesBuckets(int idx, float dt)
        {
            var region = waveParticleRegions[idx];
            //var handles = new NativeArray<JobHandle>(N_omega, Allocator.Temp);
            JobHandle combined = default;

            for (int b = 0; b < N_omega; b++)
            {
                var arr = region.buckets[b].AsArray();
                int len = arr.Length;
                region.EnsureScratchCapacity(b, len);
                if (len == 0) continue;

                var job = new WaveParticleUpdateCullJob
                {
                    particles = arr,
                    survivors = region.scratch[b].AsParallelWriter(),
                    deltaTime = dt,
                    planeSize = region.size.x,
                    oceanSize = oceanSize,
                    regionCenter = region.center,
                    regionHalf = region.size * 0.5f
                };

                // 每个 bucket 一条并行任务，批大小可按硬件调 64/128
                var handle = job.Schedule(len, 128);
                combined = JobHandle.CombineDependencies(combined, handle);
            }
            combined.Complete();

            // 零拷贝切换：scratch -> buckets
            region.SwapBuckets();
        }*/
        void UpdateRegionParticlesBuckets(int idx, float dt)
        {
            var region = waveParticleRegions[idx];

            // 预先算好比例，减少每粒子的除法
            float planeScale = region.size.x / oceanSize; // 你原来是 planeSize=region.size.x

            // 我们为每个 bucket 做：并行 更新+标记 -> 单 job 压缩
            NativeArray<JobHandle> handles = new NativeArray<JobHandle>(N_omega * 2, Allocator.Temp);
            int h = 0;

            for (int b = 0; b < N_omega; b++)
            {
                var src = region.buckets[b].AsArray();
                int len = src.Length;
                region.EnsureScratchCapacity(b, len);  // 目标 outList 提前扩容（下一步压缩写）

                if (len == 0) { region.scratch[b].Clear(); continue; }

                // 暂存 updated 与 alive（临时栈/帧内存，不进托管堆）
                var updated = new NativeArray<WaveParticle>(len, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var alive = new NativeArray<byte>(len, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                // 并行：更新 + 标记
                var updHandle = new UpdateMarkAliveJob
                {
                    src = src,
                    dst = updated,
                    alive = alive,
                    deltaTime = dt,
                    planeScale = planeScale,
                    regionCenter = region.center,
                    regionHalf = region.size * 0.5f,
                    amplitudeHalfLife = wakeAmplitudeHalfLife
                }.Schedule(len, 128);

                // 单通道（或粗粒度并行）压缩：顺序写入 scratch[b]
                var compactHandle = new CompactAliveJob
                {
                    updated = updated,
                    alive = alive,
                    outList = region.scratch[b]
                }.Schedule(updHandle);

                // 释放临时内存
                JobHandle disposeUpdated = updated.Dispose(compactHandle);
                JobHandle disposeAlive = alive.Dispose(compactHandle);
                handles[h++] = disposeUpdated;
                handles[h++] = disposeAlive;

            }

            // 等所有 bucket 完成
            if (h > 0)
            {
                var combined = JobHandle.CombineDependencies(handles.GetSubArray(0, h));
                combined.Complete();
            }
            handles.Dispose();

            // 零拷贝切换 scratch -> buckets
            region.SwapBuckets();
        }


        /*void UpdateRegionParticleBufferBuckets(int idx)
        {
            var region = waveParticleRegions[idx];
            for (int b = 0; b < N_omega; b++)
            {
                int count = region.buckets[b].Length;
                bucketCounts[b] = count;
                if (count == 0) continue;

                // 打包到复用数组（或改用 BeginWrite/EndWrite + NativeArray）
                for (int i = 0; i < count; i++) {
                    particleData[i] = region.buckets[b][i].ToVector4();
                    //particleVelData[i] = region.buckets[b][i].GetVelocity();
                }
                particleBuffersPerBucket[idx][b].SetData(particleData, 0, 0, count);
                //particleVelBuffersPerBucket[idx][b].SetData(particleVelData, 0, 0, count);
            }
        }*/
        void UpdateRegionParticleBufferBuckets(int idx)
        {
            var region = waveParticleRegions[idx];

            // 可选：如果你想并行所有 bucket，把所有 handle 记录下来，最后 Combine/Complete 一次
            NativeArray<JobHandle> handles = new NativeArray<JobHandle>(N_omega, Allocator.Temp); // *2 是给速度的，可不需要就减掉
            int hCount = 0;

            for (int b = 0; b < N_omega; b++)
            {
                var arr = region.buckets[b].AsArray();
                int count = arr.Length;
                bucketCounts[b] = count;
                if (count == 0) continue;

                // ---- 写入粒子（Vector4）----
                // 直接得到指向 GPU buffer 的 NativeArray<T> 视图（零拷贝）
                var gpuSpanV4 = particleBuffersPerBucket[idx][b].BeginWrite<Vector4>(0, count);

                // 调度 Burst Job 把 WaveParticle -> Vector4
                var packJob = new PackParticlesToV4Job
                {
                    src = arr,
                    dst = gpuSpanV4
                }.Schedule(count, 128);

                handles[hCount++] = packJob;

                // ----（可选）写入速度（Vector2）----
                // 如果后续要用 SplatVelocityBucket，这里同样走 BeginWrite
                // var gpuSpanV2 = particleVelBuffersPerBucket[idx][b].BeginWrite<Vector2>(0, count);
                // var velJob = new PackVelocitiesToV2Job { src = arr, dst = gpuSpanV2 }.Schedule(count, 128);
                // handles[hCount++] = velJob;

                // 注意：EndWrite 需要在对应写入 job 完成后再调，
                // 这里先把 job 句柄存起来，后面统一 Complete 再 EndWrite
            }

            // ---- 统一等待所有打包 job 完成 ----
            if (hCount > 0)
            {
                var combined = JobHandle.CombineDependencies(handles.GetSubArray(0, hCount));
                combined.Complete();
            }
            handles.Dispose();

            // ---- 统一 EndWrite（顺序无所谓，但必须保证对应 job 已完成）----
            for (int b = 0; b < N_omega; b++)
            {
                int count = bucketCounts[b];
                if (count == 0) continue;

                particleBuffersPerBucket[idx][b].EndWrite<Vector4>(count);

                // 如果上面也写了速度，这里对应 EndWrite
                // particleVelBuffersPerBucket[idx][b].EndWrite<Vector2>(count);
            }
        }

        [BurstCompile]
        struct DisposeNativeArrayJob<T> : IJob where T : struct
        {
            public NativeArray<T> arr;
            public void Execute() => arr.Dispose();
        }


        static void ClearArraySlices(RenderTexture arrayRT, int sliceCount)
        {
            var prev = RenderTexture.active;
            for (int s = 0; s < sliceCount; s++)
            {
                Graphics.SetRenderTarget(arrayRT, 0, CubemapFace.Unknown, s); // 绑定指定 slice
                GL.Clear(clearDepth: false, clearColor: true, backgroundColor: Color.clear);
            }
            RenderTexture.active = prev;
            Graphics.SetRenderTarget(null);
        }

        // 同步某个 region 的 RT -> Texture2D
        void SyncRegionTexturesToCPU(int regionIdx)
        {
            // 高度

            RenderTexture.active = heightMap[regionIdx];
            heightMapT2D[regionIdx].ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            heightMapT2D[regionIdx].Apply();

            // 法线

            RenderTexture.active = normalMap[regionIdx];
            normalMapT2D[regionIdx].ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            normalMapT2D[regionIdx].Apply();

            RenderTexture.active = null;
        }


        //use for debug
        /// <summary>把某个region的heightSlicesFloat的第slice层显示到一个RawImage</summary>
        public void ShowBucketSlice(int regionIndex, int sliceIndex, UnityEngine.UI.RawImage target)
        {
            if (target == null) return;

            // 从 Array 拷贝到二维纹理（要求同分辨率同格式）
            Graphics.CopyTexture(
                heightSlicesFloat[regionIndex],    // src array
                sliceIndex, 0,                     // srcElement(=slice), srcMip
                _slicePreviewRT,                   // dst 2D
                0, 0                               // dstElement(=0), dstMip
            );

            target.texture = _slicePreviewRT;
        }

        public void DebugSliceStats(int regionIndex, int sliceIndex)
        {
            // 创建临时 RFloat RT
            var tmpRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat);
            tmpRT.enableRandomWrite = false;
            tmpRT.Create();

            // 拷贝指定 slice
            Graphics.CopyTexture(heightSlicesFloat[regionIndex], sliceIndex, 0, tmpRT, 0, 0);

            AsyncGPUReadback.Request(tmpRT, 0, request =>
            {
                // 用完立刻释放
                tmpRT.Release();
                UnityEngine.Object.Destroy(tmpRT);

                if (request.hasError)
                {
                    Debug.LogError("Readback failed");
                    return;
                }

                var data = request.GetData<float>(); // 单通道直接用 float
                double sum = 0;
                float minv = float.PositiveInfinity;
                float maxv = float.NegativeInfinity;
                int n = data.Length;

                for (int i = 0; i < n; i++)
                {
                    float v = data[i];
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    sum += v;
                }

                double avg = sum / n;
                Debug.Log($"[Slice Stats] region={regionIndex}, slice={sliceIndex}, min={minv:F6}, max={maxv:F6}, avg={avg:F6}");
            });
        }

        // 调用示例：EnqueueDebugSliceMax(heightSlicesTmp[r], b, $"H r={r} b={b}");
        //         或  EnqueueDebugSliceMax(heightSlicesFloat[r], b, $"V r={r} b={b}");
        void EnqueueDebugSliceMax(RenderTexture arrayRT, int slice, string label)
        {
            // 复制该 slice → 临时 RFloat 2D
            var tmpR = RenderTexture.GetTemporary(arrayRT.width, arrayRT.height, 0, RenderTextureFormat.RFloat);
            Graphics.CopyTexture(arrayRT, slice, 0, tmpR, 0, 0);

            // 异步读回并计算统计量
            AsyncGPUReadback.Request(tmpR, 0, request =>
            {
                RenderTexture.ReleaseTemporary(tmpR);

                if (request.hasError)
                {
                    Debug.LogError($"AsyncGPUReadback failed: {label}");
                    return;
                }

                var data = request.GetData<float>();
                double sum = 0;
                float minv = float.PositiveInfinity;
                float maxv = float.NegativeInfinity;

                int n = data.Length;
                for (int i = 0; i < n; i++)
                {
                    float v = data[i];
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    sum += v;
                }
                double avg = sum / n;
                float absMax = Mathf.Max(Mathf.Abs(minv), Mathf.Abs(maxv));

                Debug.Log($"[SliceStats {label}] min={minv:F6}, max={maxv:F6}, absMax={absMax:F6}, avg={avg:F6}");
            });
        }

        void EnqueueAverageHeightLogCPU(int regionIdx)
        {
            var rt = heightMap[regionIdx]; // RFloat，单通道
            int nPixels = rt.width * rt.height;

            AsyncGPUReadback.Request(rt, 0, request =>
            {
                if (request.hasError)
                {
                    Debug.LogError($"[AvgHeight] Readback failed, region={regionIdx}");
                    return;
                }

                var data = request.GetData<float>(); // RFloat：长度 = width*height
                double sum = 0.0;
                float minH = float.PositiveInfinity;
                float maxH = float.NegativeInfinity;
                for (int i = 0; i < data.Length; i++)
                {
                    sum += data[i];
                    if (data[i] < minH) minH = data[i];
                    if (data[i] > maxH) maxH = data[i];
                }
                float mean = (float)(sum / nPixels);

                Debug.Log($"WP [AvgHeight][CPU] region={regionIdx} mean={mean:F6} min={minH:F6}  max={maxH:F6}");
            });
        }


        void OnDisable()
        {
            CleanupResources();
        }

        void OnDestroy()
        {
            CleanupResources();
        }

        void CleanupResources()
        {
            // ---- RenderTextures ----
            ReleaseRenderTextures(ref heightSlicesInt);
            ReleaseRenderTextures(ref heightSlicesFloat);
            ReleaseRenderTextures(ref heightSlicesTmp);
            ReleaseRenderTextures(ref heightMap);
            ReleaseRenderTextures(ref normalMap);

            if (_slicePreviewRT != null)
            {
                _slicePreviewRT.Release();
                Destroy(_slicePreviewRT);
                _slicePreviewRT = null;
            }

            // ---- Texture2D (Unity Object，需要 Destroy) ----
            ReleaseTexture2Ds(ref heightMapT2D);
            ReleaseTexture2Ds(ref normalMapT2D);

            // ---- ComputeBuffers ----
            if (particleBuffersPerBucket != null)
            {
                foreach (var buffers in particleBuffersPerBucket)
                {
                    if (buffers == null) continue;
                    foreach (var buffer in buffers)
                    {
                        buffer?.Release();
                    }
                }
                particleBuffersPerBucket = null;
            }

            if (particleVelBuffersPerBucket != null)
            {
                foreach (var buffers in particleVelBuffersPerBucket)
                {
                    if (buffers == null) continue;
                    foreach (var buffer in buffers)
                    {
                        buffer?.Release();
                    }
                }
                particleVelBuffersPerBucket = null;
            }

            radiiBuf?.Release();
            radiiBuf = null;

            // ---- NativeLists in regions ----
            if (waveParticleRegions != null)
            {
                foreach (var region in waveParticleRegions)
                {
                    region?.Dispose();
                }
            }

            Debug.Log("[WaveParticleSystem] Resources cleaned up.");
        }



        /// <summary>
        /// 采样水面高度和法线（不包含水平位移）
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
            if (heightMapT2D != null && heightMapT2D[regionIdx] != null)
            {
                height = heightMapT2D[regionIdx].GetPixelBilinear(uv.x, uv.y).r;
            }
            else
            {
                Debug.LogWarning("未找到对应的 heightMapT2D");
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

            // 返回的 position 不加水平位移，只是原始XZ和水面高度
            return new Vector3(worldXZ.x, region.center.y + height, worldXZ.y);
        }


        /// <summary>
        /// 导出测得频谱与理论频谱的二维矩阵到 CSV 文件
        /// 额外导出：
        /// 1) 每个 theta 上对所有 omega 的能量和（方向谱边缘）
        /// 2) 每个 omega 上对所有 theta 的能量和（频谱边缘）
        /// </summary>
        void ExportSpectrumCsv(
            double[,] E_meas,
            double[,] E_theory,
            SpectrumToParticlesConverter converter,
            WavesSettings ws,
            Vector2 regionSize)
        {
            int Nw = converter._Nomega;
            int Nt = converter._Ntheta;

            // === 文件名构造 ===
            float wind = ws.local.windSpeed.magnitude;
            float size = regionSize.x;

            string date = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{wind:F1}_{size:F1}_{Nw}_{Nt}_{date}.csv";

            // 放到 Assets/Exports 下面：
            string dir = Path.Combine(Application.dataPath, "Exports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);

            using (StreamWriter sw = new StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                sw.WriteLine("Hybrid Ocean Spectrum Export");
                sw.WriteLine($"WindSpeed,{wind:F3}");
                sw.WriteLine($"RegionSize,{size}");
                sw.WriteLine($"N_omega,{Nw}");
                sw.WriteLine($"N_theta,{Nt}");
                sw.WriteLine($"Date,{date}");
                sw.WriteLine();

                // =======================
                // 1) 2D 矩阵：E(omega, theta)
                // =======================
                sw.WriteLine("Full 2D Spectrum (normalized)");
                sw.Write("Bin");
                for (int it = 0; it < Nt; it++)
                    sw.Write($",theta{it}_meas,theta{it}_theory");
                sw.WriteLine();

                for (int iw = 0; iw < Nw; iw++)
                {
                    sw.Write($"omega{iw}");
                    for (int it = 0; it < Nt; it++)
                    {
                        sw.Write($",{E_meas[iw, it].ToString("F6", CultureInfo.InvariantCulture)}");
                        sw.Write($",{E_theory[iw, it].ToString("F6", CultureInfo.InvariantCulture)}");
                    }
                    sw.WriteLine();
                }

                sw.WriteLine();
                
                // =======================
                // 2) 同一 theta：对所有 omega 求和
                //    => 方向边缘谱 E_theta(it)
                // =======================
                sw.WriteLine("Directional Marginals: sum over omega for each theta");
                sw.WriteLine("thetaIndex,meas_sum,theory_sum");

                for (int it = 0; it < Nt; it++)
                {
                    double sumMeasTheta = 0.0;
                    double sumTheoryTheta = 0.0;
                    for (int iw = 0; iw < Nw; iw++)
                    {
                        sumMeasTheta   += E_meas[iw, it];
                        sumTheoryTheta += E_theory[iw, it];
                    }

                    sw.WriteLine(
                        $"{it}," +
                        $"{sumMeasTheta.ToString("F6", CultureInfo.InvariantCulture)}," +
                        $"{sumTheoryTheta.ToString("F6", CultureInfo.InvariantCulture)}"
                    );
                }

                sw.WriteLine();

                // =======================
                // 3) 同一 omega：对所有 theta 求和
                //    => 频率边缘谱 E_omega(iw)
                // =======================
                sw.WriteLine("Frequency Marginals: sum over theta for each omega");
                sw.WriteLine("omegaIndex,meas_sum,theory_sum");

                for (int iw = 0; iw < Nw; iw++)
                {
                    double sumMeasOmega = 0.0;
                    double sumTheoryOmega = 0.0;
                    for (int it = 0; it < Nt; it++)
                    {
                        sumMeasOmega   += E_meas[iw, it];
                        sumTheoryOmega += E_theory[iw, it];
                    }

                    sw.WriteLine(
                        $"{iw}," +
                        $"{sumMeasOmega.ToString("F6", CultureInfo.InvariantCulture)}," +
                        $"{sumTheoryOmega.ToString("F6", CultureInfo.InvariantCulture)}"
                    );
                }

                sw.WriteLine();
                sw.WriteLine("End");
            }

            Debug.Log($"[CSV Export] 频谱结果已保存至: {path}");
        }



        /// <summary>
        /// 对指定 region 做一次 频率–方向 频谱一致性测试
        /// 统计当前波粒子系统的能量分布 E_meas(ω,θ)，
        /// 和解析谱离散后给出的 E_theory(ω,θ) 做对比。
        /// </summary>
        [ContextMenu("Run Spectrum Consistency Test (Region 0)")]
        public void RunSpectrumConsistencyTestRegion0()
        {
            RunSpectrumConsistencyTest(0);
        }

        /// <param name="regionIdx">要测试的波粒子区域索引</param>
        public void RunSpectrumConsistencyTest(int regionIdx)
        {
            if (converter == null)
            {
                Debug.LogWarning("[SpectrumTest] converter 为空");
                return;
            }

            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count)
            {
                Debug.LogWarning($"[SpectrumTest] regionIdx 越界: {regionIdx}");
                return;
            }

            // 确保 converter 是初始化过的（和当前 N_omega / N_theta 一致）
            converter.Initialize(wavesSettings, N_omega, N_theta);

            int Nw = converter._Nomega;
            int Nt = converter._Ntheta;

            var region = waveParticleRegions[regionIdx];

            // ========= 1) 统计粒子能量 E_meas(ω,θ) =========
            // 用 height^2 作为能量 proxy，按 (bucketNum, 方向bin) 累加
            double[,] E_meas = new double[Nw, Nt];

            for (int iw = 0; iw < Nw; iw++)
            {
                if (region.buckets == null || iw >= region.buckets.Length) break;

                var arr = region.buckets[iw].AsArray();
                int len = arr.Length;
                for (int i = 0; i < len; i++)
                {
                    var p = arr[i];
                    float h = p.height;
                    if (Mathf.Abs(h) < 1e-7f) continue;

                    // 根据粒子方向找最近的 θ-bin
                    int it = converter.FindThetaBin(p.direction);
                    it = Mathf.Clamp(it, 0, Nt - 1);

                    // double e = (double)h * (double)h;  // 能量 ~ 振幅^2
                    double e = (double)h * (double)h * (double)(p.radius * p.radius); // 考虑粒子面积影响

                    E_meas[iw, it] += e;
                }
            }

            // ========= 2) 计算理论 bin 能量 E_theory(ω,θ) =========
            double[,] E_theory = new double[Nw, Nt];
            for (int iw = 0; iw < Nw; iw++)
            {
                for (int it = 0; it < Nt; it++)
                {
                    double e = converter.EvalBinEnergy(iw, it);
                    E_theory[iw, it] = Math.Max(0.0, e);
                }
            }

            // ========= 3) 归一化到概率分布，避免总体能量不同的影响 =========
            double sumMeas = 0.0, sumTheory = 0.0;
            for (int iw = 0; iw < Nw; iw++)
            {
                for (int it = 0; it < Nt; it++)
                {
                    sumMeas   += E_meas[iw, it];
                    sumTheory += E_theory[iw, it];
                }
            }

            if (sumMeas <= 0.0 || sumTheory <= 0.0)
            {
                Debug.LogWarning($"[SpectrumTest] 能量总和为 0，sumMeas={sumMeas}, sumTheory={sumTheory}");
                return;
            }

            for (int iw = 0; iw < Nw; iw++)
            {
                for (int it = 0; it < Nt; it++)
                {
                    E_meas[iw, it]   /= sumMeas;
                    E_theory[iw, it] /= sumTheory;
                }
            }

            // ========= 4) 计算几个指标：L1 差、L2 差、相关系数 =========
            double l1 = 0.0;
            double l2 = 0.0;

            // 为相关系数准备一维向量
            int K = Nw * Nt;
            double meanMeas = 0.0, meanTheory = 0.0;

            for (int iw = 0; iw < Nw; iw++)
            {
                for (int it = 0; it < Nt; it++)
                {
                    double a = E_meas[iw, it];
                    double b = E_theory[iw, it];

                    l1 += Math.Abs(a - b);
                    double diff = a - b;
                    l2 += diff * diff;

                    meanMeas   += a;
                    meanTheory += b;
                }
            }

            meanMeas   /= K;
            meanTheory /= K;

            double cov = 0.0, varA = 0.0, varB = 0.0;
            for (int iw = 0; iw < Nw; iw++)
            {
                for (int it = 0; it < Nt; it++)
                {
                    double a = E_meas[iw, it];
                    double b = E_theory[iw, it];

                    double da = a - meanMeas;
                    double db = b - meanTheory;

                    cov  += da * db;
                    varA += da * da;
                    varB += db * db;
                }
            }

            double corr = 0.0;
            if (varA > 1e-12 && varB > 1e-12)
            {
                corr = cov / Math.Sqrt(varA * varB);
            }

            // ========= 5) 打日志 =========
            Debug.Log(
                $"[SpectrumTest] region={regionIdx}, Nw={Nw}, Nt={Nt}\n" +
                $"  L1 difference = {l1:F4}\n" +
                $"  L2 difference = {Math.Sqrt(l2):F4}\n" +
                $"  Corr(E_meas, E_theory) = {corr:F4}"
            );

            // 也可以顺便打印每个频率桶的一维谱对比（把方向求和）
            for (int iw = 0; iw < Nw; iw++)
            {
                double sumMeasW = 0.0;
                double sumTheoryW = 0.0;
                for (int it = 0; it < Nt; it++)
                {
                    sumMeasW   += E_meas[iw, it];
                    sumTheoryW += E_theory[iw, it];
                }

                Debug.Log($"[SpectrumTest] ω-bin {iw}  meas={sumMeasW:F4}  theory={sumTheoryW:F4}");
            }
            ExportSpectrumCsv(E_meas, E_theory, converter, wavesSettings, region.size);

        }

        // 放在 WaveParticleSystem 里面任意位置（比如其它 [ContextMenu] 附近）
        [ContextMenu("Dump Height Histogram (Region 0)")]
        public void DumpHeightHistogramRegion0()
        {
            // 默认做 64 个 bins，你可以改成 128/256
            EnqueueHeightHistogram(0, 64);
        }

        public void EnqueueHeightHistogram(int regionIdx, int binCount = 64)
        {
            if (regionIdx < 0 || regionIdx >= heightMap.Length || heightMap[regionIdx] == null)
            {
                Debug.LogWarning($"[HeightHist] regionIdx={regionIdx} 无效或没有 heightMap");
                return;
            }

            var rt = heightMap[regionIdx]; // RFloat 单通道
            int width = rt.width;
            int height = rt.height;
            int nPixels = width * height;

            // 做一次异步读回，避免卡主线程
            AsyncGPUReadback.Request(rt, 0, request =>
            {
                if (request.hasError)
                {
                    Debug.LogError($"[HeightHist] GPUReadback 失败, region={regionIdx}");
                    return;
                }

                var data = request.GetData<float>(); // 长度 = width * height

                if (data.Length == 0)
                {
                    Debug.LogWarning($"[HeightHist] 数据为空, region={regionIdx}");
                    return;
                }

                // 1) 基本统计量：min / max / mean
                double sum = 0.0;
                float minH = float.PositiveInfinity;
                float maxH = float.NegativeInfinity;

                for (int i = 0; i < data.Length; i++)
                {
                    float v = data[i];
                    sum += v;
                    if (v < minH) minH = v;
                    if (v > maxH) maxH = v;
                }

                float mean = (float)(sum / nPixels);

                // 避免所有像素高度相同导致 binWidth = 0
                if (Mathf.Abs(maxH - minH) < 1e-7f)
                {
                    Debug.Log($"[HeightHist] region={regionIdx}, 所有像素高度几乎相同: h≈{mean:F6}");
                    return;
                }

                // 2) 构建直方图
                int bins = Mathf.Max(1, binCount);
                int[] hist = new int[bins];

                float range = maxH - minH;
                float invBinWidth = bins / range;   // 等价于 1 / binWidth

                for (int i = 0; i < data.Length; i++)
                {
                    float v = data[i];
                    int bin = (int)((v - minH) * invBinWidth);
                    if (bin < 0) bin = 0;
                    if (bin >= bins) bin = bins - 1;
                    hist[bin]++;
                }

                // 3) 打印统计结果（可以复制到 Excel 画图）
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[HeightHist] region={regionIdx}");
                sb.AppendLine($"Pixels = {nPixels}");
                sb.AppendLine($"Min = {minH:F6}, Max = {maxH:F6}, Mean = {mean:F6}");
                sb.AppendLine($"Bins = {bins}");
                sb.AppendLine("binIndex, hMin, hMax, count, probability");

                // 准备导出用的数组
                float[] binMin = new float[bins];
                float[] binMax = new float[bins];
                int[] counts   = hist;   // 直接复用 hist

                for (int b = 0; b < bins; b++)
                {
                    float h0 = minH + (range * b) / bins;
                    float h1 = minH + (range * (b + 1)) / bins;
                    int count = hist[b];
                    float p = (float)count / nPixels;

                    // 填导出数组
                    binMin[b] = h0;
                    binMax[b] = h1;

                    sb.AppendLine($"{b}, {h0:F6}, {h1:F6}, {count}, {p:F6}");
                }

                Debug.Log(sb.ToString());

                // 这里就有 binMin / binMax / counts 了，可以导出 CSV
                ExportHeightHistogramCsv(regionIdx, minH, maxH, bins, binMin, binMax, counts);

            });
        }
        public void ExportHeightHistogramCsv(
            int regionIdx,
            float minV,
            float maxV,
            int bins,
            float[] binMin,
            float[] binMax,
            int[] counts)
        {
            string date = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"hist_region{regionIdx}_{bins}bins_{date}.csv";
            
            // 放到 Assets/Exports 下面：
            string dir = Path.Combine(Application.dataPath, "Exports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);

            int total = 0;
            foreach (var c in counts) total += c;

            using (var sw = new StreamWriter(path, false, Encoding.UTF8))
            {
                sw.WriteLine("Height Histogram Export");
                sw.WriteLine($"Region,{regionIdx}");
                sw.WriteLine($"Bins,{bins}");
                sw.WriteLine($"Min,{minV}");
                sw.WriteLine($"Max,{maxV}");
                sw.WriteLine($"TotalPixels,{total}");
                sw.WriteLine();

                sw.WriteLine("binIndex,hMin,hMax,count,probability");

                for (int i = 0; i < bins; i++)
                {
                    float p = (float)counts[i] / total;
                    sw.WriteLine(
                        $"{i},{binMin[i].ToString("F6")},{binMax[i].ToString("F6")},{counts[i]},{p.ToString("F8")}"
                    );
                }
            }

            Debug.Log($"[CSV Export] Height histogram saved -> {path}");
        }


        public void GenerateWaveParticles(
            int regionIdx,
            Vector2 pos,        // 物体在水面上的投影位置 (x,z)
            float Q_vert,       // 竖直体积通量 [m^3/s]（>0：压水，<0：吸水）
            float Q_horz,       // 水平体积通量 [m^3/s]（>0：沿 vel 水平方向推水）
            Vector3 vel,        // 刚体速度（世界空间）
            float dt,
            int dirSampleCount = 16)
        {
            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count) return;
            if (dirSampleCount <= 0) return;

            float g = wavesSettings.g > 0 ? wavesSettings.g : 9.81f;

            // --- 小工具：根据排水体积 dV 选一个最合适的半径桶 ---
            int PickBucketByVolume(float dV, out float radiusOut, out float kOut, out float omegaOut)
            {
                dV = Mathf.Max(dV, 0f);

                // 很小就随便给个最小桶，避免 Nan
                if (dV <= 1e-8f)
                {
                    radiusOut = bucketRadii[0];
                    kOut      = Mathf.PI / Mathf.Max(radiusOut, 1e-3f);
                    omegaOut  = Mathf.Sqrt(g * kOut);
                    return 0;
                }

                // 论文里的近似体积关系：dV ≈ 1.4535 * r^3  →  r ≈ (dV / 1.4535)^(1/3)
                float r = Mathf.Pow(dV / 1.4535f, 1f / 3f);

                int   best     = 0;
                float bestDiff = float.MaxValue;
                for (int b = 0; b < bucketRadii.Length; b++)
                {
                    float d = Mathf.Abs(bucketRadii[b] - r);
                    if (d < bestDiff)
                    {
                        bestDiff = d;
                        best     = b;
                    }
                }

                best      = Mathf.Clamp(best, 0, bucketRadii.Length - 1);
                radiusOut = bucketRadii[best];
                kOut      = Mathf.PI / Mathf.Max(radiusOut, 1e-3f);
                omegaOut  = Mathf.Sqrt(g * kOut);
                return best;
            }

            // =====================================================
            // 1) 竖直排水：均匀环
            // =====================================================
            // Q_vert > 0 : 向下压水 → 正振幅
            // Q_vert < 0 : 向上抽水 → 负振幅
            float signY   = Mathf.Sign(Q_vert);
            float dV_vert = Mathf.Abs(Q_vert) * dt;          // 本帧竖直排水体积

            if (dV_vert > 1e-8f)
            {
                int   bucketY = PickBucketByVolume(dV_vert, out float rY, out float kY, out float omegaY);
                float rY2     = rY * rY;

                // 总振幅，满足（大致） dV_vert ≈ 1.4535 * A_total_Y * rY^2
                const float K_ampY = 1.0f;                   // 可调系数
                float A_total_Y = signY * K_ampY * dV_vert / (1.4535f * rY2);
                A_total_Y = Mathf.Clamp(A_total_Y, -5f, 5f);

                float a_i = 0.5f * A_total_Y / dirSampleCount;      // 均匀分配到一圈

                if (Mathf.Abs(a_i) > 1e-7f)
                {
                    for (int i = 0; i < dirSampleCount; i++)
                    {
                        float ang = i * Mathf.PI * 2f / dirSampleCount;
                        Vector2 dir  = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                        Vector2 ppos = pos + 5f * dir * rY;

                        // 相速可以用深水色散：c = ω/k = sqrt(g / k)
                        float cY = Mathf.Sqrt(g / Mathf.Max(kY, 1e-3f));

                        var p = new WaveParticle
                        {
                            position  = ppos,
                            height    = a_i,
                            direction = dir,
                            speed     = cY,
                            radius    = rY,
                            omega     = omegaY,
                            k         = kY,
                            initialHeight = a_i,
                            initialLife = wakeParticleLife,
                            remainingLife = wakeParticleLife,
                            bucketNum = bucketY
                        };
                        waveParticleRegions[regionIdx].buckets[bucketY].Add(p);
                    }
                }
            }

            // =====================================================
            // 2) 水平排水：Kelvin 尾迹（只在下游半平面）
            // =====================================================
            Vector2 vH = new Vector2(vel.x, vel.z);
            float   vH_mag = vH.magnitude;
            float   signH  = Mathf.Sign(Q_horz);             // 目前只用来将来决定正/负振幅

            float dV_horz = Mathf.Abs(Q_horz) * dt;

            if (dV_horz > 1e-8f && vH_mag > 1e-3f)
            {
                int   bucketH = PickBucketByVolume(dV_horz, out float rH, out float kH, out float omegaH);
                float rH2     = rH * rH;

                // 总振幅同理：dV_horz ≈ 1.4535 * A_total_H * rH^2
                const float K_ampH = 1.0f;                   // 可调系数
                float A_total_H = signH * K_ampH * dV_horz / (1.4535f * rH2);
                A_total_H = Mathf.Clamp(A_total_H, -5f, 5f);

                Vector2 vH_dir    = vH / vH_mag;
                Vector2 downstream = -vH_dir;               // 尾迹在“下游”

                const float thetaK = 0.3398369095f;          // Kelvin 脊角 ≈ 19.47°
                float sigma        = Mathf.Lerp(0.20f, 0.08f, Mathf.Clamp01(vH_mag / (vH_mag + 1f)));
                float inv2sigma2   = 1f / (2f * sigma * sigma);
                float epsilon      = 0.0f;

                float[] w   = new float[dirSampleCount];
                float  wsum = 0f;

                for (int i = 0; i < dirSampleCount; i++)
                {
                    float ang = i * Mathf.PI * 2f / dirSampleCount;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

                    // 半平面裁剪：只在下游造波
                    if (Vector2.Dot(dir, downstream) <= 0f)
                    {
                        w[i] = 0f;
                        continue;
                    }

                    // 计算 dir 相对下游轴的角度 φ ∈ (-π, π]
                    float phi = Mathf.Atan2(dir.y, dir.x) - Mathf.Atan2(downstream.y, downstream.x);
                    phi = Mathf.Repeat(phi + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

                    float peakL = Mathf.Exp(-((phi - thetaK) * (phi - thetaK)) * inv2sigma2);
                    float peakR = Mathf.Exp(-((phi + thetaK) * (phi + thetaK)) * inv2sigma2);

                    // 把 |φ| > 90° 的方向衰减掉（更像 V 型尾迹）
                    float hemiWindow = Mathf.Clamp01((Mathf.PI * 0.5f - Mathf.Abs(phi)) / (Mathf.PI * 0.5f));
                    hemiWindow *= hemiWindow;

                    w[i] = hemiWindow * (peakL + peakR) + epsilon;
                    wsum += w[i];
                }

                if (wsum > 1e-6f)
                {
                    for (int i = 0; i < dirSampleCount; i++)
                    {
                        if (w[i] <= 0f) continue;

                        float ang = i * Mathf.PI * 2f / dirSampleCount;
                        Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

                        float a_i = 0.5f * A_total_H * (w[i] / wsum);
                        if (Mathf.Abs(a_i) < 1e-7f) continue;

                        Vector2 ppos = pos + 5f * dir * rH;

                        var p = new WaveParticle
                        {
                            position  = ppos,
                            height    = a_i,
                            direction = dir,
                            speed     = vH_mag,   // 这里沿用船的水平速度作为群速
                            radius    = rH,
                            omega     = omegaH,
                            k         = kH,
                            initialHeight = a_i,
                            initialLife = wakeParticleLife,
                            remainingLife = wakeParticleLife,
                            bucketNum = bucketH
                        };
                        waveParticleRegions[regionIdx].buckets[bucketH].Add(p);
                    }
                }
            }
        }

        /// <summary>
        /// 让每个 WaveParticleRegion 根据对应的 SolidHydrodynamics 移动中心（只改 xz）
        /// </summary>
        void UpdateRegionCentersFromSolids(float dt)
        {
            // 区域跟随灵敏度（可以拉到 Inspector 里变成 [SerializeField]）
            float followSpeed = 2f;          // 越大越突变
            float deadZone = 0.25f;          // 死区占比：船在区域中心 ± 25% size 内不强制跟随

            foreach (var solid in solids)
            {
                int id = solid.regionId;
                if (id < 0 || id >= waveParticleRegions.Count)
                    continue;

                var region = waveParticleRegions[id];

                // 船的世界空间投影位置
                Vector3 rbPos = solid.rb.worldCenterOfMass;
                Vector2 targetXZ = new Vector2(rbPos.x, rbPos.z);

                // 当前区域中心
                Vector2 centerXZ = new Vector2(region.center.x, region.center.y);

                // 船相对于区域中心的位置
                Vector2 local = targetXZ - centerXZ;

                // 死区：只有当船离中心太远时才推动区域中心
                float halfX = region.size.x * 0.5f;
                float halfZ = region.size.y * 0.5f;
                float deadX = halfX * deadZone;
                float deadZ = halfZ * deadZone;

                Vector2 offset = Vector2.zero;

                // x 方向
                if (local.x > deadX) offset.x = local.x - deadX;
                else if (local.x < -deadX) offset.x = local.x + deadX;

                // z 方向
                if (local.y > deadZ) offset.y = local.y - deadZ;
                else if (local.y < -deadZ) offset.y = local.y + deadZ;

                if (offset.sqrMagnitude > 1e-6f)
                {
                    // 平滑移动区域中心，避免瞬移
                    Vector2 newCenterXZ = centerXZ + offset * Mathf.Clamp01(followSpeed * dt);

                    region.center.x = newCenterXZ.x;
                    region.center.y = newCenterXZ.y;
                }
            }
        }

        // 处理所有船只的尾迹生成
        void ProcessSolidWakes(float dt)
        {
            if (solids == null) return;
            if (bucketRadii == null || bucketRadii.Length == 0) return;

            int totalWakeParticles = 0;
            float maxFlux = 0f;

            float sumAbsAmplitude = 0f;
            float maxAbsAmplitude = 0f;

            const float VOLUME_COEFF = 0.297f * Mathf.PI; // V = 0.297*pi*r^2*A

            foreach (var solid in solids)
            {
                if (solid == null || solid.waveGenDataArray == null) continue;

                // 船的水平速度门限：静止/低速时不生成尾迹（避免细小扰动持续注入能量造成堆积）
                float shipSpeedH = 0f;
                if (solid.rb != null)
                {
                    Vector3 v = solid.rb.velocity;
                    shipSpeedH = new Vector2(v.x, v.z).magnitude;
                }
                if (shipSpeedH < wakeMinShipHorizontalSpeed)
                    continue;

                // 遍历回读回来的波浪生成数据
                // 注意：这里是遍历所有三角形，如果面片数很多，可能会有性能压力
                // 建议在 Compute Shader 中做一次 Reduce 或者 AppendBuffer 筛选
                // 但按你的要求，先直接遍历
                for (int i = 0; i < solid.waveGenDataArray.Length; i++)
                {
                    var data = solid.waveGenDataArray[i];
                    float fluxV = data.flux; // m^3/s (vertical)
                    float fluxH = data.horzFlux; // m^3/s (horizontal)

                    if (Mathf.Abs(fluxV) > maxFlux) maxFlux = Mathf.Abs(fluxV);
                    if (Mathf.Abs(fluxH) > maxFlux) maxFlux = Mathf.Abs(fluxH);

                    // 阈值过滤：忽略微小的通量
                    bool hasVert = Mathf.Abs(fluxV) >= 1e-4f;
                    bool hasHorz = Mathf.Abs(fluxH) >= 1e-4f;
                    if (!hasVert && !hasHorz) continue;

                    // 计算本次时间步长内的排水体积
                    float volumeV = fluxV * dt * wakeGenerationScale;
                    float volumeH = fluxH * dt * wakeGenerationScale;

                    // 面片点速度（用于决定粒子传播方向/速度）
                    Vector3 pos = data.position;
                    Vector3 normal = data.normal;
                    Vector3 ptVel = Vector3.zero;
                    if (solid.rb != null)
                        ptVel = solid.rb.GetPointVelocity(pos);

                    // --- 由体积反推桶半径 ---
                    // 先假设 r=A，则 V = 0.297*pi*r^3 => r0 = cbrt(|V|/(0.297*pi))
                    float absV = Mathf.Abs(volumeV);
                    float absH = Mathf.Abs(volumeH);
                    if (absV < 1e-10f && absH < 1e-10f) continue;

                    // NOTE: 下面的“选桶+算振幅”会对竖直/水平各做一次。

                    // -------------------------------------------------
                    // a) 竖直排水（上下压水/抽水）
                    // -------------------------------------------------
                    if (hasVert && absV >= 1e-10f)
                    {
                        float r0 = Mathf.Pow(absV / VOLUME_COEFF, 1f / 3f);

                        int bucketIdx = wakeTargetBucketIdx;
                        float rBucket = wakeTargetRadius;

                        float bestDiff = float.PositiveInfinity;
                        for (int bi = 0; bi < bucketRadii.Length; bi++)
                        {
                            float diff = Mathf.Abs(bucketRadii[bi] - r0);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bucketIdx = bi;
                                rBucket = bucketRadii[bi];
                            }
                        }

                        if (bucketIdx >= 0 && bucketIdx < bucketRadii.Length && rBucket > 1e-6f)
                        {
                            float amplitude = volumeV / (VOLUME_COEFF * rBucket * rBucket);
                            amplitude = Mathf.Clamp(amplitude, -2f, 2f);

                            float absAmp = Mathf.Abs(amplitude);
                            sumAbsAmplitude += absAmp;
                            if (absAmp > maxAbsAmplitude) maxAbsAmplitude = absAmp;

                            Vector3 offsetDir = new Vector3(normal.x, 0, normal.z).normalized;
                            if (offsetDir == Vector3.zero) offsetDir = Vector3.up;
                            Vector3 spawnPos = pos + offsetDir * (rBucket * 1.5f);

                            WaveParticle p = new WaveParticle();
                            p.position = new Unity.Mathematics.float2(spawnPos.x, spawnPos.z);

                            Vector3 nN = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
                            float vN = Vector3.Dot(ptVel, nN);
                            float speed2 = Mathf.Abs(vN);

                            Vector2 dir2 = new Vector2(nN.x, nN.z);
                            if (dir2.sqrMagnitude > 1e-8f)
                            {
                                dir2.Normalize();
                                dir2 *= Mathf.Sign(vN == 0f ? 1f : vN);
                            }
                            else
                            {
                                Vector2 velXZ = new Vector2(ptVel.x, ptVel.z);
                                if (velXZ.sqrMagnitude > 1e-8f)
                                    dir2 = velXZ.normalized;
                            }

                            if (dir2.sqrMagnitude < 1e-8f)
                                dir2 = Vector2.up;

                            p.direction = new Unity.Mathematics.float2(dir2.x, dir2.y);
                            p.height = amplitude;
                            p.radius = rBucket;
                            p.initialHeight = amplitude;
                            p.initialLife = wakeParticleLife;
                            p.remainingLife = wakeParticleLife;

                            if (converter != null && converter._omegas != null && bucketIdx < converter._omegas.Length)
                            {
                                p.omega = converter._omegas[bucketIdx];
                                p.k = (p.omega * p.omega) / wavesSettings.g;
                                float cg = 0.5f * wavesSettings.g / p.omega;
                                p.speed = (speed2 > 1e-4f) ? speed2 : cg;
                            }
                            else
                            {
                                p.k = 1.0f / rBucket;
                                p.omega = Mathf.Sqrt(9.81f * p.k);
                                float cg = 0.5f * 9.81f / p.omega;
                                p.speed = (speed2 > 1e-4f) ? speed2 : cg;
                            }

                            p.speed = Mathf.Max(p.speed, wakeMinParticleSpeed);

                            p.bucketNum = bucketIdx;

                            for (int rIdx = 0; rIdx < waveParticleRegions.Count; rIdx++)
                            {
                                var region = waveParticleRegions[rIdx];
                                if (region.Contains(p.position, p.radius))
                                {
                                    if (region.buckets != null && region.buckets.Length > bucketIdx)
                                    {
                                        region.buckets[bucketIdx].Add(p);
                                        totalWakeParticles++;
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    // -------------------------------------------------
                    // b) 水平排水（下游尾迹）
                    // -------------------------------------------------
                    if (hasHorz && absH >= 1e-10f)
                    {
                        float r0 = Mathf.Pow(absH / VOLUME_COEFF, 1f / 3f);

                        int bucketIdx = wakeTargetBucketIdx;
                        float rBucket = wakeTargetRadius;

                        float bestDiff = float.PositiveInfinity;
                        for (int bi = 0; bi < bucketRadii.Length; bi++)
                        {
                            float diff = Mathf.Abs(bucketRadii[bi] - r0);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bucketIdx = bi;
                                rBucket = bucketRadii[bi];
                            }
                        }

                        if (bucketIdx >= 0 && bucketIdx < bucketRadii.Length && rBucket > 1e-6f)
                        {
                            float amplitude = volumeH / (VOLUME_COEFF * rBucket * rBucket);
                            amplitude = Mathf.Clamp(amplitude, -2f, 2f);

                            float absAmp = Mathf.Abs(amplitude);
                            sumAbsAmplitude += absAmp;
                            if (absAmp > maxAbsAmplitude) maxAbsAmplitude = absAmp;

                            Vector2 velXZ = new Vector2(ptVel.x, ptVel.z);
                            Vector2 downstream = (velXZ.sqrMagnitude > 1e-8f) ? (-velXZ.normalized) : Vector2.zero;

                            Vector3 spawnPos = pos;
                            if (downstream != Vector2.zero)
                                spawnPos += new Vector3(downstream.x, 0f, downstream.y) * (rBucket * 2.0f);

                            WaveParticle p = new WaveParticle();
                            p.position = new Unity.Mathematics.float2(spawnPos.x, spawnPos.z);

                            if (downstream != Vector2.zero)
                                p.direction = new Unity.Mathematics.float2(downstream.x, downstream.y);
                            else
                                p.direction = new Unity.Mathematics.float2(0f, 1f);

                            p.height = amplitude;
                            p.radius = rBucket;
                            p.initialHeight = amplitude;
                            p.initialLife = wakeParticleLife;
                            p.remainingLife = wakeParticleLife;

                            float speedH = velXZ.magnitude;
                            if (converter != null && converter._omegas != null && bucketIdx < converter._omegas.Length)
                            {
                                p.omega = converter._omegas[bucketIdx];
                                p.k = (p.omega * p.omega) / wavesSettings.g;
                                float cg = 0.5f * wavesSettings.g / p.omega;
                                p.speed = (speedH > 1e-4f) ? speedH : cg;
                            }
                            else
                            {
                                p.k = 1.0f / rBucket;
                                p.omega = Mathf.Sqrt(9.81f * p.k);
                                float cg = 0.5f * 9.81f / p.omega;
                                p.speed = (speedH > 1e-4f) ? speedH : cg;
                            }

                            p.speed = Mathf.Max(p.speed, wakeMinParticleSpeed);

                            p.bucketNum = bucketIdx;

                            for (int rIdx = 0; rIdx < waveParticleRegions.Count; rIdx++)
                            {
                                var region = waveParticleRegions[rIdx];
                                if (region.Contains(p.position, p.radius))
                                {
                                    if (region.buckets != null && region.buckets.Length > bucketIdx)
                                    {
                                        region.buckets[bucketIdx].Add(p);
                                        totalWakeParticles++;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // if (fixedFrameCnt % 60 == 0)
            {
                if (logWakeStatsEachFrame)
                {
                    float avgAbsAmplitude = (totalWakeParticles > 0) ? (sumAbsAmplitude / totalWakeParticles) : 0f;
                    Debug.Log($"[WaveParticleSystem][Wake] frame={fixedFrameCnt} count={totalWakeParticles} maxAbsAmp={maxAbsAmplitude:F6} avgAbsAmp={avgAbsAmplitude:F6} maxAbsFlux={maxFlux:F6}");
                }
            }
        }

        void ReleaseRenderTextures(ref RenderTexture[] textures)
        {
            if (textures == null) return;

            for (int i = 0; i < textures.Length; i++)
            {
                var rt = textures[i];
                if (rt == null) continue;

                rt.Release();
                Destroy(rt);
                textures[i] = null;
            }

            textures = null;
        }

        void ReleaseTexture2Ds(ref Texture2D[] textures)
        {
            if (textures == null) return;
            for (int i = 0; i < textures.Length; i++)
            {
                if (textures[i] == null) continue;
                Destroy(textures[i]);
                textures[i] = null;
            }
            textures = null;
        }



    }


}
