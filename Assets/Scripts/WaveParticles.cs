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
    public class WaveParticle
    {
        // 基础物理属性
        public Vector2 position;      // 当前位置（原WavePos）
        public Vector2 direction;     // 传播方向（原WaveDir）
        public float height;          // 波峰高度（原WaveHeight）
        public float baseHeight;      // 振幅，初始高度值
        public float speed;           // 传播速度（原WaveSpeed）
        public float radius;          // 影响半径（原Radius）
        public float waveNumber;      // 波数k（原WaveVector）

        // 相位控制
        public float phase;           // 初始相位
        public float angularFrequency;// 角频率ω=√(gk)
        public WaveParticle()
        {
            // 初始化默认值
            position = Vector2.zero;
            direction = Vector2.up;
            height = 0;
            baseHeight = 0;
            speed = 0;
            radius = 0;
            waveNumber = 0;
            phase = 0;
            angularFrequency = 0;
        }

        public WaveParticle GetNegative()
        {
            WaveParticle ret = new WaveParticle
            {
                //将p的副属性赋值给当前对象
                position = this.position + new Vector2(0, 0.5f), // 偏移位置;
                direction = this.direction,
                height = -this.height,
                baseHeight = this.baseHeight,
                speed = this.speed,
                radius = this.radius,
                waveNumber = this.waveNumber,
                phase = this.phase,
                angularFrequency = this.angularFrequency
            };
            return ret;
        }

        // 随时间更新波粒子的信息
        public void UpdateParticle(float time)
        {
            // 计算波动的高度，假设是简单的正弦波传播
            // 波动传播方程: height = A * sin(k * x - ω * t)
            // A：振幅，k：波数，ω：角频率，t：时间，x：位置

            //float omega = Mathf.Sqrt(9.8f / radius); // 角频率（ω = √(gk)）
            float curPhase = waveNumber * Vector2.Dot(position, direction.normalized) - angularFrequency * time + phase;
            height = baseHeight * Mathf.Sin(curPhase);
        }

        // 更新波粒子的位置
        public void UpdatePosition(float deltaTime, float planeSize, float oceanSize)
        {
            position += deltaTime * speed * direction * planeSize / oceanSize; // 更新位置
            float worldRadius = radius * planeSize / oceanSize;
            if (position.x > planeSize / 2 + worldRadius)
            { 
                position.x -= (planeSize + 2 * worldRadius); 
            }
            else if (position.x < -(planeSize / 2 + worldRadius))
            {
                position.x += (planeSize + 2 * worldRadius);
            }
            if (position.y > planeSize / 2 + worldRadius)
            {
                position.y -= (planeSize + 2 * worldRadius);
            }
            else if (position.y < -(planeSize / 2 + worldRadius))
            {
                position.y += (planeSize + 2 * worldRadius);
            }
        }

        // 转换为 Vector4 传入 GPU
        public Vector4 ToVector4()
        {
            return new Vector4(position.x, position.y, height, radius);
        }
        public Vector4 ToVector4Extra()
        {
            return new Vector4(phase, angularFrequency, 0f, 0f);
        }
    }
}
