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

        // 基础配置参数
        [Header("Wave Parameters")]
        [SerializeField] private float m_baseRadius = 0.15f;
        [SerializeField] private float m_baseHeight = 0.05f;
        [SerializeField] private float m_speed = 0.5f;
        [SerializeField] private Vector2 m_mainDirection = Vector2.right;
        


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
        }

        void Awake()
        {
            particles = new List<WaveParticle>();
        }

        public void Start()
        {

        }
        public void Update()
        {
            float deltaTime = Time.deltaTime;
            // 更新粒子位置和速度
            foreach (var p in particles)
            {
                p.UpdatePosition(deltaTime);
                p.UpdatePosition(deltaTime);
            }
        }
    }


}
