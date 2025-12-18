using Assets.Scripts;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using System.Text;
using System.Globalization;


public class OceanGeometry : MonoBehaviour
{
    [SerializeField] 
    WavesGenerator wavesGenerator;
    [SerializeField]
    Transform viewer;
    [SerializeField]
    Material oceanMaterial;
    [SerializeField]
    bool updateMaterialProperties;
    /*[SerializeField]
    bool showMaterialLods;*/

    [SerializeField]
    float oceanLength = 100;
    [SerializeField]
    int gridLength = 100;
    /*[SerializeField, Range(0, 1)]
    float UnityUnitsPerMeter = 0.1f; // 1U = 10m*/
    /*[SerializeField, Range(0, 8)]
    int clipLevels = 8;
    [SerializeField, Range(0, 100)]
    float skirtSize = 50;*/

    //List<Element> rings = new List<Element>();
    //List<Element> trims = new List<Element>();
    Element center;
    //Element skirt;
    //Quaternion[] trimRotations;
    //int previousVertexDensity;
    //float previousSkirtSize;

    [SerializeField] 
    public RenderTexture heightMap;
    [SerializeField] 
    public Vector2Int textureSize = new Vector2Int(256, 256);
    //Material[] materials;

    private void Start()
    {
        if (viewer == null)
            viewer = Camera.main.transform;

        oceanMaterial.SetTexture("_Displacement_c0", wavesGenerator.cascade0.Displacement);
        oceanMaterial.SetTexture("_Derivatives_c0", wavesGenerator.cascade0.Derivatives);
        oceanMaterial.SetTexture("_Turbulence_c0", wavesGenerator.cascade0.Turbulence);

        InstantiateMeshes();
        // 初始化 heightMap
        heightMap = new RenderTexture(textureSize.x, textureSize.y, 0, RenderTextureFormat.RFloat);
        heightMap.enableRandomWrite = true;
        //采样模式改为双/三线性过滤
        heightMap.filterMode = FilterMode.Trilinear;  // 或者 Bilinear
        heightMap.wrapMode = TextureWrapMode.Clamp;
        heightMap.Create();

        //Debug.Log("Material instance ID: " + oceanMaterial.GetInstanceID());
    }

    private void Update()
    {
        //DebugDispHeight();

        // ---- 打印FFT区域RMS（采样Displacement的G通道） ----
        /*if (wavesGenerator != null && wavesGenerator.cascade0 != null && wavesGenerator.cascade0.Displacement != null)
        {
            var dispRT = wavesGenerator.cascade0.Displacement;
            Texture2D tex = new Texture2D(dispRT.width, dispRT.height, TextureFormat.RGBAFloat, false);
            RenderTexture.active = dispRT;
            tex.ReadPixels(new Rect(0, 0, dispRT.width, dispRT.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            PhysicsVerify.CalcRMS(tex,true);
            Object.Destroy(tex);
        }*/
    }

    /// <summary>
    /// 对 FFT Displacement（cascade0）做高度直方图，并导出 CSV
    /// </summary>
    [ContextMenu("Export FFT Height Histogram")]
    public void ExportFFTHistogram()
    {
        EnqueueDispHeightHistogram(64);   // 64 个 bin，你可以改
    }

    /// <summary>
    /// 异步读回 FFT Displacement 的 G 通道，统计直方图并导出
    /// </summary>
    void EnqueueDispHeightHistogram(int binCount = 64)
    {
        if (wavesGenerator == null || wavesGenerator.cascade0 == null || wavesGenerator.cascade0.Displacement == null)
        {
            Debug.LogWarning("[FFT Hist] wavesGenerator / cascade0 / Displacement 为空");
            return;
        }

        var rt = wavesGenerator.cascade0.Displacement;

        AsyncGPUReadback.Request(rt, 0, request =>
        {
            if (request.hasError)
            {
                Debug.LogError("[FFT Hist] AsyncGPUReadback failed");
                return;
            }

            var data = request.GetData<Color>();
            int nPixels = data.Length;

            if (nPixels == 0)
            {
                Debug.LogWarning("[FFT Hist] no pixels");
                return;
            }

            // ---- 1) 基本统计量 ----
            double sum = 0.0;
            float minH = float.PositiveInfinity;
            float maxH = float.NegativeInfinity;

            for (int i = 0; i < nPixels; i++)
            {
                float h = data[i].g; // 高度在 G 通道
                sum += h;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }

            float mean = (float)(sum / nPixels);

            if (Mathf.Abs(maxH - minH) < 1e-7f)
            {
                Debug.Log($"[FFT Hist] 所有像素高度几乎相同: h≈{mean:F6}");
                return;
            }

            // ---- 2) 构建直方图 ----
            int bins = Mathf.Max(1, binCount);
            int[] counts = new int[bins];

            float range = maxH - minH;
            float invBinWidth = bins / range;   // 1 / binWidth

            for (int i = 0; i < nPixels; i++)
            {
                float h = data[i].g;
                int bin = (int)((h - minH) * invBinWidth);
                if (bin < 0) bin = 0;
                if (bin >= bins) bin = bins - 1;
                counts[bin]++;
            }

            // ---- 3) 打 Log（可视检查） ----
            var sb = new StringBuilder();
            sb.AppendLine("[FFT HeightHist]");
            sb.AppendLine($"Pixels = {nPixels}");
            sb.AppendLine($"Min = {minH:F6}, Max = {maxH:F6}, Mean = {mean:F6}");
            sb.AppendLine($"Bins = {bins}");
            sb.AppendLine("binIndex, hMin, hMax, count, probability");

            for (int b = 0; b < bins; b++)
            {
                float h0 = minH + (range * b) / bins;
                float h1 = minH + (range * (b + 1)) / bins;
                int count = counts[b];
                float p = (float)count / nPixels;

                sb.AppendLine($"{b}, {h0:F6}, {h1:F6}, {count}, {p:F6}");
            }

            Debug.Log(sb.ToString());

            // ---- 4) 导出 CSV ----
            ExportFFTHistogramCsv(minH, maxH, bins, counts, nPixels);
        });
    }

    /// <summary>
    /// 导出 FFT 高度直方图到 CSV
    /// 文件名格式: FFT_oceanLen_gridLen_bins_yyyyMMdd_HHmmss.csv
    /// </summary>
    void ExportFFTHistogramCsv(
        float minH,
        float maxH,
        int bins,
        int[] counts,
        int totalPixels)
    {
        float range = maxH - minH;

        string date = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // 这里用了 oceanLength / gridLength，让文件名带一点场景信息
        string fileName = $"FFT_{oceanLength:F1}_{gridLength}_{bins}_{date}.csv";
        
        // 放到 Assets/Exports 下面：
        string dir = Path.Combine(Application.dataPath, "Exports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);

        using (var sw = new StreamWriter(path, false, Encoding.UTF8))
        {
            sw.WriteLine("FFT Height Histogram Export");
            sw.WriteLine($"OceanLength,{oceanLength.ToString("F3", CultureInfo.InvariantCulture)}");
            sw.WriteLine($"GridLength,{gridLength}");
            sw.WriteLine($"Bins,{bins}");
            sw.WriteLine($"Min,{minH.ToString("F6", CultureInfo.InvariantCulture)}");
            sw.WriteLine($"Max,{maxH.ToString("F6", CultureInfo.InvariantCulture)}");
            sw.WriteLine($"TotalPixels,{totalPixels}");
            sw.WriteLine();

            sw.WriteLine("binIndex,hMin,hMax,count,probability");

            for (int b = 0; b < bins; b++)
            {
                float h0 = minH + (range * b) / bins;
                float h1 = minH + (range * (b + 1)) / bins;
                int count = counts[b];
                float p = (float)count / totalPixels;

                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1:F6},{2:F6},{3},{4:F8}",
                    b, h0, h1, count, p));
            }

            sw.WriteLine();
            sw.WriteLine("End");
        }

        Debug.Log($"[FFT Hist] CSV exported: {path}");
    }


    public void DebugDispHeight() {
        var rt = wavesGenerator.cascade0.Displacement;

        AsyncGPUReadback.Request(rt, 0, req =>
        {
            if (req.hasError) { Debug.LogError("[Disp] Readback failed"); return; }

            // 每个像素是一个 Color( r,g,b,a )，你的高度在 g
            var pixels = req.GetData<Color>();
            int n = pixels.Length;

            double sum = 0.0;
            float minH = float.PositiveInfinity;
            float maxH = float.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                float h = pixels[i].g;    // 高度 = G 通道
                sum += h;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }

            float mean = (float)(sum / n);

            Debug.Log($"FFT [Disp] mean={mean:F6}  min={minH:F6}  max={maxH:F6}");
        });
    }

    void InstantiateMeshes()
    {
        float meshScale = oceanLength / gridLength;
        Mesh centerMesh = CreatePlaneMesh(gridLength, gridLength, meshScale, Seams.None);
        center = InstantiateElement("Center", centerMesh, oceanMaterial);
    }

    Element InstantiateElement(string name, Mesh mesh, Material mat)
    {
        GameObject go = new GameObject();
        go.name = name;
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.zero;
        MeshFilter meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.mesh = mesh;
        MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = true;
        meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        meshRenderer.material = mat;
        meshRenderer.allowOcclusionWhenDynamic = false;
        return new Element(go.transform, meshRenderer);
    }

    Mesh CreatePlaneMesh(int width, int height, float lengthScale, Seams seams = Seams.None, int trianglesShift = 0)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Clipmap plane";
        if ((width + 1) * (height + 1) >= 256 * 256)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        Vector3[] vertices = new Vector3[(width + 1) * (height + 1)];
        int[] triangles = new int[width * height * 2 * 3];
        Vector3[] normals = new Vector3[(width + 1) * (height + 1)];

        for (int i = 0; i < height + 1; i++)
        {
            for (int j = 0; j < width + 1; j++)
            {
                int x = j;
                int z = i;

                if ((i == 0 && seams.HasFlag(Seams.Bottom)) || (i == height && seams.HasFlag(Seams.Top)))
                    x = x / 2 * 2;
                if ((j == 0 && seams.HasFlag(Seams.Left)) || (j == width && seams.HasFlag(Seams.Right)))
                    z = z / 2 * 2;

                //vertices[j + i * (width + 1)] = new Vector3(x, 0, z) * lengthScale;
                float xOffset = width / 2.0f;
                float zOffset = height / 2.0f;
                vertices[j + i * (width + 1)] = new Vector3(x - xOffset, 0, z - zOffset) * lengthScale;
                normals[j + i * (width + 1)] = Vector3.up;
            }
        }

        int tris = 0;
        for (int i = 0; i < height; i++)
        {
            for (int j = 0; j < width; j++)
            {
                int k = j + i * (width + 1);
                if ((i + j + trianglesShift) % 2 == 0)
                {
                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + width + 2;

                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 2;
                    triangles[tris++] = k + 1;
                }
                else
                {
                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + 1;

                    triangles[tris++] = k + 1;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + width + 2;
                }
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        return mesh;
    }

    class Element
    {
        public Transform Transform;
        public MeshRenderer MeshRenderer;

        public Element(Transform transform, MeshRenderer meshRenderer)
        {
            Transform = transform;
            MeshRenderer = meshRenderer;
        }
    }


    [System.Flags]
    enum Seams
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        All = Left | Right | Top | Bottom
    };
}


