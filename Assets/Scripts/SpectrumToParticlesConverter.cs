using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Assets.Scripts
{
    /// <summary>
    /// 将光谱参数（如 JONSWAP）转换为波粒子的工具类。
    /// 可用于边界重建，实现与 FFT 模拟的耦合。
    /// </summary>
    public class SpectrumToParticlesConverter
    {
        public Vector2 windSpeed = new Vector2(5, 0);
        /// <summary>
        /// 根据光谱参数，在指定正方形区域内生成波粒子。
        /// 主要用于重建区域边缘的粒子状态。
        /// </summary>
        /// <param name="spectrum">光谱参数（通常来源于 FFT 的设置）</param>
        /// <param name="gravity">重力加速度</param>
        /// <param name="waterDepth">水深</param>
        /// <param name="regionCenter">目标区域中心，x 和 y 表示世界坐标的 x 和 z 轴</param>
        /// <param name="regionSize">正方形区域的边长</param>
        /// <param name="sampleCount">在区域内采样的波数数量</param>
        /// <returns>生成的波粒子列表</returns>
        public List<WaveParticle> GenerateParticlesFromSpectrum(
            WavesSettings ws,
            Vector2 regionCenter,
            Vector2 regionSize,
            int sampleCount = 10)
        {
            List<WaveParticle> particles = new List<WaveParticle>();

            // 根据谱参数生成波矢 k 和方向 theta
            for (int i = 0; i < sampleCount; i++)
            {
                // 1. 随机采样角度
                float theta = Random.Range(0f, 2f * Mathf.PI);
                Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                // 2. 采样波长 / 波数
                float omega = Random.Range(ws.spectrums[0].peakOmega * 0.5f, ws.spectrums[0].peakOmega * 2.5f);
                float k = omega * omega / ws.g;
                float radius = Mathf.PI / k;
                float phaseSpeed = Mathf.Sqrt(ws.g / k);

                // 3. 计算振幅：A = sqrt(2 * S(k))
                float S = JONSWAPSpectrum(omega,ws.spectrums[0].peakOmega,dir,windSpeed,ws.g,ws.depth,ws.local.fetch);
                float A = Mathf.Sqrt(2f * S);

                // 4. 在边缘区域分布（仅边框带内）
                Vector2 pos = SamplePositionOnRegionEdge(regionCenter, regionSize);

                var particle = new WaveParticle
                {
                    position = pos,
                    direction = dir.normalized,
                    height = A,
                    baseHeight = A,
                    phase = 0f,
                    angularFrequency = omega,
                    waveNumber = k,
                    radius = radius,
                    speed = phaseSpeed
                };
                Debug.Log("generate a wp:" + particle.position.x);
                particles.Add(particle);

                // 也加入反向相位粒子
               particles.Add(particle.GetNegative(100,100));
            }

            return particles;
        }

        public List<WaveParticle> GenerateParticlesFromSpectrumCDF(
            SpectrumSettings spectrum,
            float gravity,
            float waterDepth,
            Vector2 regionCenter,
            Vector2 regionSize,
            int sampleCount = 100)
        {
            List<WaveParticle> particles = new List<WaveParticle>();

            // 1. 离散omega区间，计算S(ω)
            int N = 512;
            float omegaMin = spectrum.peakOmega * 0.5f;
            float omegaMax = spectrum.peakOmega * 2.5f;
            float[] omegas = new float[N];
            float[] S = new float[N];
            for (int i = 0; i < N; i++)
            {
                omegas[i] = Mathf.Lerp(omegaMin, omegaMax, i / (float)(N - 1));
                // 这里用JONSWAPSpectrum计算谱值
                S[i] = JONSWAPSpectrum(
                    omegas[i],
                    spectrum.peakOmega,
                    Vector2.right,    // 这里只需要S(ω)幅值，不用管方向
                    windSpeed,
                    gravity,
                    waterDepth,
                    10000f            // fetch，随便填个大数
                );
            }
            // 2. 累加出CDF
            float S_sum = S.Sum();
            float[] CDF = new float[N];
            CDF[0] = S[0] / S_sum;
            for (int i = 1; i < N; i++) CDF[i] = CDF[i - 1] + S[i] / S_sum;

            // 3. 采样sampleCount次
            for (int j = 0; j < sampleCount; j++)
            {
                // 随机方向
                float theta = Random.Range(0f, 2f * Mathf.PI);
                Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
                // CDF采样omega
                float r = Random.value;
                int idx = Array.FindIndex(CDF, cdfVal => cdfVal > r);
                if (idx < 0) idx = N - 1;
                float omega = omegas[idx];
                float k = omega * omega / gravity;
                float radius = Mathf.PI / k;
                float phaseSpeed = Mathf.Sqrt(gravity / k);

                // 用谱值决定振幅（按你原有公式）
                float S_val = S[idx];
                float A = Mathf.Sqrt(2f * S_val);

                // 区域边缘采样位置
                Vector2 pos = SamplePositionOnRegionEdge(regionCenter, regionSize);

                var particle = new WaveParticle
                {
                    position = pos,
                    direction = dir.normalized,
                    height = A,
                    baseHeight = A,
                    phase = 0f,
                    angularFrequency = omega,
                    waveNumber = k,
                    radius = radius,
                    speed = phaseSpeed
                };
                particles.Add(particle);

                // 可选：加入反相位粒子
                particles.Add(particle.GetNegative(100, 100));
            }
            return particles;
        }


        /// <summary>
        /// 计算带方向和深度修正的 JONSWAP 频谱密度 S(k,dir) 风浪Wind Wave用
        /// </summary>
        /// <param name="k">波数大小</param>
        /// <param name="dir">波数方向（单位向量）</param>
        /// <param name="windVec">风速矢量（含大小和方向）</param>
        /// <param name="g">重力加速度（9.81）</param>
        /// <param name="depth">水深</param>
        /// <param name="fetch">有效风区长度（可用海面长度代替）</param>
        public float JONSWAPSpectrum(
            float ω,
            float ωp,
            Vector2 dir,
            Vector2 windVec,
            float g,
            float depth,  
            float fetch
        )
        {
            // 转成角频率 ω = sqrt(g k)
            //float ω = Mathf.Sqrt(g * k);
            float k = ω * ω / g;

            float U = windVec.magnitude;
            //Debug.Log($"U: {U}");

            // 计算谱无方向部分 Sjw(ω)
            float α = 0.076f * Mathf.Pow((U * U) / (g * fetch), 0.22f);
            //float ωp = 0.84f * g / Mathf.Max(U, 0.1f);
            //float ωp = 22f * Mathf.Pow((g * g) / (U * fetch), 0.333333f);
            //Debug.Log($"fetch: { fetch},α: { α}, ω: { ω}, ωp: {ωp}");
            float γ = 3.3f;
            float σ = (ω <= ωp) ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow((ω - ωp), 2f) / (2f * σ * σ * ωp * ωp));
            float S0 = (α * g * g) / Mathf.Pow(ω, 5f)
                     * Mathf.Exp(-1.25f * Mathf.Pow(ωp / ω, 4f))
                     * Mathf.Pow(γ, r);
            //Debug.Log("S0: "+ S0);
            //return S0;

            // 有限深度 TMA 修正
            float ωh = ω * Mathf.Sqrt(depth / g);
            float TMA = ωh <= 1f
                ? 0.5f * ωh * ωh
                : (ωh < 2f
                   ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f)
                   : 1f);

            float S_deep = S0 * TMA;
            //Debug.Log($"S_deep: {S_deep}");
            //return S_deep;

            // 方向性修正 D(θ)
            //    θ = 波向 与 风向 夹角
            float θ = Vector2.SignedAngle(windVec.normalized, dir) * Mathf.Deg2Rad;
            //    一般用 cos^n 展开，指数 n 随 ω/ωp 而变化
            float μ = (ω <= ωp) ? 5f : -2.5f;
            float n = 16f * Mathf.Pow(ω / ωp, μ);
            float D = (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(Mathf.Cos(θ / 2f), n);
            //Debug.Log($"D: {D}");
            // 6. 转换到 S(k) = S(ω) · (dω/dk) = S_deep · (1/2) sqrt(g/k)
            float domega_dk = 0.5f * Mathf.Sqrt(g / k);
            //Debug.Log($"domega_dk: {domega_dk}");
            return S_deep * D * domega_dk;
        }

        /// <summary>
        /// 从长方形区域边缘采样一个粒子位置，用于实现边界粒子生成。
        /// </summary>
        Vector2 SamplePositionOnRegionEdge(Vector2 center, Vector2 size)
        {
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;
            // u分别控制在长宽范围
            float uX = Random.Range(-halfX, halfX);
            float uY = Random.Range(-halfY, halfY);

            switch (Random.Range(0, 4))
            {
                case 0: return center + new Vector2(-halfX, uY);  // Left
                case 1: return center + new Vector2(halfX, uY);   // Right
                case 2: return center + new Vector2(uX, -halfY);  // Bottom
                case 3: return center + new Vector2(uX, halfY);   // Top
            }
            return center;
        }

    }
}
