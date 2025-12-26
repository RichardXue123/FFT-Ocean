using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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

        // 振幅指数衰减半衰期（秒）。<=0 时不做指数淡出。
        public float amplitudeHalfLife;

        public float2 regionCenter;  // 用于Contains判断
        public float2 regionHalf;    // = region.size * 0.5f

        public void Execute(int index)
        {
            var p = particles[index];

            // 寿命剔除：remainingLife<=0 代表无限寿命
            if (p.remainingLife > 0f)
            {
                p.remainingLife -= deltaTime;
                if (p.remainingLife <= 0f)
                    return;

                // 指数淡出（按半衰期）
                if (p.initialLife > 1e-6f && amplitudeHalfLife > 1e-6f)
                {
                    float age = p.initialLife - p.remainingLife;
                    float factor = math.exp2(-age / amplitudeHalfLife);
                    p.height = p.initialHeight * factor;
                }
            }

            float maxMove = p.speed * deltaTime + p.radius;  // 没有 speed 就用一个保守上界
            float2 d0 = math.abs(p.position - regionCenter) - regionHalf;
            d0 = math.max(d0, 0);              // 只要超出的一半
            if (math.any(d0 > maxMove))
                return; // 直接丢掉，无需 Update
            // 位置更新
            p.Update(deltaTime, planeSize, oceanSize);

            // 越界剔除（含半径扩展）
            float2 d = math.abs(p.position - regionCenter);
            float2 ext = regionHalf + new float2(p.radius, p.radius);
            if (math.all(d <= ext))
                survivors.AddNoResize(p); // 预先保证容量
        }
    }
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
    public struct UpdateMarkAliveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<WaveParticle> src;  // 读旧
        public NativeArray<WaveParticle> dst;              // 写新（就地或到 scratch 缓冲）
        public NativeArray<byte> alive;                    // 0/1 生存标记（避免并发写 NativeList）

        public float deltaTime;
        public float planeScale;       // = planeSize / oceanSize（预先计算，减少每粒子除法）
        public float2 regionCenter;
        public float2 regionHalf;

        // 振幅指数衰减半衰期（秒）。<=0 时不做指数淡出。
        public float amplitudeHalfLife;

        public void Execute(int i)
        {
            var p = src[i];

            // 寿命剔除：remainingLife<=0 代表无限寿命
            if (p.remainingLife > 0f)
            {
                p.remainingLife -= deltaTime;
                if (p.remainingLife <= 0f)
                {
                    alive[i] = 0;
                    return;
                }

                // 指数淡出（按半衰期）
                if (p.initialLife > 1e-6f && amplitudeHalfLife > 1e-6f)
                {
                    float age = p.initialLife - p.remainingLife;
                    float factor = math.exp2(-age / amplitudeHalfLife);
                    p.height = p.initialHeight * factor;
                }
            }

            // 早期粗剔除：如果离区域包围盒很远，直接淘汰
            float maxMove = p.speed * deltaTime + p.radius;
            float2 d0 = math.abs(p.position - regionCenter) - regionHalf;
            d0 = math.max(d0, 0);
            if (math.any(d0 > maxMove))
            {
                alive[i] = 0;
                return;
            }

            // 位置更新（合并比例，减少指令/除法）
            p.position += p.direction * (p.speed * deltaTime * planeScale);

            // 精确包含测试（带半径）
            float2 d = math.abs(p.position - regionCenter);
            float2 ext = regionHalf + new float2(p.radius, p.radius);
            bool inside = math.all(d <= ext);

            alive[i] = (byte)(inside ? 1 : 0);
            dst[i] = p;     // 把更新后的粒子写回（下一步再线性压缩）
        }
    }
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
    public struct CompactAliveJob : IJob
    {
        [ReadOnly] public NativeArray<WaveParticle> updated;  // 上一 Job 写回的数组
        [ReadOnly] public NativeArray<byte> alive;            // 0/1 标记
        public NativeList<WaveParticle> outList;              // 已提前 Ensure 容量

        public void Execute()
        {
            outList.Clear(); // 保证长度为0，但不改变容量
                             // 线性压缩，完全顺序写，极省时
            for (int i = 0; i < updated.Length; i++)
                if (alive[i] != 0)
                    outList.AddNoResize(updated[i]);
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

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
    public struct PackParticlesToV4Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Assets.Scripts.WaveParticle> src; // bucket 内的粒子
        [WriteOnly] public NativeArray<Vector4> dst;                     // 指向 GPU Buffer 的 BeginWrite 区域

        public void Execute(int i)
        {
            var p = src[i];
            // 直接展开，避免 ToVector4() 的函数调用开销
            dst[i] = new Vector4(p.position.x, p.position.y, p.height, p.radius);
        }
    }

    //（可选）同时打包速度到另一个 GPU Buffer
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
    public struct PackVelocitiesToV2Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Assets.Scripts.WaveParticle> src;
        [WriteOnly] public NativeArray<Vector2> dst;

        public void Execute(int i)
        {
            var p = src[i];
            dst[i] = new Vector2(p.direction.x * p.speed, p.direction.y * p.speed);
        }
    }


}
