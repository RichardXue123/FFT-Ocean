using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// GPU-based hydrodynamics (Fluid → Solid & Solid → Fluid).
/// 不知道 WaveParticleSystem，所有水面信息从外部传入。
/// </summary>
public class SolidHydrodynamics : MonoBehaviour
{
    public int regionId;
    public ComputeShader perTriangleCS;
    public ComputeShader reduceCS;

    [Header("Hydrodynamics")]
    [Tooltip("Water Drag Coefficient. Typical values: 0.05 - 0.2 for streamlined hulls.")]
    public float CdWater = 0.1f;
    
    [Tooltip("Air Drag Coefficient.")]
    public float CdAir = 0.01f;

    [Tooltip("拖入你生成的简化物理网格。如果不填，默认使用物体身上的渲染网格。")]
    public Mesh physicalMesh;

    public Rigidbody rb;

    Mesh mesh;

    // 输入
    ComputeBuffer triBuffer;
    ComputeBuffer vertBuffer;

    // per-triangle 输出：水力 / 风力 / 力矩 / 体积
    ComputeBuffer forceWaterBuffer;
    ComputeBuffer forceAirBuffer;
    ComputeBuffer torqueBuffer;
    ComputeBuffer volumeBuffer;

    // per-triangle 输出：体积通量 Solid → Fluid
    // 对应 HLSL: RWStructuredBuffer<float> _OutVertFlux; _OutHorzFlux;
    ComputeBuffer vertFluxBuffer;
    ComputeBuffer horzFluxBuffer;

    // reduction 输出
    ComputeBuffer partialForceWater;
    ComputeBuffer partialForceAir;
    ComputeBuffer partialTorque;
    ComputeBuffer partialVolume;

    // reduction 输出：体积通量
    ComputeBuffer partialVertFlux;
    ComputeBuffer partialHorzFlux;

    uint triCount;

    // Solid → Fluid 总体积通量（汇总后的结果，给 WaveParticleSystem 用）
    public float totalVertFlux;   // ∑ 三角形 vertFlux，单位 m^3/s
    public float totalHorzFlux;   // ∑ 三角形 horzFlux，单位 m^3/s

    public float smoothedVertFlux;
    
    public float smoothedHorzFlux;

    // [Debug] 存储上一帧的力学数据用于 Gizmos 绘制
    private Vector3 debug_Fb;
    private Vector3 debug_COB;
    private Vector3 debug_Fg;
    private Vector3 debug_Fdrag;

    [Header("Mass Helper")]
    [Tooltip("Target density (kg/m3) for auto-mass calculation. Water is 1000. Typical boat overall density is 200-600.")]
    public float targetDensity = 400f;

    [ContextMenu("Auto Calculate Mass")]
    public void AutoCalculateMass()
    {
        Mesh m = physicalMesh;
        if (m == null)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null) m = mf.sharedMesh;
        }

        if (m == null)
        {
            Debug.LogError("[SolidHydro] No mesh found to calculate volume.");
            return;
        }

        float rawVolume = CalculateMeshVolume(m);
        
        // Apply world scale
        Vector3 scale = transform.lossyScale;
        float scaledVolume = rawVolume * Mathf.Abs(scale.x * scale.y * scale.z);

        float suggestedMass = scaledVolume * targetDensity;

        Debug.Log($"[SolidHydro] Mesh Raw Volume: {rawVolume:F3} m^3");
        Debug.Log($"[SolidHydro] Scaled Volume (World): {scaledVolume:F3} m^3");
        Debug.Log($"[SolidHydro] Calculated Mass (Density {targetDensity}): {suggestedMass:F1} kg");

        if (rb != null)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(rb, "Auto Set Mass");
#endif
            rb.mass = suggestedMass;
            Debug.Log($"[SolidHydro] Rigidbody mass has been updated to {suggestedMass:F1} kg.");
        }
    }

    private float CalculateMeshVolume(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        double volume = 0.0;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 p1 = vertices[triangles[i + 0]];
            Vector3 p2 = vertices[triangles[i + 1]];
            Vector3 p3 = vertices[triangles[i + 2]];

            // Signed volume of tetrahedron from origin
            // V = (1/6) * det(p1, p2, p3) = (1/6) * (p1 . (p2 x p3))
            volume += Vector3.Dot(p1, Vector3.Cross(p2, p3)) / 6.0f;
        }

        return (float)Mathf.Abs((float)volume);
    }

    [Header("Debug")]
    public bool showDebugGizmos = true;
    public Color colorSubmerged = new Color(0, 1, 1, 0.3f); // 浅蓝
    public Color colorSurface = new Color(1, 0, 0, 0.3f);   // 浅红

    // Debug data
    Vector4[] debugVolumeData;
    Vector3[] debugVertices;
    int[] debugTriangles;

    // Simple substitute for HLSL uint3
    struct uint3
    {
        public uint x;
        public uint y;
        public uint z;

        public uint3(uint _x, uint _y, uint _z)
        {
            x = _x;
            y = _y;
            z = _z;
        }
    }

    const int REDUCE_SIZE = 64;

    void OnEnable()
    {
        rb = GetComponent<Rigidbody>();
        
        if (physicalMesh != null)
        {
            mesh = physicalMesh;
        }
        else
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
        }

        if (mesh == null)
        {
            Debug.LogError("[SolidHydro] No mesh found! Assign a Physical Mesh or add a MeshFilter.");
            return;
        }

        triCount = (uint)mesh.triangles.Length / 3;
        Debug.Log($"[SolidHydro] Using mesh: {mesh.name}, Triangles: {triCount}");

        if (triCount == 0)
        {
            Debug.LogWarning("[SolidHydro] triCount == 0, component will do nothing.");
            return;
        }

        ReleaseBuffers();

        // --- triangle buffer ---
        triBuffer = new ComputeBuffer((int)triCount, sizeof(uint) * 3);

        var tris = new List<uint3>();
        int[] t = mesh.triangles;
        for (int i = 0; i < t.Length; i += 3)
            tris.Add(new uint3((uint)t[i], (uint)t[i + 1], (uint)t[i + 2]));
        triBuffer.SetData(tris);

        // --- vertex buffer ---
        vertBuffer = new ComputeBuffer(mesh.vertexCount, sizeof(float) * 3);
        vertBuffer.SetData(mesh.vertices);

        // --- per triangle outputs ---
        forceWaterBuffer = new ComputeBuffer((int)triCount, sizeof(float) * 3);
        forceAirBuffer   = new ComputeBuffer((int)triCount, sizeof(float) * 3);
        torqueBuffer     = new ComputeBuffer((int)triCount, sizeof(float) * 3);
        volumeBuffer     = new ComputeBuffer((int)triCount, sizeof(float) * 4);

        // 新增：体积通量 per-triangle
        vertFluxBuffer   = new ComputeBuffer((int)triCount, sizeof(float));
        horzFluxBuffer   = new ComputeBuffer((int)triCount, sizeof(float));

        // --- reduction outputs ---
        partialForceWater = new ComputeBuffer(REDUCE_SIZE, sizeof(float) * 3);
        partialForceAir   = new ComputeBuffer(REDUCE_SIZE, sizeof(float) * 3);
        partialTorque     = new ComputeBuffer(REDUCE_SIZE, sizeof(float) * 3);
        partialVolume     = new ComputeBuffer(REDUCE_SIZE, sizeof(float) * 4);

        // 新增：体积通量 reduction 输出
        partialVertFlux   = new ComputeBuffer(REDUCE_SIZE, sizeof(float));
        partialHorzFlux   = new ComputeBuffer(REDUCE_SIZE, sizeof(float));

        // Initialize debug arrays
        if (triCount > 0)
        {
            debugVolumeData = new Vector4[triCount];
            debugVertices = mesh.vertices;
            debugTriangles = mesh.triangles;
        }
    }

    void OnDisable()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        triBuffer?.Dispose();
        vertBuffer?.Dispose();

        forceWaterBuffer?.Dispose();
        forceAirBuffer?.Dispose();
        torqueBuffer?.Dispose();
        volumeBuffer?.Dispose();

        vertFluxBuffer?.Dispose();
        horzFluxBuffer?.Dispose();

        partialForceWater?.Dispose();
        partialForceAir?.Dispose();
        partialTorque?.Dispose();
        partialVolume?.Dispose();

        partialVertFlux?.Dispose();
        partialHorzFlux?.Dispose();

        triBuffer = null;
        vertBuffer = null;
        forceWaterBuffer = null;
        forceAirBuffer = null;
        torqueBuffer = null;
        volumeBuffer = null;
        vertFluxBuffer = null;
        horzFluxBuffer = null;
        partialForceWater = null;
        partialForceAir = null;
        partialTorque = null;
        partialVolume = null;
        partialVertFlux = null;
        partialHorzFlux = null;
    }

    /// <summary>
    /// 由外部（水面系统）驱动，每帧在 heightMap 更新后调用。
    /// </summary>
    public void ComputeForces(
        Texture heightMap,
        float waterDensity,
        float cdWater,
        float airDensity,
        float cdAir,
        Vector3 windVelocity,
        int resolution,
        float regionSize,
        Vector2 regionMin)
    {
        if (triCount == 0) return;

        int kernel = perTriangleCS.FindKernel("CS_TriangleForces");

        // ------------------------------
        // set params
        // ------------------------------
        perTriangleCS.SetInt("_TriangleCount", (int)triCount);
        perTriangleCS.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        perTriangleCS.SetMatrix("_WorldToLocal", transform.worldToLocalMatrix);

        perTriangleCS.SetFloat("_WaterDensity", waterDensity);
        perTriangleCS.SetFloat("_CdWater", cdWater);
        perTriangleCS.SetFloat("_AirDensity", airDensity);
        perTriangleCS.SetFloat("_CdAir", cdAir);
        perTriangleCS.SetFloat("_Gravity", -Physics.gravity.y);

        perTriangleCS.SetFloats("_ObjectVelocity", rb.velocity.x, rb.velocity.y, rb.velocity.z);
        perTriangleCS.SetFloats("_AngularVelocity", rb.angularVelocity.x, rb.angularVelocity.y, rb.angularVelocity.z);
        perTriangleCS.SetFloats("_ObjectCOM",
            rb.worldCenterOfMass.x,
            rb.worldCenterOfMass.y,
            rb.worldCenterOfMass.z);

        perTriangleCS.SetVector("_WindVelocity", windVelocity);

        perTriangleCS.SetInt("_Resolution", resolution);
        perTriangleCS.SetFloat("_RegionSize", regionSize);
        perTriangleCS.SetFloats("_RegionMin", regionMin.x, regionMin.y);

        // ------------------------------
        // bind buffers / textures
        // ------------------------------
        perTriangleCS.SetBuffer(kernel, "_Triangles",     triBuffer);
        perTriangleCS.SetBuffer(kernel, "_VerticesLocal", vertBuffer);

        perTriangleCS.SetBuffer(kernel, "_OutForceWater", forceWaterBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutForceAir",   forceAirBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutTorque",     torqueBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutVolume",     volumeBuffer);

        // 新增：Solid → Fluid 通量输出
        perTriangleCS.SetBuffer(kernel, "_OutVertFlux",   vertFluxBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutHorzFlux",   horzFluxBuffer);

        perTriangleCS.SetTexture(kernel, "_HeightMap", heightMap);

        // ------------------------------
        // dispatch per-triangle kernel
        // ------------------------------
        uint groups = (triCount + 63) / 64;
        perTriangleCS.Dispatch(kernel, (int)groups, 1, 1);

#if UNITY_EDITOR
        if (showDebugGizmos && volumeBuffer != null && debugVolumeData != null)
        {
            volumeBuffer.GetData(debugVolumeData);
        }
#endif

        // ------------------------------
        // reduction
        // ------------------------------
        int reduceKernel = reduceCS.FindKernel("CS_Reduce");
        reduceCS.SetInt("_Count", (int)triCount);

        // 这里假设你的 Reduce.compute 已经改成:
        // StructuredBuffer<float3> _InForceWater, _InForceAir, _InTorque;
        // StructuredBuffer<float4> _InVolume;
        // StructuredBuffer<float>  _InVertFlux, _InHorzFlux;
        // RWStructuredBuffer<float3> _OutForceWater, _OutForceAir, _OutTorque;
        // RWStructuredBuffer<float4> _OutVolume;
        // RWStructuredBuffer<float>  _OutVertFlux, _OutHorzFlux;

        reduceCS.SetBuffer(reduceKernel, "_InForceWater",  forceWaterBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InForceAir",    forceAirBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InTorque",      torqueBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InVolume",      volumeBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InVertFlux",    vertFluxBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InHorzFlux",    horzFluxBuffer);

        reduceCS.SetBuffer(reduceKernel, "_OutForceWater", partialForceWater);
        reduceCS.SetBuffer(reduceKernel, "_OutForceAir",   partialForceAir);
        reduceCS.SetBuffer(reduceKernel, "_OutTorque",     partialTorque);
        reduceCS.SetBuffer(reduceKernel, "_OutVolume",     partialVolume);
        reduceCS.SetBuffer(reduceKernel, "_OutVertFlux",   partialVertFlux);
        reduceCS.SetBuffer(reduceKernel, "_OutHorzFlux",   partialHorzFlux);

        reduceCS.Dispatch(reduceKernel, 1, 1, 1);

        // ------------------------------
        // read back partial sums
        // ------------------------------
        Vector3[] FW = new Vector3[REDUCE_SIZE];
        Vector3[] FA = new Vector3[REDUCE_SIZE];
        Vector3[] T  = new Vector3[REDUCE_SIZE];
        Vector4[] V  = new Vector4[REDUCE_SIZE];

        float[] vertFluxPart = new float[REDUCE_SIZE];
        float[] horzFluxPart = new float[REDUCE_SIZE];

        partialForceWater.GetData(FW);
        partialForceAir.GetData(FA);
        partialTorque.GetData(T);
        partialVolume.GetData(V);
        partialVertFlux.GetData(vertFluxPart);
        partialHorzFlux.GetData(horzFluxPart);

        Vector3 totalFW = Vector3.zero;
        Vector3 totalFA = Vector3.zero;
        Vector3 totalT  = Vector3.zero;

        Vector3 volWeightedSum = Vector3.zero;
        float   volSum         = 0f;

        totalVertFlux = 0f;
        totalHorzFlux = 0f;

        for (int i = 0; i < REDUCE_SIZE; i++)
        {
            totalFW += FW[i];
            totalFA += FA[i];
            totalT  += T[i];

            volWeightedSum += new Vector3(V[i].x, V[i].y, V[i].z);
            volSum         += V[i].w;

            totalVertFlux  += vertFluxPart[i];
            totalHorzFlux  += horzFluxPart[i];
        }

        // 指数平滑，alpha 越小越平滑
        float alpha = 0.1f;
        smoothedVertFlux = Mathf.Lerp(smoothedVertFlux, totalVertFlux, alpha);
        smoothedHorzFlux = Mathf.Lerp(smoothedHorzFlux, totalHorzFlux, alpha);
        // ------------------------------
        // buoyancy
        // ------------------------------
        Vector3 Fb  = Vector3.zero;
        Vector3 COB = rb.worldCenterOfMass;

        if (volSum > 0)
        {
            COB = volWeightedSum / volSum;
            Fb  = Vector3.up * waterDensity * -Physics.gravity.y * volSum;
            rb.AddForceAtPosition(Fb, COB);
        }

        // ------------------------------
        // drag & torque
        // ------------------------------
        // Vector3 F_drag = totalFW + totalFA;

        Vector3 F_drag = totalFW;
        
        // 启用阻力
        // rb.AddForce(F_drag);
        rb.AddForceAtPosition(F_drag, COB);
        // rb.AddTorque(totalT);

        // debug: 分解输出
        Vector3 Fg   = rb.mass * Physics.gravity;
        Vector3 F_net = Fb + F_drag + Fg;

        // 存储数据给 OnDrawGizmos 使用
        debug_Fb = Fb;
        debug_COB = COB;
        debug_Fg = Fg;
        debug_Fdrag = F_drag;

        // 只有在开启 Debug Gizmos 时才打印详细日志，避免刷屏
        if (showDebugGizmos)
        {
            Debug.Log(
                $"[SolidHydro] Force Analysis:\n" +
                $"  Buoyancy (Fb): {Fb} (Mag: {Fb.magnitude:F1})\n" +
                $"  Drag (Fd):     {F_drag} (Mag: {F_drag.magnitude:F1})\n" +
                $"  Gravity (Fg):  {Fg} (Mag: {Fg.magnitude:F1})\n" +
                $"  Submerged Vol: {volSum:F3} m^3"
            );
        }
    }

    void OnDrawGizmos()
    {
        if (!showDebugGizmos) return;

        // 1. 画出浮力和重力的箭头
        // 使用存储的上一帧数据
        float arrowScale = 0.0005f; // 力的缩放比例，根据力的大小调整
        float headSize = 0.5f;
        float pointSize = 0.2f; // 作用力点的大小

        // 重力：红色箭头，从重心向下
        if (rb != null)
        {
            Vector3 com = rb.worldCenterOfMass;
            
            // 重力点
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(com, pointSize);
            DrawArrow(com, debug_Fg * arrowScale, Color.red, headSize);
        }

        // 阻力：青色箭头，从浮心出发（与浮力作用点一致）
        if (debug_Fdrag.magnitude > 0.1f)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(debug_COB, pointSize);
            DrawArrow(debug_COB, debug_Fdrag * arrowScale, Color.cyan, headSize);
        }

        // 浮力：绿色箭头，从浮心向上
        if (debug_Fb.magnitude > 0.1f)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(debug_COB, pointSize);
            DrawArrow(debug_COB, debug_Fb * arrowScale, Color.green, headSize);
        }

        if (debugVolumeData == null || debugVertices == null || debugTriangles == null) return;

        Gizmos.matrix = transform.localToWorldMatrix;

        for (int i = 0; i < triCount; i++)
        {
            // w component holds the submerged volume
            bool isSubmerged = debugVolumeData[i].w > 1e-5f;

            Gizmos.color = isSubmerged ? colorSubmerged : colorSurface;

            int i0 = debugTriangles[i * 3 + 0];
            int i1 = debugTriangles[i * 3 + 1];
            int i2 = debugTriangles[i * 3 + 2];

            Vector3 v0 = debugVertices[i0];
            Vector3 v1 = debugVertices[i1];
            Vector3 v2 = debugVertices[i2];

            Gizmos.DrawLine(v0, v1);
            Gizmos.DrawLine(v1, v2);
            Gizmos.DrawLine(v2, v0);
            
            // Optional: Draw a small cross at the center to make it more visible
            // Vector3 center = (v0 + v1 + v2) / 3.0f;
            // Gizmos.DrawRay(center, Vector3.up * 0.05f);
        }
    }

    // 简单的画箭头辅助函数
    void DrawArrow(Vector3 pos, Vector3 direction, Color color, float headSize = 0.25f)
    {
        if (direction == Vector3.zero) return;
        
        Gizmos.color = color;
        Gizmos.DrawRay(pos, direction);
        
        Vector3 right = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 + 20, 0) * new Vector3(0, 0, 1);
        Vector3 left = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 - 20, 0) * new Vector3(0, 0, 1);
        
        Vector3 endPos = pos + direction;
        Gizmos.DrawRay(endPos, right * headSize);
        Gizmos.DrawRay(endPos, left * headSize);
    }
}
