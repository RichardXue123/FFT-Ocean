using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using UnityEngine;
using System.Text;
using System.Threading.Tasks;

namespace Assets.Scripts
{
    // 波粒子核心数据结构
    [System.Serializable]
    public struct WaveParticle
    {
        // 基础物理属性
        public Vector2 position;      // 当前位置（原WavePos）
        public Vector2 direction;     // 传播方向（原WaveDir）
        public float height;          // 波峰高度（原WaveHeight）
        public float speed;           // 传播速度（原WaveSpeed）
        public float radius;          // 影响半径（原Radius）
        public float waveNumber;      // 波数k（原WaveVector）

        // 相位控制
        //public float phaseOffset;     // 相位偏移量
        //public float angularFrequency;// 角频率ω=√(gk)


        // 更新波粒子的高度（根据时间和位置计算）
        public void UpdateParticle(float time)
        {
            // 计算波动的高度，假设是简单的正弦波传播
            // 波动传播方程: height = A * sin(k * x - ω * t)
            // A：振幅，k：波数，ω：角频率，t：时间，x：位置

            float omega = Mathf.Sqrt(9.8f / radius); // 角频率（ω = √(gk)）
            float k = waveNumber; // 波数
            float phase = k * position.x - omega * time; // 相位（位置与时间的关系）
            //height = height * Mathf.Sin(phase); // 高度随时间变化
        }

        // 更新波粒子的位置
        public void UpdatePosition(float deltaTime)
        {
            position += deltaTime * speed * direction; // 更新位置
        }

        // 需要转换为 Vector4 才能传入 GPU
        public Vector4 ToVector4()
        {
            return new Vector4(position.x, position.y, height, 0);
        }
    }
}
