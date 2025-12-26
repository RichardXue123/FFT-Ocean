using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using UnityEngine;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;

namespace Assets.Scripts
{
    // 波粒子核心数据结构
    [System.Serializable]
    public struct WaveParticle
    {
        // 基础物理属性
        public float2 position;      // 当前位置（原WavePos）
        public float2 direction;     // 传播方向（原WaveDir）
        public float height;          // 波峰高度（原WaveHeight）
        public float speed;           // 传播速度（原WaveSpeed）
        public float radius;          // 影响半径（原Radius）
        public float k;      // 波数k
        public float omega;// 角频率ω=√(gk)

        // 用于寿命淡出：线性衰减 height = initialHeight * (remainingLife / initialLife)
        // initialLife<=0 表示无限寿命/不做淡出
        public float initialHeight;
        public float initialLife;

        // 剩余寿命（秒）。<=0 表示无限寿命（用于谱初始化粒子）。
        public float remainingLife;

        public int bucketNum; //所分到bucket的num

        public WaveParticle GetNegative(float planSize,  float oceanSize)
        {
            // 归一化方向向量
            float2 dirNorm = math.normalize(this.direction);
            // 负粒子位置：沿 direction 负方向偏移一个 radius
            float2 negPos = this.position - dirNorm * this.radius * planSize / oceanSize;

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
                initialHeight = -this.initialHeight,
                initialLife = this.initialLife,
                remainingLife = this.remainingLife,
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
        public float2 GetVelocity()
        {
            return direction * speed;
        }
    }
}
