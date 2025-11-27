using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// GPU-based hydrodynamics (Fluid → Solid).
/// This component DOES NOT know WaveParticleSystem.
/// All water data MUST be passed externally.
///
/// WaveParticleSystem drives:
///     solidHydro.ComputeForces(heightMap, density, Cd, resolution, regionSize, regionMin)
/// </summary>
public class SolidHydrodynamics : MonoBehaviour
{
    public int regionId;
    public ComputeShader perTriangleCS;
    public ComputeShader reduceCS;

    public Rigidbody rb;

    Mesh mesh;

    ComputeBuffer triBuffer;
    ComputeBuffer vertBuffer;

    ComputeBuffer forceBuffer;
    ComputeBuffer torqueBuffer;
    ComputeBuffer volumeBuffer;

    ComputeBuffer partialForce;
    ComputeBuffer partialTorque;
    ComputeBuffer partialVolume;

    uint triCount;
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

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        mesh = GetComponent<MeshFilter>().sharedMesh;

        triCount = (uint)mesh.triangles.Length / 3;

        // --- allocate triangle buffer ---
        triBuffer = new ComputeBuffer((int)triCount, sizeof(uint) * 3);

        List<uint3> tris = new List<uint3>();
        int[] t = mesh.triangles;
        for (int i = 0; i < t.Length; i += 3)
            tris.Add(new uint3((uint)t[i], (uint)t[i + 1], (uint)t[i + 2]));
        triBuffer.SetData(tris);

        // --- vertex buffer ---
        vertBuffer = new ComputeBuffer(mesh.vertexCount, sizeof(float) * 3);
        vertBuffer.SetData(mesh.vertices);

        // --- per triangle outputs ---
        forceBuffer  = new ComputeBuffer((int)triCount, sizeof(float) * 3);
        torqueBuffer = new ComputeBuffer((int)triCount, sizeof(float) * 3);
        volumeBuffer = new ComputeBuffer((int)triCount, sizeof(float) * 4);

        // --- reduction output ---
        partialForce  = new ComputeBuffer(64, sizeof(float) * 3);
        partialTorque = new ComputeBuffer(64, sizeof(float) * 3);
        partialVolume = new ComputeBuffer(64, sizeof(float) * 4);
    }

    void OnDestroy()
    {
        triBuffer?.Dispose();
        vertBuffer?.Dispose();
        forceBuffer?.Dispose();
        torqueBuffer?.Dispose();
        volumeBuffer?.Dispose();
        partialForce?.Dispose();
        partialTorque?.Dispose();
        partialVolume?.Dispose();
    }


    /// <summary>
    /// Compute forces acting on the rigidbody due to water.
    /// This MUST be called by WaveParticleSystem AFTER heightmap finished.
    /// </summary>
    public void ComputeForces(
        Texture heightMap,
        float waterDensity,
        float Cd,
        int resolution,
        float regionSize,
        Vector2 regionMin)
    {
        int kernel = perTriangleCS.FindKernel("CS_TriangleForces");

        // ------------------------------
        // set params
        // ------------------------------

        perTriangleCS.SetInt("_TriangleCount", (int)triCount);

        perTriangleCS.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        perTriangleCS.SetMatrix("_WorldToLocal", transform.worldToLocalMatrix);

        perTriangleCS.SetFloat("_WaterDensity", waterDensity);
        perTriangleCS.SetFloat("_Cd", Cd);
        perTriangleCS.SetFloat("_Gravity", -Physics.gravity.y);

        perTriangleCS.SetFloats("_ObjectVelocity", rb.velocity.x, rb.velocity.y, rb.velocity.z);
        perTriangleCS.SetFloats("_AngularVelocity", rb.angularVelocity.x, rb.angularVelocity.y, rb.angularVelocity.z);
        perTriangleCS.SetFloats("_ObjectCOM",
            rb.worldCenterOfMass.x, rb.worldCenterOfMass.y, rb.worldCenterOfMass.z);

        // region info
        perTriangleCS.SetInt("_Resolution", resolution);
        perTriangleCS.SetFloat("_RegionSize", regionSize);
        perTriangleCS.SetFloats("_RegionMin", regionMin.x, regionMin.y);

        // ------------------------------
        // buffers
        // ------------------------------
        perTriangleCS.SetBuffer(kernel, "_Triangles", triBuffer);
        perTriangleCS.SetBuffer(kernel, "_VerticesLocal", vertBuffer);

        perTriangleCS.SetBuffer(kernel, "_OutForce", forceBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutTorque", torqueBuffer);
        perTriangleCS.SetBuffer(kernel, "_OutVolume", volumeBuffer);

        // heightmap
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

        reduceCS.SetBuffer(reduceKernel, "_InForce", forceBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InTorque", torqueBuffer);
        reduceCS.SetBuffer(reduceKernel, "_InVolume", volumeBuffer);

        reduceCS.SetBuffer(reduceKernel, "_OutForce", partialForce);
        reduceCS.SetBuffer(reduceKernel, "_OutTorque", partialTorque);
        reduceCS.SetBuffer(reduceKernel, "_OutVolume", partialVolume);

        reduceCS.Dispatch(reduceKernel, 1, 1, 1);

        // ------------------------------
        // read back partial sums
        // ------------------------------
        Vector3[] F = new Vector3[64];
        Vector3[] T = new Vector3[64];
        Vector4[] V = new Vector4[64];

        partialForce.GetData(F);
        partialTorque.GetData(T);
        partialVolume.GetData(V);

        Vector3 totalF = Vector3.zero;
        Vector3 totalT = Vector3.zero;

        Vector3 volWeightedSum = Vector3.zero;
        float volSum = 0;

        for (int i = 0; i < 64; i++)
        {
            totalF += F[i];
            totalT += T[i];

            volWeightedSum += new Vector3(V[i].x, V[i].y, V[i].z);
            volSum += V[i].w;
        }

        // ------------------------------
        // buoyancy
        // ------------------------------
        Debug.Log("[SolidHydrodynamics] volSum: " + volSum);
        Debug.Log("[SolidHydrodynamics] volWeightedSum: " + volWeightedSum);
        if (volSum > 0)
        {
            Vector3 COB = volWeightedSum / volSum;
            Vector3 Fb = Vector3.up * waterDensity * -Physics.gravity.y * volSum;
            rb.AddForceAtPosition(Fb, COB);
            //Debug.Log("[SolidHydrodynamics] COB: " + COB);
            Debug.Log("[SolidHydrodynamics] Fb: " + Fb);
        }

        // ------------------------------
        // drag & torque
        // ------------------------------
        rb.AddForce(totalF);
        rb.AddTorque(totalT);
    }
}
