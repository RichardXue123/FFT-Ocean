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

        public NativeList<WaveParticle>[] buckets; // 当前帧可见数据
        public NativeList<WaveParticle>[] scratch; // 并行 Job 的写入目标（下一步会与 buckets 交换）

        public void InitBuckets(int nOmega)
        {
            // 初始化两个持久数组，帧间复用
            buckets = new NativeList<WaveParticle>[nOmega];
            scratch = new NativeList<WaveParticle>[nOmega];
            for (int i = 0; i < nOmega; i++)
            {
                buckets[i] = new NativeList<WaveParticle>(Allocator.Persistent);
                scratch[i] = new NativeList<WaveParticle>(Allocator.Persistent);
            }
        }

        // 供 Step1 之前调用：保证 scratch[b] 有足够容量，并把长度清零
        public void EnsureScratchCapacity(int b, int needed)
        {
            var list = scratch[b];
            if (list.Capacity < needed) list.Capacity = needed; // 只在需要时增长
            list.Clear(); // 长度归零，供并行 AddNoResize 使用
        }

        public void ClearBuckets()
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
            for (int i = 0; i < scratch.Length; i++) scratch[i].Clear();
        }

        public void SwapBuckets()
        {
            for (int i = 0; i < buckets.Length; i++)
            {
                var tmp = buckets[i];
                buckets[i] = scratch[i];
                scratch[i] = tmp;
            }
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

        public void Dispose()
        {
            if (buckets != null)
                for (int i = 0; i < buckets.Length; i++)
                    if (buckets[i].IsCreated) buckets[i].Dispose();

            if (scratch != null)
                for (int i = 0; i < scratch.Length; i++)
                    if (scratch[i].IsCreated) scratch[i].Dispose();
        }
    }
}
