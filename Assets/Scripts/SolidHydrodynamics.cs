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

    void Start()
    {
        rb   = GetComponent<Rigidbody>();
        mesh = GetComponent<MeshFilter>().sharedMesh;

        triCount = (uint)mesh.triangles.Length / 3;
        Debug.Log($"[SolidHydro] Mesh has {triCount} triangles.");

        if (triCount == 0)
        {
            Debug.LogWarning("[SolidHydro] triCount == 0, component will do nothing.");
            return;
        }

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
    }

    void OnDestroy()
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
        Vector3 F_drag = totalFW + totalFA;
        F_drag = new Vector3(0f, 0f, 0f);  // 你现在暂时关掉阻力

        //rb.AddForce(F_drag);
        //rb.AddTorque(totalT);

        // debug: 分解输出
        Vector3 Fg   = rb.mass * Physics.gravity;
        Vector3 F_net = Fb + F_drag + Fg;

        Debug.Log(
            $"[SolidHydro] volSum={volSum}, " +
            $"Fb={Fb}, F_water={totalFW}, F_air={totalFA}, Fg={Fg}, F_net={F_net}, " +
            $"COB={COB}, Q_vert={totalVertFlux}, Q_horz={totalHorzFlux}"
        );
    }
}
