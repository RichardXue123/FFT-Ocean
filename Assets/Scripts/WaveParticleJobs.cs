using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Assets.Scripts
{
    [BurstCompile]
    public struct WaveParticleUpdateCullJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<WaveParticle> particles;          // 只读旧粒子
        public NativeList<WaveParticle>.ParallelWriter survivors;       // 并行写回保留粒子

        public float deltaTime;
        public float planeSize;
        public float oceanSize;

        public Vector2 regionCenter;  // 用于Contains判断
        public Vector2 regionHalf;    // = region.size * 0.5f

        public void Execute(int index)
        {
            var p = particles[index];
            // 位置更新
            p.Update(deltaTime, planeSize, oceanSize);

            // 越界剔除（含半径扩展）
            float dx = Mathf.Abs(p.position.x - regionCenter.x);
            float dy = Mathf.Abs(p.position.y - regionCenter.y);
            bool inside = (dx <= regionHalf.x + p.radius) && (dy <= regionHalf.y + p.radius);

            if (inside)
            {
                // 预先保证了容量，因此这里用 AddNoResize（线程安全且无锁扩容）
                survivors.AddNoResize(p);
            }
        }
    }
    [BurstCompile]
    public struct WaveParticleUpdateJob : IJobParallelFor
    {
        public NativeArray<WaveParticle> particles;
        public float deltaTime;
        public float planeSize;
        public float oceanSize;

        public void Execute(int index)
        {
            //为什么需要取出再拷回？？
            var p = particles[index];
            p.Update(deltaTime, planeSize, oceanSize);
            particles[index] = p;
        }
    }

    [BurstCompile]
    public struct WaveParticleBilinearSplatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<WaveParticle> particles;
        [NativeDisableParallelForRestriction]
        public NativeArray<float> heightMap; // 当前 bucket 的高度图
        public Vector2 regionCenter;
        public Vector2 regionSize;
        public int resolution;
        public int N_omega; // bucket 总数
        public int this_bucket; //当前bucket

        public void Execute(int i)
        {
            var p = particles[i];
            if (p.bucketNum != this_bucket)
            {
                return;
            }
            Vector2 wPos = p.position;
            Vector2 regionMin = regionCenter - 0.5f * regionSize;
            Vector2 regionMax = regionCenter + 0.5f * regionSize;
            Vector2 worldToUV = (wPos - regionMin) / (regionMax - regionMin);

            // 亚像素坐标（浮点数）
            // Clamp，防止1.0时越界
            float xF = Mathf.Clamp01(worldToUV.x) * (resolution - 1);
            float yF = Mathf.Clamp01(worldToUV.y) * (resolution - 1);
            int x = (int)xF;
            int y = (int)yF;
            float dX = xF - x;
            float dY = yF - y;

            // 4个像素索引
            int x0y0 = x + y * resolution;
            int x1y0 = (x + 1) + y * resolution;
            int x0y1 = x + (y + 1) * resolution;
            int x1y1 = (x + 1) + (y + 1) * resolution;

            // Splat到4个点（双线性权重）
            // 无需原子加，因为每个 bucket/job 高度图是独立的
            if (x0y0 >= 0 && x1y0 >= 0 && x0y1 >= 0 && x1y1 >= 0 &&
                x0y0 < heightMap.Length && x1y0 < heightMap.Length &&
                x0y1 < heightMap.Length && x1y1 < heightMap.Length)
            {
                heightMap[x0y0] += p.height * (1.0f - dX) * (1.0f - dY);
                heightMap[x1y0] += p.height * dX * (1.0f - dY);
                heightMap[x0y1] += p.height * (1.0f - dX) * dY;
                heightMap[x1y1] += p.height * dX * dY;
            }
        }
    }

}
