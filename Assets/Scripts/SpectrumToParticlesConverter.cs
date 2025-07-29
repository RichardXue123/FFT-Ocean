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
                float S = JONSWAPSpectrum(omega,ws.spectrums[0].peakOmega,dir,ws.local.windSpeed,ws.g,ws.depth,ws.local.fetch);
                if (float.IsNaN(S) || float.IsInfinity(S))
                {
                    Debug.LogWarning("S: NaN");
                    continue;
                }
                if (S <= 0) { 
                    //Debug.Log("S:"+S);
                    continue;
                }
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
                //Debug.Log("generate a wp:" + particle.position.x);
                particles.Add(particle);

                // 也加入反向相位粒子
               particles.Add(particle.GetNegative(regionSize.x,regionSize.x));
            }

            return particles;
        }

        public List<WaveParticle> GenerateParticlesFromSpectrum(
            WavesSettings ws,
            Vector2 regionCenter,
            Vector2 regionSize,
            int N_omega = 16, // 频率采样数
            int N_theta = 8)  // 方向采样数
        {
            List<WaveParticle> particles = new List<WaveParticle>();

            // 频率采样区间，主峰附近
            float omega_p = ws.spectrums[0].peakOmega;
            float omega_min = omega_p * 0.5f;
            float omega_max = omega_p * 2.5f;
            float delta_omega = (omega_max - omega_min) / N_omega;
            float delta_theta = 2 * Mathf.PI / N_theta;

            for (int iw = 0; iw < N_omega; iw++)
            {
                float omega = omega_min + delta_omega * (iw + 0.5f);
                float k = omega * omega / ws.g;
                float radius = Mathf.PI / k;
                float phaseSpeed = Mathf.Sqrt(ws.g / k);

                for (int itheta = 0; itheta < N_theta; itheta++)
                {
                    float theta = delta_theta * (itheta);
                    Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                    // 谱采样，带宽加权（能量归一化）
                    float S = JONSWAPSpectrum(omega, omega_p, dir, ws.local.windSpeed, ws.g, ws.depth, ws.local.fetch);
                    if (float.IsNaN(S) || float.IsInfinity(S) || S <= 0) continue;

                    float amplitude = Mathf.Sqrt(2f * S * delta_omega * delta_theta); // 归一化的振幅

                    // 边缘采样一个位置（方向可选对应主传播边，也可随机）
                    Vector2 pos = SamplePositionOnRegionEdge(regionCenter, regionSize);

                    var particle = new WaveParticle
                    {
                        position = pos,
                        direction = dir.normalized,
                        height = amplitude,
                        baseHeight = amplitude,
                        phase = 0f, // 可加随机相位扰动
                        angularFrequency = omega,
                        waveNumber = k,
                        radius = radius,
                        speed = phaseSpeed
                    };
                    particles.Add(particle);

                    // 可选：也加入反向相位粒子
                    //particles.Add(particle.GetNegative(regionSize.x, regionSize.x));
                }
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

            // 计算谱无方向部分 Sjw(ω)
            float α = 0.076f * Mathf.Pow((U * U) / (g * fetch), 0.22f);
            float γ = 3.3f;
            float σ = (ω <= ωp) ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow((ω - ωp), 2f) / (2f * σ * σ * ωp * ωp));
            float S0 = (α * g * g) / Mathf.Pow(ω, 5f)
                     * Mathf.Exp(-1.25f * Mathf.Pow(ωp / ω, 4f))
                     * Mathf.Pow(γ, r);
            if (float.IsNaN(S0) || float.IsInfinity(S0))
            {
                Debug.Log("S0: NaN");
            }

            // 有限深度 TMA 修正
            float ωh = ω * Mathf.Sqrt(depth / g);
            float TMA = ωh <= 1f
                ? 0.5f * ωh * ωh
                : (ωh < 2f
                   ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f)
                   : 1f);

            float S_deep = S0 * TMA;

            // 方向性修正 D(θ)
            //    θ = 波向 与 风向 夹角
            float θ = Vector2.SignedAngle(windVec.normalized, dir) * Mathf.Deg2Rad;
            //    一般用 cos^n 展开，指数 n 随 ω/ωp 而变化
            float μ = (ω <= ωp) ? 5f : -2.5f;
            float n = 16f * Mathf.Pow(ω / ωp, μ);
            float cosHalfTheta = Mathf.Cos(θ / 2f);
            cosHalfTheta = Mathf.Clamp01(cosHalfTheta); // 0~1之间，防止负数
            float D;
            if (cosHalfTheta == 0f && n != 0f)
                D = 0f;
            else
                D = (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(cosHalfTheta, n);

            if (float.IsNaN(D) || float.IsInfinity(D))
            {
                D = 0f; // 强制安全
            }
            // 转换到 S(k) = S(ω) · (dω/dk) = S_deep · (1/2) sqrt(g/k)
            float domega_dk = 0.5f * Mathf.Sqrt(g / k);
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
