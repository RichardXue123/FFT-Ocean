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
        public float k;      // 波数k
        public float omega;// 角频率ω=√(gk)

        public int bucketNum; //所分到bucket的num

        public WaveParticle GetNegative(float planSize,  float oceanSize)
        {
            // 归一化方向向量
            Vector2 dirNorm = this.direction.normalized;
            // 负粒子位置：沿 direction 负方向偏移一个 radius
            Vector2 negPos = this.position - dirNorm * this.radius * planSize / oceanSize;

            WaveParticle ret = new WaveParticle
            {
                position = negPos,
                direction = this.direction,
                // 高度取反
                height = -this.height,
                // 保留相同的振幅、速度、波数、半径、相位和角频率
                speed = this.speed,
                radius = this.radius,
                k = this.k,
                omega = this.omega,
                bucketNum = this.bucketNum
            };
            return ret;
        }

        // 更新波粒子
        public void Update(float deltaTime, float planeSize, float oceanSize)
        {
            position += deltaTime * speed * direction * planeSize / oceanSize; // 更新位置
        }

        // 转换为 Vector4 传入 GPU
        public Vector4 ToVector4()
        {
            return new Vector4(position.x, position.y, height, radius);
        }
        public Vector2 ToVector2()
        {
            return new Vector2(direction.x, direction.y);
        }
        public Vector2 GetVelocity()
        {
            return direction * speed;
        }
    }
}
