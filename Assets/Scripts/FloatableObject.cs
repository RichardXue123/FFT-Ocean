using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Assets.Scripts
{
    [RequireComponent(typeof(Rigidbody))]
    public class FloatableObject : MonoBehaviour
    {
        public WaveParticleSystem water;
        [SerializeField] public int regionIndex = 0;
        [Tooltip("体积均匀采样点数，越多精度越高")]
        public int sampleCount = 1000;
        [Tooltip("物体密度（kg/m³），默认为水1000")]
        public float density = 1000f;
        [Tooltip("用于生成尾迹/涟漪的方向采样数")]
        public int DirSampleCount = 16;

        // 用于存储物体局部采样点坐标（相对于物体局部中心）
        List<Vector3> localSamplePoints = new List<Vector3>();
        // 物体估算体积
        float objectVolume = 1f;
        Rigidbody rb;
        int prevPointCount;
        float dv;
        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            GenerateSamplePointsInBounds();
            // objectVolume 可用网格体积或包围盒体积估算
            objectVolume = EstimateObjectVolume();
            Debug.Log("objectVolume: " + objectVolume);
            dv = objectVolume / sampleCount; // 每点代表的小体积
            prevPointCount = 0;
        }

        // 1. 均匀或随机生成采样点（包围盒内随机点，mesh内点可用advanced方法）
        void GenerateSamplePointsInBounds()
        {
            localSamplePoints.Clear();
            var meshFilter = GetComponent<MeshFilter>();
            var bounds = GetComponent<Renderer>().bounds;
            var localBounds = new Bounds(transform.InverseTransformPoint(bounds.center), bounds.size);
            var meshBounds = meshFilter.sharedMesh.bounds;
            Debug.Log("localbounds:" + localBounds);
            int generated = 0, maxTry = sampleCount * 10;
            while (generated < sampleCount && maxTry-- > 0)
            {
                Vector3 local = new Vector3(
                    Random.Range(meshBounds.min.x, meshBounds.max.x),
                    Random.Range(meshBounds.min.y, meshBounds.max.y),
                    Random.Range(meshBounds.min.z, meshBounds.max.z));
                localSamplePoints.Add(local);
                //Debug.Log($"Point "+generated+": " + local);
                generated++;
            }
        }

        // 2. 估算体积（可直接用包围盒近似，也可用mesh体积）
        float EstimateObjectVolume()
        {
            var bounds = GetComponent<Renderer>().bounds;
            return bounds.size.x * bounds.size.y * bounds.size.z;
            // 或用MeshUtility/Collider的体积方法
        }

        void FixedUpdate()
        {
            if (water == null || localSamplePoints.Count == 0 || sampleCount <= 0) return;

            const float g = 9.81f;
            int submergedCount = 0;

            // === 浮力 ===
            foreach (var local in localSamplePoints)
            {
                Vector3 worldPos = transform.TransformPoint(local);
                Vector2 probeXZ = new Vector2(worldPos.x, worldPos.z);

                Vector3 normal;
                Vector3 surfacePos = water.SampleWaterSurfacePositionAndNormal(regionIndex, probeXZ, out normal);

                float depth = surfacePos.y - worldPos.y;
                if (depth > 0f)
                {
                    // 简化的“点式浮力”，方向用水面法线
                    float forceMag = density * g * dv;
                    rb.AddForceAtPosition(normal * forceMag, worldPos);
                    submergedCount++;
                }
            }

            // === 计算当前淹没体积 V_inwater ===
            float V_inwater = submergedCount * dv;

            // === 质心速度 ===
            Vector3 v = rb.velocity;

            // === 时间步长 ===
            float dt = Time.fixedDeltaTime;

            // 传给水系统，让水系统依据 V_inwater、v、dirSamples 生成涟漪/尾迹
            // （半径选桶、方向分布、正负振幅在水系统里做）
            if (V_inwater > 0f || v.sqrMagnitude > 1e-2f)
            {
                Vector2 posXZ = new Vector2(transform.position.x, transform.position.z);
                water.GenerateWaveParticles(
                    regionIndex,
                    posXZ,
                    V_inwater,
                    v,
                    dt,
                    DirSampleCount
                );
            }
        }
    }
}
