using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Assets.Scripts
{
    /// <summary>
    /// 将谱参数（如 JONSWAP）转换为波粒子的工具类。
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


        // 涌浪相关参数
        public float Tp = 3.8f;     // 涌浪 峰值周期 Tp
        public float Hs = 3.0f;     //涌浪 有义波高，默认3m

        private float _omegaMin;
        private float _omegaMax;
        //private float _deltaOmega;
        private float _deltaTheta;

        // 分桶频率与由其派生的量
        public float[] _omegas;       // ω[i]
        public float[] _deltaOmegas;
        // 额外保存桶边界（调试/可视化有用）
        public float[] _omegaEdges;
        private float[] _ks;           // k[i]
        public float[] _radii;        // radius[i] = π/k
        private float[] _phaseSpeeds;  // c[i] = sqrt(g/k)
        private float[] _omegaPow3;    // ω^3（用于 per-frame 公式）
        private float[] _omegaPow4;    // ω^4（用于全区域公式）

        // 方向分桶相关
        private float[] _thetas;       // θ[i] - 各桶的中心方向（相对风向）
        private float[] _deltaThetas;  // Δθ[i] - 各桶的角度宽度
        private float[] _thetaEdges;   // 方向桶边界
        private float _windDirection;  // 风向角度（弧度）

        // 小数累计
        public List<float> batchAccumulate = new List<float>();

        static float[] p = {0.99999999999980993f, 676.5203681218851f, -1259.1392167224028f,
            771.32342877765313f, -176.61502916214059f, 12.507343278686905f,
            -0.13857109526572012f, 9.9843695780195716e-6f, 1.5056327351493116e-7f};

            /// <summary>
            /// 预计算所有与频率/方向相关、可跨帧复用的量。
            /// </summary>
        public void Initialize(WavesSettings ws, int N_omega = 8, int N_theta = 8)
        {
            _ws = ws;
            _Nomega = Mathf.Max(1, N_omega);
            _Ntheta = Mathf.Max(1, N_theta);

            _g = ws.g;
            _depth = ws.depth;
            _fetch = ws.local.fetch;
            _windVec = ws.local.windSpeed;
            _omegaP = ws.spectrums[0].peakOmega;

            _omegaMin = _omegaP * 0.5f;
            _omegaMax = _omegaP * 2.5f;
            
            // 计算风向
            _windDirection = Mathf.Atan2(_windVec.y, _windVec.x);

            _omegas = new float[_Nomega];
            _ks = new float[_Nomega];
            _radii = new float[_Nomega];
            _phaseSpeeds = new float[_Nomega];
            _omegaPow3 = new float[_Nomega];
            _omegaPow4 = new float[_Nomega];
            _deltaOmegas = new float[_Nomega];
            _omegaEdges = new float[_Nomega + 1];

            // 方向分桶数组
            _thetas = new float[_Ntheta];
            _deltaThetas = new float[_Ntheta];
            _thetaEdges = new float[_Ntheta + 1];

            // ---------- 1) 预采样 S_deep(ω) 并做累计能量 ----------
            int M = 1024; // 细网格
            float dω_uniform = (_omegaMax - _omegaMin) / (M - 1);

            // 方向平均的深水权重（去掉方向 D，取 S_deep*dω/dk 这部分）
            float EvalWeight(float ω)
            {
                // 复制自 JONSWAPSpectrumCached，但不乘方向项 D
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
                float TMA = (ωh <= 1f) ? 0.5f * ωh * ωh
                            : (ωh < 2f ? 1f - 0.5f * Mathf.Pow(2f - ωh, 2f) : 1f);

                float S_deep = S0 * TMA;

                // ====== 关键：除以 ω³ 作为权重 ======
                // 因为粒子数 ∝ ω³ × S(ω) × Δω，要让各桶粒子数相近，
                // CDF 按 S(ω)/ω³ 加权，这样高频区域会分配更宽的桶（Δω大），少采样
                float omega3 = ω * ω * ω;
                return Mathf.Max(0f, S_deep / (omega3 + 1e-10f));
            }

            // 细网格上评估
            float[] ωgrid = new float[M];
            float[] wgrid = new float[M];
            for (int j = 0; j < M; j++)
            {
                float ω = _omegaMin + j * dω_uniform;
                ωgrid[j] = ω;
                wgrid[j] = EvalWeight(ω);
            }

            // 梯形积分累计
            float[] cdf = new float[M];
            cdf[0] = 0f;
            for (int j = 1; j < M; j++)
            {
                cdf[j] = cdf[j - 1] + 0.5f * (wgrid[j] + wgrid[j - 1]) * dω_uniform;
            }
            float totalEnergy = cdf[M - 1];
            if (totalEnergy <= 1e-20f) totalEnergy = 1e-20f;

            // ---------- 2) 依据等能量选择桶边界 ----------
            _omegaEdges[0] = _omegaMin;
            _omegaEdges[_Nomega] = _omegaMax;

            // 二分查找 CDF^-1
            float FindOmegaAtCdf(float target)
            {
                int lo = 0, hi = M - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cdf[mid] < target) lo = mid + 1; else hi = mid;
                }
                // 线性内插
                int i1 = Mathf.Clamp(lo - 1, 0, M - 2);
                int i2 = i1 + 1;
                float t1 = cdf[i1], t2 = cdf[i2];
                float w = (Mathf.Abs(t2 - t1) < 1e-12f) ? 0f : (target - t1) / (t2 - t1);
                return Mathf.Lerp(ωgrid[i1], ωgrid[i2], Mathf.Clamp01(w));
            }

            for (int i = 1; i < _Nomega; i++)
            {
                float target = (totalEnergy * i) / _Nomega;
                _omegaEdges[i] = FindOmegaAtCdf(target);
            }

            // ---------- 3) 每个桶中心频率 = 能量加权质心；Δω 为边界差 ----------
            for (int iw = 0; iw < _Nomega; iw++)
            {
                float a = _omegaEdges[iw];
                float b = _omegaEdges[iw + 1];

                // 细网格上做质心近似：∫ω*S dω / ∫S dω
                // 先快速遍历 ωgrid 覆盖 [a,b] 的索引范围
                int j0 = Mathf.Clamp(Mathf.FloorToInt((a - _omegaMin) / dω_uniform), 0, M - 2);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((b - _omegaMin) / dω_uniform), 1, M - 1);

                float num = 0f, den = 0f;
                for (int j = j0 + 1; j <= j1; j++)
                {
                    // 小段 [ω_{j-1}, ω_j]
                    float ωL = ωgrid[j - 1], ωR = ωgrid[j];
                    float sL = wgrid[j - 1], sR = wgrid[j];

                    // 与 [a,b] 截断
                    float L = Mathf.Max(ωL, a);
                    float R = Mathf.Min(ωR, b);
                    if (R <= L) continue;

                    // 线性内插 s(ω) ≈ sL + t (sR - sL)
                    // ∫ s dω ≈ 0.5*(s(L)+s(R))*(R-L)
                    float sL2 = Mathf.Lerp(sL, sR, (L - ωL) / (ωR - ωL));
                    float sR2 = Mathf.Lerp(sL, sR, (R - ωL) / (ωR - ωL));
                    float segDen = 0.5f * (sL2 + sR2) * (R - L);

                    // ∫ ω s(ω) dω 近似：用中点加权（足够好）
                    float mid = 0.5f * (L + R);
                    float sMid = 0.5f * (sL2 + sR2);
                    float segNum = mid * sMid * (R - L);

                    den += segDen;
                    num += segNum;
                }

                float ωc = (den > 1e-20f) ? (num / den) : 0.5f * (a + b);
                _omegas[iw] = ωc;
                _deltaOmegas[iw] = Mathf.Max(1e-6f, b - a); // 防零

                // 后续派生量
                float k = ωc * ωc / _g;
                _ks[iw] = k;
                _radii[iw] = Mathf.PI / k;
                _phaseSpeeds[iw] = Mathf.Sqrt(_g / k);
                _omegaPow3[iw] = ωc * ωc * ωc;
                _omegaPow4[iw] = _omegaPow3[iw] * ωc;
            }

            // ---------- 4) 方向分桶：基于峰值频率的方向分布做等能量采样 ----------
            int M_theta = 360; // 方向细网格（1度精度）
            float dθ_uniform = 2f * Mathf.PI / M_theta;
            
            // 使用峰值频率的方向分布作为参考
            float EvalDirectionWeight(float θ_rel)
            {
                // θ_rel 是相对风向的角度，范围 [-π, π]
                float μ = 5f;  // 使用峰值频率处的 μ（ω = ωp）
                float n = 16f; // ω/ωp = 1 时
                
                float cosHalf = Mathf.Cos(θ_rel / 2f);
                if (cosHalf <= 0f) return 0f;
                
                float D = (n + 1f) / (2f * Mathf.PI) * Mathf.Pow(cosHalf, n);
                return D;
            }
            
            // 细网格上评估方向分布
            float[] θgrid = new float[M_theta];
            float[] dgrid = new float[M_theta];
            for (int j = 0; j < M_theta; j++)
            {
                float θ_rel = -Mathf.PI + j * dθ_uniform;  // [-π, π]
                θgrid[j] = θ_rel;
                dgrid[j] = EvalDirectionWeight(θ_rel);
            }
            
            // 梯形积分计算 CDF
            float[] cdf_theta = new float[M_theta];
            cdf_theta[0] = 0f;
            for (int j = 1; j < M_theta; j++)
            {
                cdf_theta[j] = cdf_theta[j - 1] + 0.5f * (dgrid[j] + dgrid[j - 1]) * dθ_uniform;
            }
            float totalDir = cdf_theta[M_theta - 1];
            if (totalDir <= 1e-20f) totalDir = 1e-20f;
            
            // 二分查找方向 CDF^-1
            float FindThetaAtCdf(float target)
            {
                int lo = 0, hi = M_theta - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cdf_theta[mid] < target) lo = mid + 1; else hi = mid;
                }
                int i1 = Mathf.Clamp(lo - 1, 0, M_theta - 2);
                int i2 = i1 + 1;
                float t1 = cdf_theta[i1], t2 = cdf_theta[i2];
                float w = (Mathf.Abs(t2 - t1) < 1e-12f) ? 0f : (target - t1) / (t2 - t1);
                return Mathf.Lerp(θgrid[i1], θgrid[i2], Mathf.Clamp01(w));
            }
            
            // 等分方向 CDF
            _thetaEdges[0] = -Mathf.PI;
            _thetaEdges[_Ntheta] = Mathf.PI;
            
            for (int i = 1; i < _Ntheta; i++)
            {
                float target = (totalDir * i) / _Ntheta;
                _thetaEdges[i] = FindThetaAtCdf(target);
            }
            
            // 计算各桶中心方向和宽度
            for (int itheta = 0; itheta < _Ntheta; itheta++)
            {
                float a = _thetaEdges[itheta];
                float b = _thetaEdges[itheta + 1];
                
                // 能量加权质心
                int j0 = Mathf.Clamp(Mathf.FloorToInt((a + Mathf.PI) / dθ_uniform), 0, M_theta - 2);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((b + Mathf.PI) / dθ_uniform), 1, M_theta - 1);
                
                float num = 0f, den = 0f;
                for (int j = j0 + 1; j <= j1; j++)
                {
                    float θL = θgrid[j - 1], θR = θgrid[j];
                    float dL = dgrid[j - 1], dR = dgrid[j];
                    
                    float L = Mathf.Max(θL, a);
                    float R = Mathf.Min(θR, b);
                    if (R <= L) continue;
                    
                    float dL2 = Mathf.Lerp(dL, dR, (L - θL) / (θR - θL));
                    float dR2 = Mathf.Lerp(dL, dR, (R - θL) / (θR - θL));
                    float segDen = 0.5f * (dL2 + dR2) * (R - L);
                    
                    float mid = 0.5f * (L + R);
                    float dMid = 0.5f * (dL2 + dR2);
                    float segNum = mid * dMid * (R - L);
                    
                    den += segDen;
                    num += segNum;
                }
                
                float θc = (den > 1e-20f) ? (num / den) : 0.5f * (a + b);
                _thetas[itheta] = θc;  // 相对风向的角度
                _deltaThetas[itheta] = Mathf.Max(1e-6f, b - a);
            }

            // 小数累加器重置
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

            // 注意：本函数的 batchSize ~ ω^4 * regionSize.x^2 
            // 仅剩与 regionSize 相关的因子在此处计算即可
            float regionFactor = regionSize.x * regionSize.x * 4f / (Mathf.PI * Mathf.PI * Mathf.PI * _g * _g);

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
                        // 使用预计算的方向（相对风向 + 风向角度）
                        float theta = _windDirection + _thetas[itheta];
                        Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

                        float S = JONSWAPSpectrumCached(omega, dir);
                        if (float.IsNaN(S) || float.IsInfinity(S) || S <= 0) continue;

                        float amplitude = Mathf.Sqrt(2f * S * _deltaOmegas[iw] * _deltaThetas[itheta]);
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
            float regionFrameFactor = deltaTime * regionSize.x * 4f / (Mathf.PI * Mathf.PI * Mathf.PI * _g);

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
                        // 使用预计算的方向（相对风向 + 风向角度）
                        float theta = _windDirection + _thetas[itheta];
                        float2 dir = new float2(Mathf.Cos(theta), Mathf.Sin(theta));

                        float S = JONSWAPSpectrumCached(omega, dir);
                        if (float.IsNaN(S) || float.IsInfinity(S) || S <= 0) continue;

                        float amplitude = Mathf.Sqrt(2f * S * _deltaOmegas[iw] * _deltaThetas[itheta]);
                        if (amplitude < 1e-5f) { continue; }
                        Vector2 pos = SamplePositionOnRegionEdge(regionCenter, regionSize, dir);

                        var particle = new WaveParticle
                        {
                            position = pos,
                            direction = math.normalize(dir),
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


        /// <summary>
        /// 计算带方向和深度修正的 JONSWAPGlenn 频谱密度 S(k,dir) 涌浪Swell用
        /// </summary>
        /// <param name="k">波数大小</param>
        /// <param name="dir">波数方向（单位向量）</param>
        /// <param name="g">重力加速度（9.81）</param>
        /// <param name="depth">水深</param>
        public float JONSWAPGlennSpectrum(
            float k,
            Vector2 dir,
            WavesSettings ws
        )
        {
            // 转成角频率 ω = sqrt(g k)
            float ω = Mathf.Sqrt(ws.g * k);
            float f = ω / (2f * Mathf.PI);
            float fp = 1 / Tp;
            //float γ = 3.3f;
            float γjg = 9.5f * Mathf.Pow(Hs, 0.34f) * fp;
            float σ = (f <= fp) ? 0.07f : 0.09f;
            //float ωp = 0.84f * g / Mathf.Max(U, 0.1f);
            float cc = 1.15f + 0.1688f * γjg - 0.925f / (1.909f + γjg);
            float c = (5f * Hs * Hs) / (16f * fp) * Mathf.Pow(cc, -1f);
            float r = Mathf.Exp(-1 * (f - fp) * (f - fp) / (2 * σ * σ * fp * fp));
            float Sjg = c * Mathf.Pow(f / fp, -5f) * Mathf.Exp(-5.0f / 4.0f * Mathf.Pow(f / fp, -4f)) * Mathf.Pow(γjg, r);
            //return Sjg;

            float μ = f > fp ? -2.5f : 5.0f;
            float sw = 16.0f * Mathf.Pow(f / fp, μ);
            float U = Mathf.Sqrt(ws.swell.windSpeed.x * ws.swell.windSpeed.x + ws.swell.windSpeed.y * ws.swell.windSpeed.y);
            Vector2 swellDir = new Vector2(ws.swell.windSpeed.x / U, ws.swell.windSpeed.y / U);

            //应用方向夹角修正
            float θ = Mathf.Atan2(dir.y, dir.x) - Mathf.Atan2(swellDir.y, swellDir.x);
            if (Mathf.Abs(θ) > Mathf.PI)
            {
                θ = 2 * Mathf.PI - Mathf.Abs(θ);
            }
            float DirSpectrum = MyGammaDouble(sw + 1) / (2 * Mathf.Sqrt(Mathf.PI) * MyGammaDouble(sw + 0.5f)) * Mathf.Pow(Mathf.Cos(θ / 2), 2 * sw);
            //最后转换到sk
            float Sk = Sjg * DirSpectrum / (4.0f * Mathf.PI) * Mathf.Sqrt(ws.g / k) / k;
            return (float)Sk;
        }

        /// <summary>
        /// 用来计算 Γ(z)（Gamma 函数）的近似值,实现了一个 Lanczos 近似
        /// </summary>
        private float MyGammaDouble(float z)
        {
            int g = 7;
            if (z < 0.5)
                return Mathf.PI / (Mathf.Sin(Mathf.PI * z) * MyGammaDouble(1 - z));
            z -= 1;
            float x = (float)p[0];
            for (var i = 1; i < g + 2; i++)
                x += (float)p[i] / (z + i);
            float t = z + g + 0.5f;
            return Mathf.Sqrt(2 * Mathf.PI) * (Mathf.Pow(t, z + 0.5f)) * Mathf.Exp(-t) * x;
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

        // 只在会“进入区域”的边上采样：根据 dir 决定候选边
        Vector2 SamplePositionOnRegionEdge(Vector2 center, Vector2 size, float2 dir)
        {
            dir = math.normalize(dir);
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;

            // 预先生成沿边方向的随机坐标，避免在不同分支重复声明
            float randAlongX = Random.Range(-halfX, halfX);
            float randAlongY = Random.Range(-halfY, halfY);

            // 轻微向内推进，避免落在边线上被误判越界
            float epsX = 1e-4f * size.x;
            float epsY = 1e-4f * size.y;

            // 根据传播方向，给可进入的边分配权重（近似通量权重）
            float ax = Mathf.Abs(dir.x);
            float ay = Mathf.Abs(dir.y);
            float wLeft = (dir.x > 1e-6f) ? ax : 0f;
            float wRight = (dir.x < -1e-6f) ? ax : 0f;
            float wBottom = (dir.y > 1e-6f) ? ay : 0f;
            float wTop = (dir.y < -1e-6f) ? ay : 0f;
            float wSum = wLeft + wRight + wBottom + wTop;

            // 退化情形：方向几乎为零，默认从左边进入
            if (wSum <= 1e-6f)
                return center + new Vector2(-halfX + epsX, randAlongY);

            // 按权重选边
            float r = Random.value * wSum;

            if (r < wLeft)                            // Left（内法线 +X）
                return center + new Vector2(-halfX + epsX, randAlongY);
            r -= wLeft;

            if (r < wRight)                           // Right（内法线 -X）
                return center + new Vector2(halfX - epsX, randAlongY);
            r -= wRight;

            if (r < wBottom)                          // Bottom（内法线 +Y）
                return center + new Vector2(randAlongX, -halfY + epsY);

            // Top（内法线 -Y）
            return center + new Vector2(randAlongX, halfY - epsY);
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
