using UnityEngine;

public class CustomPlaneGenerator : MonoBehaviour
{
    public int width = 10;
    public int height = 10;
    public int subdivisionsX = 255;
    public int subdivisionsY = 255;

    void Start()
    {
        GeneratePlane();
    }

    void GeneratePlane()
    {
        Mesh mesh = new Mesh();
        GetComponent<MeshFilter>().mesh = mesh;

        // 创建顶点
        int vertexCount = (subdivisionsX + 1) * (subdivisionsY + 1);
        Vector3[] vertices = new Vector3[vertexCount];
        int[] triangles = new int[subdivisionsX * subdivisionsY * 6];

        for (int y = 0; y <= subdivisionsY; y++)
        {
            for (int x = 0; x <= subdivisionsX; x++)
            {
                int currentIndex = y * (subdivisionsX + 1) + x;
                vertices[currentIndex] = new Vector3(x * (width / subdivisionsX), 0, y * (height / subdivisionsY));
            }
        }

        // 创建三角面
        int triIndex = 0;
        for (int y = 0; y < subdivisionsY; y++)
        {
            for (int x = 0; x < subdivisionsX; x++)
            {
                int topLeft = y * (subdivisionsX + 1) + x;
                int topRight = topLeft + 1;
                int bottomLeft = (y + 1) * (subdivisionsX + 1) + x;
                int bottomRight = bottomLeft + 1;

                triangles[triIndex] = topLeft;
                triangles[triIndex + 1] = bottomLeft;
                triangles[triIndex + 2] = topRight;

                triangles[triIndex + 3] = topRight;
                triangles[triIndex + 4] = bottomLeft;
                triangles[triIndex + 5] = bottomRight;

                triIndex += 6;
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
    }
}