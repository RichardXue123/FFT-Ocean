using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Assets.Scripts
{
    [RequireComponent(typeof(Rigidbody)), RequireComponent(typeof(MeshFilter))]
    public class FloatableObject : MonoBehaviour
    {
        [Header("Info")]
        [SerializeField] private float estimatedVolume_m3;  // 估算体积（m³），仅显示

        public WaveParticleSystem water;
        [SerializeField] public int regionIndex = 0;
        [Tooltip("体积均匀采样点数，越多精度越高")]
        public int sampleCount = 1000;
        [Tooltip("物体密度（kg/m³），默认为水1000")]
        public float density = 1000f;
        [Tooltip("用于生成尾迹/涟漪的方向采样数")]
        public int DirSampleCount = 32;

        List<Vector3> localSamplePoints = new();
        float objectVolume = 1f;
        Rigidbody rb;
        float dv;

        // 为内部采样准备
        //MeshCollider mc;      // 用于“点在网格内”测试
        bool mcAddedAtRuntime; // 我们是否在运行时添加了它

        void Awake()
        {
            rb = GetComponent<Rigidbody>();

            GenerateSamplePointsInsideMesh();              // ← 只采样网格内部
            objectVolume = ComputeMeshWorldVolume();       // ← 精确网格体积
            dv = (objectVolume > 0f && sampleCount > 0) ? objectVolume / sampleCount : 0f;
            estimatedVolume_m3 = objectVolume;   // ← 显示到 Inspector

            dv = (objectVolume > 0f && sampleCount > 0) ? objectVolume / sampleCount : 0f;

            Debug.Log($"[Floatable] samples={localSamplePoints.Count}, volume≈{objectVolume:F4} m³, dv={dv:E2}");
        }
#if UNITY_EDITOR
        void OnValidate()
        {
            // 编辑器下：有 Mesh 才尝试刷新
            var mf = GetComponent<MeshFilter>();
            if (!Application.isPlaying && mf != null && mf.sharedMesh != null)
            {
                estimatedVolume_m3 = ComputeMeshWorldVolume();
            }
        }

        [ContextMenu("Recompute Estimated Volume")]
        void RecomputeEstimatedVolume()
        {
            estimatedVolume_m3 = ComputeMeshWorldVolume();
        }
#endif
        void OnDestroy()
        {

        }


        // === 只在网格内部生成采样点（一次性） ===
        void GenerateSamplePointsInsideMesh()
        {
            localSamplePoints.Clear();

            var mf = GetComponent<MeshFilter>();
            var mesh = mf ? mf.sharedMesh : null;
            if (mesh == null)
            {
                Debug.LogWarning("[Floatable] No MeshFilter/mesh found.");
                return;
            }

            // 1) 创建临时查询物体（不挂在刚体层级内）
            var temp = new GameObject($"{name}_QueryCollider_TEMP");
            temp.hideFlags = HideFlags.HideAndDontSave;

            // 放到一个“不会与船体碰撞”的层（用内置 Ignore Raycast=2 比较方便）
            int queryLayer = 2;                 // Ignore Raycast
            int boatLayer = gameObject.layer;  // 船当前的层
            temp.layer = queryLayer;

            // 暂时忽略两层之间的碰撞
            bool prevIgnore = Physics.GetIgnoreLayerCollision(queryLayer, boatLayer);
            Physics.IgnoreLayerCollision(queryLayer, boatLayer, true);

            // 非凸 MeshCollider（默认 isTrigger=false）
            var mc = temp.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;      // concave 查询才准确
                                    // mc.isTrigger = false; // 不要设成 Trigger，否则就会报错

            // 复制世界变换（不要设父子关系）
            temp.transform.position = transform.position;
            temp.transform.rotation = transform.rotation;
            temp.transform.localScale = transform.lossyScale;

            try
            {
                Bounds localBounds = mesh.bounds;
                float charSize = localBounds.extents.magnitude;
                float eps = Mathf.Max(1e-4f * charSize, 1e-6f);

                int generated = 0, maxTries = sampleCount * 50;
                while (generated < sampleCount && maxTries-- > 0)
                {
                    Vector3 local = new Vector3(
                        Random.Range(localBounds.min.x, localBounds.max.x),
                        Random.Range(localBounds.min.y, localBounds.max.y),
                        Random.Range(localBounds.min.z, localBounds.max.z));

                    Vector3 world = transform.TransformPoint(local);
                    Vector3 closest = mc.ClosestPoint(world);

                    // 点在网格内部：ClosestPoint 返回自己（距离≈0）
                    if ((closest - world).sqrMagnitude <= eps * eps)
                    {
                        localSamplePoints.Add(local);
                        generated++;
                    }
                }

                if (generated < sampleCount)
                {
                    Debug.LogWarning($"[Floatable] 仅生成到 {generated}/{sampleCount} 个内部采样点；" +
                                     $"可增大 maxTries 或检查网格是否闭合。");
                }
            }
            finally
            {
                // 恢复 Layer 碰撞设置 & 销毁临时物体
                Physics.IgnoreLayerCollision(queryLayer, boatLayer, prevIgnore);
                if (Application.isPlaying) Destroy(temp); else DestroyImmediate(temp);
            }
        }



        // === 精确计算网格在世界空间的体积（要求网格闭合、面朝向一致） ===
        float ComputeMeshWorldVolume()
        {
            var mf = GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;
            var tris = mesh.triangles;

            // 为简洁，直接把顶点变到世界空间再累加
            var M = transform.localToWorldMatrix;
            double vol = 0.0;

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 p0 = M.MultiplyPoint3x4(verts[tris[i]]);
                Vector3 p1 = M.MultiplyPoint3x4(verts[tris[i + 1]]);
                Vector3 p2 = M.MultiplyPoint3x4(verts[tris[i + 2]]);
                vol += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6.0; // 有向体积
            }

            return Mathf.Abs((float)vol); // 体积取绝对值
        }

        void FixedUpdate()
        {
            if (water == null || localSamplePoints.Count == 0 || sampleCount <= 0) return;

            const float g = 9.81f;
            int submergedCount = 0;

            foreach (var local in localSamplePoints)
            {
                Vector3 worldPos = transform.TransformPoint(local);
                Vector2 probeXZ = new Vector2(worldPos.x, worldPos.z);

                Vector3 normal;
                Vector3 surfacePos = water.SampleWaterSurfacePositionAndNormal(regionIndex, probeXZ, out normal);

                float depth = surfacePos.y - worldPos.y;
                if (depth > 0f)
                {
                    float forceMag = density * g * dv;   // 均匀体素的阿基米德力
                    rb.AddForceAtPosition(normal * forceMag, worldPos);
                    submergedCount++;
                }
            }

            float V_inwater = submergedCount * dv;
            Vector3 v = rb.velocity;
            float dt = Time.fixedDeltaTime;

            if (V_inwater > 0f || v.sqrMagnitude > 1e-2f)
            {
                Vector2 posXZ = new Vector2(transform.position.x, transform.position.z);
                water.GenerateWaveParticles(regionIndex, posXZ, V_inwater, v, dt, DirSampleCount);
            }
        }
    }
}
