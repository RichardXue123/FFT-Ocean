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
        private ComputeBuffer particleBuffer2;
        public int frameCnt;

        // 基础配置参数
        [Header("Wave Parameters")]
        [SerializeField] private float m_baseRadius = 0.2f;
        [SerializeField] private float m_baseHeight = 0.1f;
        [SerializeField] private float m_speed = 0.3f;
        [SerializeField] private Vector2 m_mainDirection = Vector2.right;
        //[Range(0, 1)] public float radiusScale = 0.25f;
        [Range(0, 20)] public float windSpeed = 5f;
        [SerializeField] float waterDepth = 500f;
        [SerializeField] public float maxRadius= 1000f;
        [SerializeField] public int particleCnt = 10000;


        [Header("Rendering")]
        public Mesh ballMesh;
        public Material ballMaterial;
        public Material instanceMaterial;

        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public RenderTexture heightMap;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);

        [SerializeField] int resolution = 256;
        [SerializeField] float planeSize = 10f;
        [SerializeField] float oceanSize = 100f;
        [SerializeField] Material oceanMaterial;

        MeshUtils.Element OceanCenter;

        public UnityEngine.UI.RawImage heightMapDisplay;

        void Awake()
        {
            particles = new List<WaveParticle>();
        }

        public void Start()
        {
            //InitializeSimpleWave();
            particles.Clear();
            particles = GenerateParticles(particleCnt, windSpeed);
            //particles = GenerateParticlesTest(count: 1, windSpeed: 10.0f);
            Debug.Log($"Generated {particles.Count} basic wave particles");
            // 初始化 ComputeBuffer
            particleBuffer = new ComputeBuffer(particles.Count, sizeof(float) * 4);


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
            oceanMaterial.SetTexture("_HeightMap", heightMap);
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
                particles[i].UpdatePosition(deltaTime,planeSize,oceanSize);
                particles[i].UpdateParticle(time);
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

        List<WaveParticle> GenerateParticles(int count, float windSpeed) //JONSWAP 适用 风速: 3m/s-20m/s
        {
            float scale = planeSize / resolution;
            
            Debug.Log("scale: " + scale);
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

        // 波粒子初始化方法 
        public void InitializeSimpleWave()
        {
            //TODO：
            //优化初始化方案
            // 清空现有粒子
            particles.Clear();

            // 基础参数计算（参考OnCreate）
            const float gravity = 9.8f;
            float k = 2 * Mathf.PI / m_baseRadius; // 波数k = 2π/λ
            float actualSpeed = Mathf.Sqrt(gravity / k); // 相速度公式

            // 生成单排粒子
            const int particleCount = 100;
            const float spacing = 10f/particleCount; // 粒子间距

            for (int i = 0; i < particleCount; i++)
            {
                WaveParticle particle = new WaveParticle
                {
                    position = new Vector2(-5 + i * spacing, 5 - i * spacing), 
                    direction = m_mainDirection.normalized,
                    baseHeight = m_baseHeight,
                    speed = actualSpeed,
                    //speed = 0,
                    radius = m_baseRadius,
                    waveNumber = k
                };

                // 添加正负粒子对
                particles.Add(particle);

                // 添加负粒子（高度取反）
                //WaveParticle negativeParticle = particle.GetNegative();
                //particles.Add(negativeParticle);
            }

            Debug.Log($"Generated {particles.Count} basic wave particles");
            // 初始化 ComputeBuffer
            particleBuffer = new ComputeBuffer(particles.Count, sizeof(float) * 4);
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
            instanceMaterial.SetBuffer("_ParticleBuffer", particleBuffer);

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
                Debug.Log($"Particle {i}: {debugData[i]}");
                p_cnt++;
            }
            Debug.Log($"Total big particles: {p_cnt2}");
            Debug.Log($"Highest particle height: {highest}");

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
