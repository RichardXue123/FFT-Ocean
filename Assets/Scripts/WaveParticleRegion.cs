using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// 表示一个波粒子作用的区域（目前只支持长方形，后续可扩展）。
    /// </summary>
    [Serializable]
    public class WaveParticleRegion
    {
        /// <summary>
        /// 区域中心，世界坐标（X, Y）。
        /// </summary>
        public Vector2 center;

        /// <summary>
        /// 长方形的长宽。
        /// </summary>
        public Vector2 size;

        /// <summary>
        /// 本区域的粒子列表（可选）。
        /// </summary>
        //public NativeList<WaveParticle> particles = new NativeList<WaveParticle>(Allocator.Persistent);

        public NativeList<WaveParticle>[] buckets;

        public void InitBuckets(int nOmega)
        {
            buckets = new NativeList<WaveParticle>[nOmega];
            for (int i = 0; i < nOmega; i++)
                buckets[i] = new NativeList<WaveParticle>(Allocator.Persistent);
        }

        public void ClearBuckets()
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
        }

        /// <summary>
        /// 构造函数。
        /// </summary>
        public WaveParticleRegion(Vector2 center, Vector2 size)
        {
            this.center = center;
            this.size = size;
            //this.particles = new NativeList<WaveParticle>(Allocator.Persistent);
        }

        /// <summary>
        /// 判断某个点的影响范围是否在本区域内。
        /// </summary>
        // 区域判定方法（带半径/包围盒可选）
        public bool Contains(Vector2 pos, float radius)
        {
            Vector2 rel = pos - center;
            Vector2 half = size * 0.5f;
            return Mathf.Abs(rel.x) <= half.x + radius && Mathf.Abs(rel.y) <= half.y + radius;
        }
    }
}
