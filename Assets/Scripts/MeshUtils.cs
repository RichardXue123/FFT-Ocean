using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public static class MeshUtils
{
    /// <summary>
    /// 创建一个分辨率为 (resolutionX x resolutionY)、物理尺寸为 (width x length) 的平面 Mesh。
    /// </summary>
    public static Mesh CreateHighResPlaneMesh(int resolutionX, int resolutionY, float width, float length)
    {
        int vertsX = resolutionX + 1;
        int vertsY = resolutionY + 1;
        int vertCount = vertsX * vertsY;
        int triCount = resolutionX * resolutionY * 2;

        var mesh = new Mesh();
        mesh.name = $"Plane_{resolutionX}x{resolutionY}";

        // 顶点索引如果超过 65535，就必须用 UInt32
        if (vertCount > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        // 1) 生成顶点和法线
        Vector3[] vertices = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];

        float dx = width / resolutionX;
        float dz = length / resolutionY;
        for (int y = 0; y < vertsY; y++)
        {
            for (int x = 0; x < vertsX; x++)
            {
                int idx = x + y * vertsX;
                float vx = -width * 0.5f + x * dx;   // 居中显示
                float vz = -length * 0.5f + y * dz;
                vertices[idx] = new Vector3(vx, 0, vz);
                normals[idx] = Vector3.up;
                uvs[idx] = new Vector2((float)x / resolutionX, (float)y / resolutionY);
            }
        }

        // 2) 生成三角形索引
        int[] triangles = new int[triCount * 3];
        int ti = 0;
        for (int y = 0; y < resolutionY; y++)
        {
            for (int x = 0; x < resolutionX; x++)
            {
                int i0 = x + y * vertsX;
                int i1 = (x + 1) + y * vertsX;
                int i2 = x + (y + 1) * vertsX;
                int i3 = (x + 1) + (y + 1) * vertsX;

                // 交错切分，避免长条容易产生条纹
                if ((x + y) % 2 == 0)
                {
                    triangles[ti++] = i0;
                    triangles[ti++] = i2;
                    triangles[ti++] = i1;

                    triangles[ti++] = i1;
                    triangles[ti++] = i2;
                    triangles[ti++] = i3;
                }
                else
                {
                    triangles[ti++] = i0;
                    triangles[ti++] = i2;
                    triangles[ti++] = i3;

                    triangles[ti++] = i0;
                    triangles[ti++] = i3;
                    triangles[ti++] = i1;
                }
            }
        }

        // 3) 赋值到 Mesh
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;

        return mesh;
    }


    /// <summary>
    /// 在场景中创建一个 GameObject，挂载 MeshFilter/MeshRenderer，
    /// 并设置父节点、名称、网格和材质，返回它的 Transform 和 MeshRenderer。
    /// </summary>
    public static Element InstantiateElement(string name, Mesh mesh, Material material, Transform parent = null)
    {
        Debug.Log("Attempting to instantiate plane...");
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);

        // MeshFilter
        var mf = go.AddComponent<MeshFilter>();
        mf.mesh = mesh;

        // MeshRenderer
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = true;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        mr.allowOcclusionWhenDynamic = false;

        return new Element(go.transform, mr);
    }

    /// <summary>
    /// 简便返回值，包含 Transform 和 MeshRenderer
    /// </summary>
    public struct Element
    {
        public Transform Transform;
        public MeshRenderer MeshRenderer;
        public Element(Transform t, MeshRenderer mr)
        {
            Transform = t;
            MeshRenderer = mr;
        }
    }
}
