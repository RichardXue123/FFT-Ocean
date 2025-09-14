using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class CameraCaptureOnKey : MonoBehaviour
{
    [Header("Trigger")]
    public KeyCode hotkey = KeyCode.P;

    [Header("Output")]
    public int width = 3840;
    public int height = 2160;
    [Range(1, 8)] public int msaa = 1;
    public bool transparentBackground = false; // 仅 PNG/EXR 有效；需相机背景色允许透明
    public bool includeUI = false;             // 勾选=抓取最终屏幕图像（含Overlay UI）；不勾选=只抓该相机视角
    public string subfolder = "Captures";

    public enum FileType { PNG, JPG, EXR }
    public FileType fileType = FileType.PNG;
    [Range(1, 100)] public int jpgQuality = 100;

    [Header("Burst / Interval")]
    [Min(1)] public int captureCount = 1;       // 总共拍几张
    [Min(0f)] public float captureInterval = 0; // 每张间隔秒数（0 = 连续拍）

    // --- internal ---
    private Camera cam;

    // SRP 一帧延迟抓取所需的临时状态
    private RenderTexture _pendingRT;
    private CameraClearFlags _oldFlags;
    private Color _oldBG;
    private RenderTexture _oldTarget;
    private string _pendingPath;

    private bool _isBurstRunning = false; // 防止重复触发

    void Awake()
    {
        cam = GetComponent<Camera>();
    }

    void OnEnable()
    {
        RenderPipelineManager.endCameraRendering += OnEndCameraRenderingSRP;
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= OnEndCameraRenderingSRP;
        CleanupPending();
    }

    void Update()
    {
        if (Input.GetKeyDown(hotkey) && !_isBurstRunning)
        {
            StartCoroutine(BurstCaptureCoroutine());
        }
    }

    // ---------- 连拍主流程 ----------
    System.Collections.IEnumerator BurstCaptureCoroutine()
    {
        _isBurstRunning = true;

        // 同一轮连拍使用同一个时间戳基准 + 递增序号，避免同名覆盖
        string tsBase = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        for (int i = 0; i < Mathf.Max(1, captureCount); i++)
        {
            string path = ComposePath(tsBase, i);

            if (includeUI)
            {
                // 抓取最终屏幕（含 Overlay UI）
                yield return CaptureGameViewCoroutine(path);  // 自带跨帧
            }
            else
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                {
                    // Built-in：立即渲染并保存，但随后至少跨一帧，避免同帧多张
                    CaptureBuiltinImmediate(path);
                    if (captureInterval <= 0f)
                        yield return null; // 下一张放到下一帧
                }
                else
                {
                    // SRP：下一帧在 endCameraRendering 回调里保存
                    CaptureSRPNextFrame(path);
                    while (_pendingRT != null) yield return null; // 等保存完毕
                }
            }

            // 间隔（最后一张不等）
            if (i < captureCount - 1 && captureInterval > 0f)
                yield return new WaitForSeconds(captureInterval);
        }

        _isBurstRunning = false;
    }

    // ---------- Built-in 渲染管线：立即渲染/读回 ----------
    void CaptureBuiltinImmediate(string path)
    {
        var desc = MakeRTDesc();
        var rt = RenderTexture.GetTemporary(desc);

        var prevActive = RenderTexture.active;
        var prevTarget = cam.targetTexture;

        var oldFlags = cam.clearFlags;
        var oldBG = cam.backgroundColor;

        try
        {
            if (transparentBackground)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                var c = cam.backgroundColor; c.a = 0f;
                cam.backgroundColor = c;
            }

            cam.targetTexture = rt;
            cam.Render(); // Built-in 可用

            SaveRTToFile(rt, path);
#if UNITY_EDITOR
            Debug.Log($"Saved capture to: {path}");
#endif
        }
        finally
        {
            cam.clearFlags = oldFlags;
            cam.backgroundColor = oldBG;
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ---------- SRP (URP/HDRP)：下一帧在 endCameraRendering 回调里读回 ----------
    void CaptureSRPNextFrame(string path)
    {
        if (_pendingRT != null) return; // 避免重复占用

        _pendingRT = RenderTexture.GetTemporary(MakeRTDesc());
        _pendingPath = path;

        _oldFlags = cam.clearFlags;
        _oldBG = cam.backgroundColor;
        _oldTarget = cam.targetTexture;

        if (transparentBackground)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            var c = cam.backgroundColor; c.a = 0f;
            cam.backgroundColor = c;
        }

        // 让本帧该相机渲染到我们的 RT
        cam.targetTexture = _pendingRT;
        // 注：在 SRP 中不要调用 cam.Render()；让管线自己在本帧渲染
    }

    void OnEndCameraRenderingSRP(ScriptableRenderContext ctx, Camera renderedCam)
    {
        if (renderedCam != cam || _pendingRT == null) return;

        try
        {
            SaveRTToFile(_pendingRT, _pendingPath);
#if UNITY_EDITOR
            Debug.Log($"Saved capture to: {_pendingPath}");
#endif
        }
        finally
        {
            CleanupPending();
            _pendingPath = null;
        }
    }

    void CleanupPending()
    {
        if (_pendingRT != null)
        {
            cam.targetTexture = _oldTarget;
            cam.clearFlags = _oldFlags;
            cam.backgroundColor = _oldBG;

            RenderTexture.ReleaseTemporary(_pendingRT);
            _pendingRT = null;
        }
    }

    // ---------- 抓屏（包含 Overlay UI） ----------
    System.Collections.IEnumerator CaptureGameViewCoroutine(string path)
    {
        // 等到当帧所有相机与 UI 都画完
        yield return new WaitForEndOfFrame();

        // 直接获取屏幕纹理（分辨率为当前 GameView/屏幕分辨率）
        var tex = ScreenCapture.CaptureScreenshotAsTexture();

        var bytes = Encode(tex);
        EnsureDirExists(path);
        File.WriteAllBytes(path, bytes);
#if UNITY_EDITOR
        Debug.Log($"Saved capture to: {path}");
#endif
        Object.Destroy(tex);
    }

    // ---------- 读取与保存 ----------
    void SaveRTToFile(RenderTexture rt, string path)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;

        // EXR 走半精度格式，其它走 RGBA32
        var texFormat = (fileType == FileType.EXR) ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

        var tex = new Texture2D(rt.width, rt.height, texFormat, false, linear);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply(false, false);

        var bytes = Encode(tex);
        EnsureDirExists(path);
        File.WriteAllBytes(path, bytes);

        Object.Destroy(tex);
        RenderTexture.active = prev;
    }

    byte[] Encode(Texture2D tex)
    {
        switch (fileType)
        {
            case FileType.JPG: return tex.EncodeToJPG(jpgQuality);
            case FileType.EXR: return tex.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
            default: return tex.EncodeToPNG();
        }
    }

    RenderTextureDescriptor MakeRTDesc()
    {
        var fmt = (fileType == FileType.EXR || cam.allowHDR) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        var desc = new RenderTextureDescriptor(width, height, fmt, 24)
        {
            msaaSamples = Mathf.Max(1, msaa),
            sRGB = (QualitySettings.activeColorSpace == ColorSpace.Linear)
        };
        return desc;
    }

    // 生成唯一文件名：同一轮连拍用同一时间戳 + 序号
    string ComposePath(string tsBase, int index)
    {
        string dir = "C:\\Users\\27487\\Pictures\\paper\\results\\Captured";
        string ext = fileType == FileType.JPG ? "jpg" : (fileType == FileType.EXR ? "exr" : "png");
        string suffix = (captureCount > 1) ? $"_{index:D3}" : "";
        string full = Path.Combine(dir, $"cam_{name}_{tsBase}{suffix}.{ext}");
        return full;
    }

    void EnsureDirExists(string fullPath)
    {
        string dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
