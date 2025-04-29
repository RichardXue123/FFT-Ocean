using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteInEditMode]
public class HighResPlane : MonoBehaviour
{
    [Tooltip("网格细分像素数 X 方向")] public int subdivisionsX = 256;
    [Tooltip("网格细分像素数 Z 方向")] public int subdivisionsZ = 256;
    [Tooltip("平面世界大小 X")] public float sizeX = 10f;
    [Tooltip("平面世界大小 Z")] public float sizeZ = 10f;
    [Tooltip("生成网格所用材质")] public Material planeMaterial;

    // 在 Inspector 按钮调用此方法生成中心平面
    public void GeneratePlane()
    {
        // 查找或创建名为 "center" 的子对象
        Transform centerTf = transform.Find("center");
        GameObject centerObj;
        if (centerTf == null)
        {
            centerObj = new GameObject("center");
            centerObj.transform.SetParent(transform);
        }
        else
        {
            centerObj = centerTf.gameObject;
        }

        // 确保 MeshFilter 和 MeshRenderer 存在
        MeshFilter mf = centerObj.GetComponent<MeshFilter>();
        if (mf == null) mf = centerObj.AddComponent<MeshFilter>();
        MeshRenderer mr = centerObj.GetComponent<MeshRenderer>();
        if (mr == null) mr = centerObj.AddComponent<MeshRenderer>();

        // 指定材质
        if (planeMaterial != null)
            mr.sharedMaterial = planeMaterial;

        // 创建 Mesh
        Mesh mesh = new Mesh { name = "HighResPlane", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        int vertsX = subdivisionsX + 1;
        int vertsZ = subdivisionsZ + 1;
        Vector3[] vertices = new Vector3[vertsX * vertsZ];
        Vector2[] uvs = new Vector2[vertices.Length];
        int[] tris = new int[subdivisionsX * subdivisionsZ * 6];

        // 顶点 & UV
        for (int z = 0; z < vertsZ; z++)
            for (int x = 0; x < vertsX; x++)
            {
                int i = x + z * vertsX;
                float u = x / (float)subdivisionsX;
                float v = z / (float)subdivisionsZ;
                float px = u * sizeX - sizeX * 0.5f;
                float pz = v * sizeZ - sizeZ * 0.5f;
                vertices[i] = new Vector3(px, 0, pz);
                uvs[i] = new Vector2(u, v);
            }

        // 三角形索引
        int ti = 0;
        for (int z = 0; z < subdivisionsZ; z++)
            for (int x = 0; x < subdivisionsX; x++)
            {
                int i0 = x + z * vertsX;
                int i1 = i0 + 1;
                int i2 = i0 + vertsX;
                int i3 = i2 + 1;

                tris[ti++] = i0; tris[ti++] = i2; tris[ti++] = i1;
                tris[ti++] = i1; tris[ti++] = i2; tris[ti++] = i3;
            }

        // 赋值
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();

        mf.sharedMesh = mesh;

#if UNITY_EDITOR
        EditorUtility.SetDirty(mf);
        SceneView.RepaintAll();
#endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(HighResPlane))]
    public class HighResPlaneEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            HighResPlane script = (HighResPlane)target;
            if (GUILayout.Button("生成 256×256 Plane"))
            {
                script.GeneratePlane();
            }
        }
    }
#endif
}
