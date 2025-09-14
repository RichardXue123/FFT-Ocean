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
using Debug = UnityEngine.Debug;

namespace Assets.Scripts
{
    public class WaveParticleSystem : MonoBehaviour
    {


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

        /*static double[] p = {0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7};*/

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

            converter.Initialize(wavesSettings,N_omega,N_theta);
            bucketCounts = new int[N_omega];

            bucketOmega = converter._omegas;
            bucketRadii = converter._radii;
            deltaOmega = converter._deltaOmegas;
            radiiBuf = new ComputeBuffer(N_omega, sizeof(float));
            radiiBuf.SetData(bucketRadii);

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
                    deltaTime: dt))
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
                SyncRegionTexturesToCPU(r);

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
                    regionHalf = region.size * 0.5f
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


        void OnDestroy()
        {
            if (particleBuffersPerBucket != null)
            {
                for (int r = 0; r < particleBuffersPerBucket.Length; r++)
                    if (particleBuffersPerBucket[r] != null)
                        for (int b = 0; b < particleBuffersPerBucket[r].Length; b++)
                            particleBuffersPerBucket[r][b]?.Dispose();
            }

            if (particleVelBuffersPerBucket != null)
            {
                for (int r = 0; r < particleVelBuffersPerBucket.Length; r++)
                    if (particleVelBuffersPerBucket[r] != null)
                        for (int b = 0; b < particleVelBuffersPerBucket[r].Length; b++)
                            particleVelBuffersPerBucket[r][b]?.Dispose();
            }
            radiiBuf?.Dispose();
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



        public void GenerateWaveParticles(int regionIdx, Vector2 pos, float V, Vector3 vel, float dt, int dirSampleCount = 16)
        {
            //Debug.Log("v:" + vel+" "+dt);
            if (regionIdx < 0 || regionIdx >= waveParticleRegions.Count) return;
            if (dirSampleCount <= 0) return;
            //var region = waveParticleRegions[regionIdx];

            float V_inwater = Mathf.Max(0f, V);
            float S_inwater = Mathf.Pow(V_inwater , (2f / 3f));
            if (V_inwater <= 0f) return;

            float g = wavesSettings.g > 0 ? wavesSettings.g : 9.81f;

            // --- 小工具：根据相速 c 选最近半径桶 ---
            int PickBucketBySpeed(float cSpeed, out float radiusOut, out float kOut, out float omegaOut)
            {
                float dV = S_inwater * cSpeed * dt;
                float r = Mathf.Pow(dV / 1.4535f, 1 / 3);
                int best = 0; float diff = float.MaxValue;
                for (int b = 0; b < bucketRadii.Length; b++)
                {
                    float d = Mathf.Abs(bucketRadii[b] - r);
                    if (d < diff) { diff = d; best = b; }
                }
                best = Mathf.Clamp(best, 0, bucketRadii.Length - 1);
                radiusOut = bucketRadii[best];
                kOut = Mathf.PI / Mathf.Max(radiusOut, 1e-3f);
                omegaOut = Mathf.Sqrt(g * kOut);
                return best;
            }

            // ==============================
            // 1) 竖直分量：均匀环
            // ==============================
            float vy = vel.y;
            float cY = Mathf.Abs(vy);
            if (cY > 1e-3f)
            {
                int bucketY = PickBucketBySpeed(cY, out float rY, out float kY, out float omegaY);

                // 向下( vy<0 )压水 -> 正振幅；向上( vy>0 )吸波 -> 负振幅
                float signY = (vy >= 0f) ? -1f : 1f;

                // 系数可按项目调（竖直项）
                const float K_ampY = 2f;
                float A_total_Y = K_ampY * signY * S_inwater * cY * dt / (1.4535f * bucketRadii[bucketY] * bucketRadii[bucketY]);
                A_total_Y = Mathf.Clamp(A_total_Y, -5f, 5f);
                //Debug.Log("S:" + S_inwater+ "cY:"+cY + "Total A_Y:" +A_total_Y+" Radius Y:"+bucketRadii[bucketY]);
                float a_i = A_total_Y / dirSampleCount; // 均匀分摊
                if (Mathf.Abs(a_i) > 1e-7f)
                {
                    for (int i = 0; i < dirSampleCount; i++)
                    {
                        float ang = i * Mathf.PI * 2f / dirSampleCount;
                        Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                        var ppos = pos + dir * rY;

                        var p = new WaveParticle
                        {
                            position = ppos,
                            height = a_i,
                            direction = dir,
                            speed = cY,
                            radius = rY,
                            omega = omegaY,
                            k = kY,
                            bucketNum = bucketY
                        };
                        waveParticleRegions[regionIdx].buckets[bucketY].Add(p);
                    }
                }
            }

            // ==============================
            // 2) 水平分量：Kelvin 尾迹双峰（只在下游半平面）
            // ==============================
            Vector2 vH = new Vector2(vel.x, vel.z);
            float cH = vH.magnitude;
            if (cH > 1e-3f)
            {
                int bucketH = PickBucketBySpeed(cH, out float rH, out float kH, out float omegaH);

                Vector2 vH_dir = vH / cH;
                Vector2 downstream = -vH_dir; // 仅允许下游半平面
                                              // Kelvin 脊角（≈19.47°）
                const float thetaK = 0.3398369095f;

                // 高斯峰宽度（σ）：越小越尖锐；可随速度调
                float sigma = Mathf.Lerp(0.20f, 0.08f, Mathf.Clamp01(cH / (cH + 1f)));
                float inv2sigma2 = 1f / (2f * sigma * sigma);
                float epsilon = 0.0f; // 或者 0.01f，尽量小，避免前向伪峰

                float[] w = new float[dirSampleCount];
                float wsum = 0f;

                for (int i = 0; i < dirSampleCount; i++)
                {
                    float ang = i * Mathf.PI * 2f / dirSampleCount;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

                    // 半平面裁剪：只在下游(与 -vH_dir 同向)造波
                    if (Vector2.Dot(dir, downstream) <= 0f)
                    {
                        w[i] = 0f;
                        continue;
                    }

                    // 计算 dir 相对“下游轴”的有符号夹角 φ ∈ (-π, π]
                    float phi = Mathf.Atan2(dir.y, dir.x) - Mathf.Atan2(downstream.y, downstream.x);
                    // wrap 到 [-π, π]
                    phi = Mathf.Repeat(phi + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

                    // 两个高斯峰，中心在 ±thetaK
                    float peakL = Mathf.Exp(-((phi - thetaK) * (phi - thetaK)) * inv2sigma2);
                    float peakR = Mathf.Exp(-((phi + thetaK) * (phi + thetaK)) * inv2sigma2);

                    // 可选：把 |φ| 超过 90° 的方向衰减甚至置零（更干净的 V 型）
                    float hemiWindow = Mathf.Clamp01((Mathf.PI * 0.5f - Mathf.Abs(phi)) / (Mathf.PI * 0.5f));
                    hemiWindow = hemiWindow * hemiWindow; // 平滑点

                    w[i] = hemiWindow * (peakL + peakR) + epsilon;
                    wsum += w[i];
                }

                if (wsum < 1e-6f) return;

                // 水平项总振幅（沿用你的体积-速度-时间缩放）
                // 系数可按项目调（竖直项）
                const float K_ampH = 1f;
                float A_total_H = K_ampH * S_inwater * cH * dt / (1.4535f * bucketRadii[bucketH] * bucketRadii[bucketH]);
                A_total_H = Mathf.Clamp(A_total_H, -5f, 5f);

                for (int i = 0; i < dirSampleCount; i++)
                {
                    if (w[i] <= 0f) continue;

                    float ang = i * Mathf.PI * 2f / dirSampleCount;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    float a_i = A_total_H * (w[i] / wsum);
                    if (Mathf.Abs(a_i) < 1e-7f) continue;

                    var ppos = pos + dir * rH;

                    var p = new WaveParticle
                    {
                        position = ppos,
                        height = a_i,
                        direction = dir,
                        speed = cH,
                        radius = rH,
                        omega = omegaH,
                        k = kH,
                        bucketNum = bucketH
                    };
                    waveParticleRegions[regionIdx].buckets[bucketH].Add(p);
                }
            }

        }



    }


}
