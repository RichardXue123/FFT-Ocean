using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        [Header("buckets相关参数")]
        [SerializeField] public RenderTexture[] heightSlicesInt;   // Tex2DArray: RInt
        [SerializeField] public RenderTexture[] heightSlicesFloat; // Tex2DArray: RFloat (滤波输入/输出)
        [SerializeField] public RenderTexture[] heightSlicesTmp; // 滤波 ping-pong 用
        [SerializeField] public ComputeShader SplatBucketsCS;
        [SerializeField] public ComputeShader Int2FloatArrayCS;
        [SerializeField] public ComputeShader SeparableFilterCS;   // 横/纵两个kernel
        [SerializeField] public ComputeShader ReduceSlicesCS;      // 合并 slices → RFloat

        private ComputeBuffer[][] particleBuffersPerBucket; // [region][bucket]
        private int[] bucketCounts;                         // 复用计数缓存
        private float[] bucketRadii;                        // 每桶代表半径(世界单位)
        private ComputeBuffer radiiBuf;

        [Header("Rendering")]
        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public ComputeShader SplatComputeShader;
        [SerializeField] public ComputeShader Int2FloatComputeShader;
        [SerializeField] public ComputeShader Height2NormalComputeShader;
        [SerializeField] public RenderTexture[] heightMap;
        [SerializeField] public RenderTexture[] normalMap;
        [SerializeField] public RenderTexture[] displacementMap;
        //[SerializeField] public ComputeBuffer[] particleBuffers;
        //[SerializeField] public ComputeBuffer[] particleDirBuffers;
        [SerializeField] public int resolution = 256;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);
        [SerializeField] public float blendRange = 0.2f;
        [SerializeField] public float blendStrength = 0.5f;
        [SerializeField] public float oceanSize = 100f;
        //[SerializeField] public NativeArray<float>[] heightMaps;

        [SerializeField] Material oceanMaterial;

        Vector4[] particleData;
        Vector2[] particleDirData;

        [Header("Debug")]
        float curTime;
        float prevTime;
        float perTime;
        // 在类里缓存一个可复用的2D RT
        RenderTexture _slicePreviewRT;

        //public int[] debugOut = new int[4];
        public UnityEngine.UI.RawImage heightMapDisplay;
        public UnityEngine.UI.RawImage normalMapDisplay;
        public UnityEngine.UI.RawImage displacementMapDisplay;

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
            particleData = new Vector4[MAX_PARTICLES];
            particleDirData = new Vector2[MAX_PARTICLES];
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
            displacementMap = new RenderTexture[MAX_REGIONS];

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

                heightMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true }; heightMap[i].Create();
                normalMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true }; normalMap[i].Create();
                displacementMap[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true }; displacementMap[i].Create();

                // 每个 region 为每个桶准备粒子 buffer（复用，容量 MAX_PARTICLES）
                if (particleBuffersPerBucket == null)
                    particleBuffersPerBucket = new ComputeBuffer[MAX_REGIONS][];
                particleBuffersPerBucket[i] = new ComputeBuffer[N_omega];
                for (int b = 0; b < N_omega; b++)
                    particleBuffersPerBucket[i][b] = new ComputeBuffer(MAX_PARTICLES, sizeof(float) * 4);
            }

            bucketCounts = new int[N_omega];
            bucketRadii = new float[N_omega]; // 计算一次代表半径
            //预计算 半径桶
            for (int b = 0; b < N_omega; b++)
            {
                // 代表半径（按你的取样：ω_min + Δω*(b+0.5)）
                float omega_p = wavesSettings.spectrums[0].peakOmega;
                float omega_min = omega_p * 0.5f;
                float omega_max = omega_p * 2.5f;
                float delta_omega = (omega_max - omega_min) / N_omega;
                float omega = omega_min + delta_omega * (b + 0.5f);
                float k = omega * omega / wavesSettings.g;
                bucketRadii[b] = Mathf.PI / k; // r = π/k
            }

            radiiBuf = new ComputeBuffer(N_omega, sizeof(float));
            radiiBuf.SetData(bucketRadii);

            fixedFrameCnt = 0;
            converter.Initialize(N_omega);
            oceanMaterial.SetFloat("_ParticleHeightScale", 1.0f);


            //测试用
            _slicePreviewRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
            _slicePreviewRT.Create();
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
                Debug.Log("time per frame: " + perTime);
                prevTime = curTime;
            }
            particleCnt = 0;

            oceanMaterial.SetInt("_RegionCount", waveParticleRegions.Count);
            oceanMaterial.SetFloat("_BlendRange", blendRange);
            oceanMaterial.SetFloat("_BlendStrength", blendStrength);

            var sw = Stopwatch.StartNew();      // 等同于 new Stopwatch(); sw.Start();

            for (int r = 0; r < waveParticleRegions.Count; r++)
            {
                sw.Restart();
                var region = waveParticleRegions[r];

                // 0) 按需生成边界粒子（你原来的逻辑）
                using (var edgeParticles = converter.GenerateParticlesFromSpectrum(
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

                for (int b = 0; b < N_omega; b++)
                {
                    int count = bucketCounts[b];
                    if (count == 0) continue;

                    SplatBucketsCS.SetBuffer(kS, "_Particles", particleBuffersPerBucket[r][b]);
                    SplatBucketsCS.SetInt("_ParticleCount", count);

                    int rp = Mathf.CeilToInt(bucketRadii[b] / (region.size.x / resolution));
                    SplatBucketsCS.SetInt("_RadiusPixels", rp);
                    SplatBucketsCS.SetInt("_Slice", b);

                    SplatBucketsCS.Dispatch(kS, Mathf.CeilToInt(count / 64f), 1, 1);
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

                // debug 高度图
                //EnqueueAverageHeightLogCPU(r);

                // 9) 法线
                int kN = Height2NormalComputeShader.FindKernel("CSMain");
                Height2NormalComputeShader.SetTexture(kN, "HeightTex", heightMap[r]);
                Height2NormalComputeShader.SetTexture(kN, "NormalTex", normalMap[r]);
                Height2NormalComputeShader.SetInts("TexSize", resolution, resolution);
                Height2NormalComputeShader.Dispatch(kN, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

                // 10) 传材质
                oceanMaterial.SetTexture($"_ParticleHeightMap{r}", heightMap[r]);
                oceanMaterial.SetTexture($"_ParticleNormalMap{r}", normalMap[r]);
                oceanMaterial.SetVector($"_RegionCenter{r}", region.center);
                oceanMaterial.SetVector($"_RegionSize{r}", region.size);
            }

            // 看第0个region的第b个bucket
            //ShowBucketSlice(0, 0, heightMapDisplay);  // 直接复用已有的 RawImage 显示

            //DebugSliceStats(0, 0);
            //DebugSliceStats(0, 1);
            //DebugSliceStats(0, 2);
            //DebugSliceStats(0, 3);
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

        void UpdateRegionParticlesBuckets(int idx, float dt)
        {
            var region = waveParticleRegions[idx];
            var handles = new Unity.Collections.NativeArray<JobHandle>(N_omega, Allocator.Temp);

            for (int b = 0; b < N_omega; b++)
            {
                var arr = region.buckets[b].AsArray();
                int len = arr.Length;

                // 即使 len == 0 也清一下 scratch，避免残留
                region.EnsureScratchCapacity(b, len);

                if (len == 0)
                {
                    handles[b] = default;
                    continue;
                }

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
                handles[b] = job.Schedule(len, 64);
            }

            JobHandle.CompleteAll(handles);
            handles.Dispose();

            // 零拷贝切换：scratch -> buckets
            region.SwapBuckets();
        }


        void UpdateRegionParticleBufferBuckets(int idx)
        {
            var region = waveParticleRegions[idx];
            for (int b = 0; b < N_omega; b++)
            {
                int count = region.buckets[b].Length;
                bucketCounts[b] = count;
                if (count == 0) continue;

                // 打包到复用数组（或改用 BeginWrite/EndWrite + NativeArray）
                for (int i = 0; i < count; i++)
                    particleData[i] = region.buckets[b][i].ToVector4();

                particleBuffersPerBucket[idx][b].SetData(particleData, 0, 0, count);
            }
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


        private void OnDestroy()
        {

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
            //float K1 = 0.1f;
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
                //waveParticleRegions[regionIdx].particles.Add(particle);
            }
        }


    }


}
