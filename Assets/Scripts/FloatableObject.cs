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
            if (water == null || localSamplePoints.Count == 0) return;
            float g = 9.81f;
            int curPointsCount = 0;
            foreach (var local in localSamplePoints)
            {
                // 1. 采样点的世界坐标
                Vector3 worldPos = transform.TransformPoint(local);
                //Debug.Log(local+": "+transform.position + ": "+worldPos);
                Vector2 probeXZ = new Vector2(worldPos.x, worldPos.z);

                // 2. 同时采样水面位置和法线
                Vector3 normal;
                Vector3 surfacePos = water.SampleWaterSurfacePositionAndNormal(regionIndex, probeXZ, out normal);
                //Debug.Log("采样点：" + probeXZ + "  水面高度：" + surfacePos.y);
                // 3. 计算采样点的相对水深（表面y-物体点y）
                float depth = surfacePos.y - worldPos.y;

                // 4. 点在水下，施加浮力（浮力方向用法线！）
                if (depth > 0)
                {
                    float forceMag = density * g * dv;
                    rb.AddForceAtPosition(normal * forceMag, worldPos);
                    // 这样浮力方向随水面斜面变化
                    curPointsCount++;
                }
            }
            float deltaV = (curPointsCount - prevPointCount) * dv;
            
            if (deltaV > 0)
            {
                //Debug.Log($"deltaV: {deltaV}");
                water.GenerateWaveParticles(regionIndex, new Vector2(transform.position.x, transform.position.z), deltaV);
            }
            prevPointCount = curPointsCount;
        }
    }
}
