using System.Collections.Generic;
using Unity.Collections;
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
        // ====== 预计算/缓存 ======
        private bool _initialized;

        // 配置与缓存的物理量
        private WavesSettings _ws;
        private int _Nomega;
        private int _Ntheta;

        private float _g;            // 重力加速度
        private float _depth;        // 水深
        private float _fetch;        // 有效风区长度
        private Vector2 _windVec;    // 风矢量
        private float _omegaP;       // 峰值角频率

        private float _omegaMin;
        private float _omegaMax;
        private float _deltaOmega;
        private float _deltaTheta;

        // 分桶频率与由其派生的量
        private float[] _omegas;       // ω[i]
        private float[] _ks;           // k[i]
        private float[] _radii;        // radius[i] = π/k
        private float[] _phaseSpeeds;  // c[i] = sqrt(g/k)
        private float[] _omegaPow3;    // ω^3（用于 per-frame 公式）
        private float[] _omegaPow4;    // ω^4（用于全区域公式）

        // 小数累计
        public List<float> batchAccumulate = new List<float>();

        /// <summary>
        /// 预计算所有与频率/方向相关、可跨帧复用的量。
        /// </summary>
        public void Initialize(WavesSettings ws, int N_omega = 8, int N_theta = 8)
        {
            _ws = ws;
            _Nomega = Mathf.Max(1, N_omega);
            _Ntheta = Mathf.Max(1, N_theta);

            // 缓存常用参数
            _g = ws.g;
            _depth = ws.depth;
            _fetch = ws.local.fetch;
            _windVec = ws.local.windSpeed;
            _omegaP = ws.spectrums[0].peakOmega;

            // 频率采样区间 & 步长
            _omegaMin = _omegaP * 0.5f;
            _omegaMax = _omegaP * 2.5f;
            _deltaOmega = (_omegaMax - _omegaMin) / _Nomega;
            _deltaTheta = 2f * Mathf.PI / _Ntheta;

            // 分配数组
            _omegas = new float[_Nomega];
            _ks = new float[_Nomega];
            _radii = new float[_Nomega];
            _phaseSpeeds = new float[_Nomega];
            _omegaPow3 = new float[_Nomega];
            _omegaPow4 = new float[_Nomega];

            // 逐桶预计算
            for (int iw = 0; iw < _Nomega; iw++)
            {
                float omega = _omegaMin + _deltaOmega * (iw + 0.5f);
                float k = omega * omega / _g;

                _omegas[iw] = omega;
                _ks[iw] = k;
                _radii[iw] = Mathf.PI / k;
                _phaseSpeeds[iw] = Mathf.Sqrt(_g / k);
                _omegaPow3[iw] = omega * omega * omega;
                _omegaPow4[iw] = _omegaPow3[iw] * omega;
            }

            // 重置小数累加器
            batchAccumulate = new List<float>(new float[_Nomega]);

            _initialized = true;
        }

        // ====== 生成函数 ======

        public NativeList<WaveParticle> GenerateParticlesFromSpectrum(
            WavesSettings ws,
            Vector2 regionCenter,
            Vector2 regionSize,
            int N_omega = 8,
            int N_theta = 8,
            Allocator allocator = Allocator.Persistent)
        {
            // 若参数变化（例如不同分辨率），允许动态再初始化
            if (!_initialized || N_omega != _Nomega || N_theta != _Ntheta || ws != _ws)
            {
                Initialize(ws, N_omega, N_theta);
            }

            var particles = new NativeList<WaveParticle>(allocator);

            // 注意：本函数的 batchSize ~ ω^4 * regionSize.x^2 （你原式）
            // 仅剩与 regionSize 相关的因子在此处计算即可
            float regionFactor = regionSize.x * regionSize.x * 8f / (Mathf.PI * Mathf.PI * Mathf.PI * _g * _g);

            for (int iw = 0; iw < _Nomega; iw++)
            {
                float omega = _omegas[iw];
                float k = _ks[iw];
                float radius = _radii[iw];
                float phaseSpeed = _phaseSpeeds[iw];

                float batchSize = _omegaPow4[iw] * regionFactor;

                // 小数累加→取整
                batchAccumulate[iw] += batchSize;
                int nBatch = Mathf.FloorToInt(batchAccumulate[iw]);
                batchAccumulate[iw] -= nBatch;

                for (int batch = 0; batch < nBatch; batch++)
                {
                    for (int itheta = 0; itheta < _Ntheta; itheta++)
                    {
                        float theta = _deltaTheta * itheta;
                        Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                        float S = JONSWAPSpectrumCached(omega, dir);
                        if (float.IsNaN(S) || float.IsInfinity(S) || S <= 0) continue;

                        float amplitude = Mathf.Sqrt(2f * S * _deltaOmega * _deltaTheta);
                        Vector2 pos = SamplePositionInRegion(regionCenter, regionSize);

                        var particle = new WaveParticle
                        {
                            position = pos,
                            direction = dir.normalized,
                            height = amplitude,
                            omega = omega,
                            k = k,
                            radius = radius,
                            speed = phaseSpeed,
                            bucketNum = iw
                        };
                        particles.Add(particle);

                        // 反向粒子（可选）
                        particles.Add(particle.GetNegative(regionSize.x, regionSize.x));
                    }
                }
            }
            return particles;
        }

        /// <summary>
        /// 主函数：采样并按radius/omega分桶（逐帧边界生成）
        /// </summary>
        public NativeList<WaveParticle> GenerateParticlesFromSpectrumPerFrame(
            WavesSettings ws,
            Vector2 regionCenter,
            Vector2 regionSize,
            int N_omega = 8,
            int N_theta = 8,
            float deltaTime = 0.02f,
            Allocator allocator = Allocator.Persistent)
        {
            // 若参数变化（例如不同分辨率），允许动态再初始化
            if (!_initialized || N_omega != _Nomega || N_theta != _Ntheta || ws != _ws)
            {
                Initialize(ws, N_omega, N_theta);
            }

            var particles = new NativeList<WaveParticle>(allocator);

            // 本函数的 batchSize ~ ω^3 * deltaTime * regionSize.x
            float regionFrameFactor = deltaTime * regionSize.x * 8f / (Mathf.PI * Mathf.PI * Mathf.PI * _g);

            for (int iw = 0; iw < _Nomega; iw++)
            {
                float omega = _omegas[iw];
                float k = _ks[iw];
                float radius = _radii[iw];
                float phaseSpeed = _phaseSpeeds[iw];

                float batchSize = _omegaPow3[iw] * regionFrameFactor;

                // 小数累加→取整
                batchAccumulate[iw] += batchSize;
                int nBatch = Mathf.FloorToInt(batchAccumulate[iw]);
                batchAccumulate[iw] -= nBatch;

                for (int batch = 0; batch < nBatch; batch++)
                {
                    for (int itheta = 0; itheta < _Ntheta; itheta++)
                    {
                        float theta = _deltaTheta * itheta;
                        Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                        float S = JONSWAPSpectrumCached(omega, dir);
                        if (float.IsNaN(S) || float.IsInfinity(S) || S <= 0) continue;

                        float amplitude = Mathf.Sqrt(2f * S * _deltaOmega * _deltaTheta);
                        Vector2 pos = SamplePositionOnRegionEdge(regionCenter, regionSize);

                        var particle = new WaveParticle
                        {
                            position = pos,
                            direction = dir.normalized,
                            height = amplitude,
                            omega = omega,
                            k = k,
                            radius = radius,
                            speed = phaseSpeed,
                            bucketNum = iw
                        };
                        particles.Add(particle);

                        // 反向粒子（可选）
                        particles.Add(particle.GetNegative(regionSize.x, regionSize.x));
                    }
                }
            }
            return particles;
        }

        // ====== 频谱：保留原公共接口 + 新的缓存版 ======

        /// <summary>
        /// 原版（保持兼容）：带方向和深度修正的 JONSWAP 频谱密度
        /// </summary>
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
            float k = ω * ω / g;
            float U = windVec.magnitude;

            float α = 0.076f * Mathf.Pow((U * U) / (g * fetch), 0.22f);
            float γ = 3.3f;
            float σ = (ω <= ωp) ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow((ω - ωp), 2f) / (2f * σ * σ * ωp * ωp));
            float S0 = (α * g * g) / Mathf.Pow(ω, 5f)
                       * Mathf.Exp(-1.25f * Mathf.Pow(ωp / ω, 4f))
                       * Mathf.Pow(γ, r);

            float ωh = ω * Mathf.Sqrt(depth / g);
            float TMA = ωh <= 1f
                ? 0.5f * ωh * ωh
                : (ωh < 2f ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f) : 1f);

            float S_deep = S0 * TMA;

            float θ = Vector2.SignedAngle(windVec.normalized, dir) * Mathf.Deg2Rad;
            float μ = (ω <= ωp) ? 5f : -2.5f;
            float n = 16f * Mathf.Pow(ω / ωp, μ);
            float cosHalfTheta = Mathf.Clamp01(Mathf.Cos(θ / 2f));
            float D = (cosHalfTheta == 0f && n != 0f)
                ? 0f
                : (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(cosHalfTheta, n);

            float domega_dk = 0.5f * Mathf.Sqrt(g / k);
            return S_deep * D * domega_dk;
        }

        /// <summary>
        /// 缓存版：内部直接用缓存的 g/depth/fetch/wind/omega_p，加速调用。
        /// </summary>
        private float JONSWAPSpectrumCached(float ω, Vector2 dir)
        {
            // 这里直接复用上面的公式，但把参数替换为缓存
            float k = ω * ω / _g;
            float U = _windVec.magnitude;

            float α = 0.076f * Mathf.Pow((U * U) / (_g * _fetch), 0.22f);
            float γ = 3.3f;
            float σ = (ω <= _omegaP) ? 0.07f : 0.09f;
            float r = Mathf.Exp(-Mathf.Pow((ω - _omegaP), 2f) / (2f * σ * σ * _omegaP * _omegaP));
            float S0 = (α * _g * _g) / Mathf.Pow(ω, 5f)
                       * Mathf.Exp(-1.25f * Mathf.Pow(_omegaP / ω, 4f))
                       * Mathf.Pow(γ, r);

            float ωh = ω * Mathf.Sqrt(_depth / _g);
            float TMA = ωh <= 1f
                ? 0.5f * ωh * ωh
                : (ωh < 2f ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f) : 1f);

            float S_deep = S0 * TMA;

            float θ = Vector2.SignedAngle(_windVec.normalized, dir) * Mathf.Deg2Rad;
            float μ = (ω <= _omegaP) ? 5f : -2.5f;
            float n = 16f * Mathf.Pow(ω / _omegaP, μ);
            float cosHalfTheta = Mathf.Clamp01(Mathf.Cos(θ / 2f));
            float D = (cosHalfTheta == 0f && n != 0f)
                ? 0f
                : (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(cosHalfTheta, n);

            float domega_dk = 0.5f * Mathf.Sqrt(_g / k);
            return S_deep * D * domega_dk;
        }

        // ====== 采样 ======

        /// <summary>
        /// 从长方形区域边缘采样一个粒子位置，用于实现边界粒子生成。
        /// </summary>
        Vector2 SamplePositionOnRegionEdge(Vector2 center, Vector2 size)
        {
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;
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

        /// <summary>
        /// 从长方形区域内部采样一个粒子位置，用于实现边界粒子生成。
        /// </summary>
        Vector2 SamplePositionInRegion(Vector2 center, Vector2 size)
        {
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;
            float uX = Random.Range(-halfX, halfX);
            float uY = Random.Range(-halfY, halfY);
            return center + new Vector2(uX, uY);
        }
    }
}
