using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Assets.Scripts
{
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

    [BurstCompile]
    struct CullInPlaceJob : IJob
    {
        public NativeArray<WaveParticle> Particles; // buckets[b].AsArray()
        public Vector2 RegionCenter;
        public Vector2 RegionHalf;                  // region.size * 0.5f

        // 写回新长度
        public NativeArray<int> NewLength;          // 长度=1, TempJob

        public void Execute()
        {
            int write = 0;
            // 这里的“包含”等价于你原先的 region.Contains(pos, radius)
            for (int i = 0; i < Particles.Length; i++)
            {
                var p = Particles[i];
                float dx = Mathf.Abs(p.position.x - RegionCenter.x);
                float dy = Mathf.Abs(p.position.y - RegionCenter.y);

                // 给边界留半径的安全带：|x-center| <= half.x - r，y 同理
                if (dx <= (RegionHalf.x - p.radius) && dy <= (RegionHalf.y - p.radius))
                {
                    if (write != i) Particles[write] = p;
                    write++;
                }
            }
            NewLength[0] = write;
        }
    }
}
