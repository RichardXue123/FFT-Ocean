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

        // 基础配置参数
        [Header("Wave Parameters")]
        [SerializeField] private float m_baseRadius = 0.15f;
        [SerializeField] private float m_baseHeight = 0.05f;
        [SerializeField] private float m_speed = 0.5f;
        [SerializeField] private Vector2 m_mainDirection = Vector2.right;

        [Header("Rendering")]
        //public Mesh instanceMesh;
        public Material instanceMaterial;
        public float instanceScale = 0.1f;

        [SerializeField] public ComputeShader heightMapComputeShader;
        [SerializeField] public RenderTexture heightMap;
        [SerializeField] public Vector2Int textureSize = new Vector2Int(256, 256);

        void Awake()
        {
            particles = new List<WaveParticle>();
        }

        public void Start()
        {
            InitializeSimpleWave();

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

        }
        public void Update()
        {
            float deltaTime = Time.deltaTime;
            float time = Time.time;
            // 更新粒子位置和速度
            for (int i = 0; i < particles.Count; i++)
            {
                WaveParticle p = particles[i];
                p.UpdatePosition(deltaTime);
                p.UpdateParticle(time);
                particles[i] = p;
            }
            UpdateParticleBuffer();
            //Graphics.DrawMeshInstanced(instanceMesh, 0, instanceMaterial, GetMatrices(), particles.Count);

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
            instanceMaterial.SetTexture("_HeightMap", heightMap);
        }

        private void OnDestroy()
        {
            if (particleBuffer != null)
            {
                particleBuffer.Release();
                particleBuffer = null;
            }
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
            const int particleCount = 10;
            const float spacing = 1.0f; // 粒子间距

            for (int i = 0; i < particleCount; i++)
            {
                WaveParticle particle = new WaveParticle
                {
                    position = new Vector2(i * spacing, 0), // 沿X轴排列
                    direction = m_mainDirection.normalized,
                    height = m_baseHeight,
                    speed = actualSpeed,
                    radius = m_baseRadius,
                    waveNumber = k
                };

                // 添加正负粒子对
                particles.Add(particle);

                // 添加负粒子（高度取反）
                WaveParticle negativeParticle = particle;
                negativeParticle.height *= -1;
                negativeParticle.position += new Vector2(0, 0.5f); // 偏移位置
                particles.Add(negativeParticle);
            }

            Debug.Log($"Generated {particles.Count} basic wave particles");
            // 初始化 ComputeBuffer
            particleBuffer = new ComputeBuffer(particles.Count, sizeof(float) * 4);
        }
        void UpdateParticleBuffer()
        {
            Vector4[] particleData = new Vector4[particles.Count];
            for (int i = 0; i < particles.Count; i++)
            {
                particleData[i] = particles[i].ToVector4();
            }

            particleBuffer.SetData(particleData);
            instanceMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        }
        Matrix4x4[] GetMatrices()
        {
            Matrix4x4[] matrices = new Matrix4x4[particles.Count];
            for (int i = 0; i < particles.Count; i++)
            {
                Vector3 pos = new Vector3(particles[i].position.x, particles[i].height, particles[i].position.y);
                matrices[i] = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one * instanceScale);
            }
            return matrices;
        }
    }


}
