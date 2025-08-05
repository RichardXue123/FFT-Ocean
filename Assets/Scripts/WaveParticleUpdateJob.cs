using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

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
}
