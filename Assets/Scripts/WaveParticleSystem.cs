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

        [Header("Rendering")]
        public Mesh ballMesh;
        public Material ballMaterial;
        public Material instanceMaterial;

        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public RenderTexture heightMap;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);

        public UnityEngine.UI.RawImage heightMapDisplay;

        void Awake()
        {
            particles = new List<WaveParticle>();
        }

        public void Start()
        {
            //InitializeSimpleWave();
            particles.Clear();
            particles = GenerateParticles(count: 100, windSpeed: 5f);
            Debug.Log($"Generated {particles.Count} basic wave particles");
            // 初始化 ComputeBuffer
            particleBuffer = new ComputeBuffer(particles.Count, sizeof(float) * 4);


            // 初始化 heightMap
            heightMap = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.RFloat);
            heightMap.enableRandomWrite = true;
            heightMap.Create();

            if (heightMapComputeShader != null)
            {
                Debug.Log("有绑定 ComputeShader。");
                if (heightMapComputeShader.HasKernel("CSMain")) { 
                    Debug.Log("有csmain");
                };
            }
            frameCnt = 0;
            instanceMaterial.SetTexture("_HeightMap", heightMap);
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
                particles[i].UpdatePosition(deltaTime);
                particles[i].UpdateParticle(time);
            }
            UpdateParticleBuffer();
            Graphics.DrawMeshInstanced(ballMesh, 0, ballMaterial, GetMatrices(), particles.Count);

            // 发送到 ComputeShader
            int kernel = heightMapComputeShader.FindKernel("CSMain");
            heightMapComputeShader.SetTexture(kernel, "Result", heightMap);
            heightMapComputeShader.SetBuffer(kernel, "Particles", particleBuffer);
            heightMapComputeShader.SetInts("TextureSize", textureSize.x, textureSize.y);
            heightMapComputeShader.SetInt("ParticleCount", particles.Count);
            heightMapComputeShader.SetFloat("time", Time.time);

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

        List<WaveParticle> GenerateParticles(int count, float windSpeed)
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
                float S = JONSWAP(ω, omegaP, alpha, gamma);
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
                p.radius = 2 * Mathf.PI / p.waveNumber * 0.25f; //???
                p.speed = Mathf.Sqrt(g / p.waveNumber) * 0.1f;
                list.Add(p);

            }
            return list;
        }

        static float JONSWAP(float omega, float omegaP, float alpha, float gamma)
        {
            float sigma = omega <= omegaP ? 0.07f : 0.09f;
            float r = Mathf.Exp(-((omega - omegaP) * (omega - omegaP)) / (2 * sigma * sigma * omegaP * omegaP));
            return alpha * 9.81f * 9.81f * Mathf.Pow(omega, -5)
                 * Mathf.Exp(-1.25f * Mathf.Pow(omegaP / omega, 4))
                 * Mathf.Pow(gamma, r);
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
            /*Vector4[] debugData = new Vector4[particles.Count];
            particleBuffer.GetData(debugData);
            for (int i = 0; i < 100; i++)
            {
                Debug.Log($"Particle {i}: {debugData[i]}");
            }*/
            // 调试：读取 ComputeBuffer 数据（注意：这会增加开销，只在调试时使用）
            /*            Vector4[] debugData = new Vector4[particles.Count];
                        particleBuffer.GetData(debugData);
                        // 打印前几个数据，确保数据发生变化
                        for (int i = 0; i < Mathf.Min(5, debugData.Length); i++)
                        {
                            Debug.Log($"Particle {i}: {debugData[i]}");
                        }*/

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
