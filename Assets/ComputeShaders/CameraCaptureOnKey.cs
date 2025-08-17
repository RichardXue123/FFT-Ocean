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
    [Range(1, 100)] public int jpgQuality = 95;

    // --- internal ---
    private Camera cam;

    // SRP 一帧延迟抓取所需的临时状态
    private RenderTexture _pendingRT;
    private bool _pendingTransparent;
    private CameraClearFlags _oldFlags;
    private Color _oldBG;
    private RenderTexture _oldTarget;

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
        if (Input.GetKeyDown(hotkey))
        {
            if (includeUI)
            {
                // 最终屏幕（含 Overlay UI）
                StartCoroutine(CaptureGameViewCoroutine());
            }
            else
            {
                // 只抓本相机视角
                if (GraphicsSettings.currentRenderPipeline == null)
                    CaptureBuiltinImmediate();
                else
                    CaptureSRPNextFrame();
            }
        }
    }

    // ---------- Built-in 渲染管线：立即渲染/读回 ----------
    void CaptureBuiltinImmediate()
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

            SaveRTToFile(rt);

            LogSavedPath();
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
    void CaptureSRPNextFrame()
    {
        if (_pendingRT != null) return; // 避免重复占用

        _pendingRT = RenderTexture.GetTemporary(MakeRTDesc());
        _pendingTransparent = transparentBackground;

        _oldFlags = cam.clearFlags;
        _oldBG = cam.backgroundColor;
        _oldTarget = cam.targetTexture;

        if (_pendingTransparent)
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
            SaveRTToFile(_pendingRT);
            LogSavedPath();
        }
        finally
        {
            CleanupPending();
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
    System.Collections.IEnumerator CaptureGameViewCoroutine()
    {
        // 等到当帧所有相机与 UI 都画完
        yield return new WaitForEndOfFrame();

        // 直接获取屏幕纹理（分辨率为当前 GameView/屏幕分辨率）
        var tex = ScreenCapture.CaptureScreenshotAsTexture();

        // 如需强制输出为设定分辨率，可在这里做一次缩放（略）

        var bytes = Encode(tex);
        File.WriteAllBytes(ComposePath(), bytes);
        LogSavedPath();
        Object.Destroy(tex);
    }

    // ---------- 读取与保存 ----------
    void SaveRTToFile(RenderTexture rt)
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
        File.WriteAllBytes(ComposePath(), bytes);

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

    string ComposePath()
    {
        string dir = "C:\\Users\\27487\\Pictures\\paper\\results\\Captured";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string ts = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string ext = fileType == FileType.JPG ? "jpg" : (fileType == FileType.EXR ? "exr" : "png");
        return Path.Combine(dir, $"cam_{name}_{ts}.{ext}");
    }

    void LogSavedPath()
    {
#if UNITY_EDITOR
        Debug.Log($"Saved capture to: {ComposePath()}");
#endif
    }
}
