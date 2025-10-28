// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using GaussianSplatting.Runtime;

// Minimal SAM client that captures the camera, sends to sidecar /sam, and applies the mask.
// No k-means, no internal depth gating, no seed occlusion or per-pixel depth.
public class SAMControllerLite : MonoBehaviour
{
    [Header("References")]
    public GaussianSplatRenderer gs;
    public ComputeShader maskSelectCS;
    public Camera sourceCamera;

    [Header("Capture")]
    [Range(64, 4096)] public int captureSize = 1024;

    [Header("Mask Apply")]
    [Range(0f,1f)] public float maskThreshold = 0.5f;
    public enum ApplyMode { Replace=0, Add=1, Subtract=2 }
    public ApplyMode applyMode = ApplyMode.Replace;
    [Tooltip("Selection Size: negative=shrink (erode), positive=grow (dilate). Units in pixels.")]
    [Range(-16, 16)] public int selectionSize = 0;
    [Tooltip("When disabled, running SAM will not modify splat selection; it only produces the mask (and can open it).")]
    public bool applySelection = false;

    [Header("Prompts (points)")]
    public List<Vector2> positivePoints = new List<Vector2>(); // normalized [0,1] in BL coordinates

    [Header("Local Python (required)")]
    public bool useLocalPython = true; // Lite controller runs SAM via local Python only
    [Tooltip("Absolute path to Python executable (venv)")]
    public string pythonExe = ""; // e.g., ...\sidecar\.venv\Scripts\python.exe
    [Tooltip("Absolute path to cli_sam.py")]
    public string pythonCli = ""; // e.g., ...\sidecar\cli_sam.py
    [Tooltip("Absolute path to SAM vit_h checkpoint .pth (optional; auto-detected if empty)")]
    public string samCheckpoint = "";
    [Tooltip("Model type for SAM (vit_h/vit_l/vit_b or mobile variant)")]
    public string samModel = "vit_h";
    [Tooltip("Force CPU for Python CLI (ignore CUDA)")]
    public bool samForceCpu = false;
    [Tooltip("Open the generated mask PNG after a local Python run")]
    public bool openMaskAfterRun = false;
    [Tooltip("Keep a Python SAM worker alive for fast runs")]
    public bool usePersistentWorker = true;
    [Tooltip("Extra Unity Console logs for worker and SAM operations")]
    public bool verboseLogs = false;
    System.Diagnostics.Process workerProc;
    System.IO.StreamWriter workerStdin;
    System.IO.StreamReader workerStdout;
    System.IO.StreamReader workerStderr;
    volatile bool workerReady = false;
    volatile string lastWorkerStdout = string.Empty;
    volatile string lastWorkerStderr = string.Empty;

    static readonly FieldInfo s_ViewBufferField = typeof(GaussianSplatRenderer).GetField("m_GpuView", BindingFlags.NonPublic | BindingFlags.Instance);

    int kApply = -1;
    bool kApplyIsLite = false;
    GraphicsBuffer seedWorldDummy;
    GraphicsBuffer depthRangeBuffer;
    GraphicsBuffer depthHistogramBuffer;
    ComputeBuffer perPixelDepthDummy;

    void Awake()
    {
        if (!sourceCamera) sourceCamera = Camera.main;
        if (!gs)
        {
#if UNITY_2023_1_OR_NEWER
            gs = FindFirstObjectByType<GaussianSplatRenderer>();
#else
            gs = FindObjectOfType<GaussianSplatRenderer>();
#endif
        }
        if (maskSelectCS)
        {
            kApply = maskSelectCS.FindKernel("ApplyMaskSelectionLite");
            kApplyIsLite = (kApply >= 0);
            if (!kApplyIsLite)
                kApply = maskSelectCS.FindKernel("ApplyMaskSelection");
        }
        if (useLocalPython && usePersistentWorker)
            StartPythonWorker();
    }

    void Update()
    {
        if (!Application.isPlaying) return;

        // Left click adds a positive point (normalized BL coordinates relative to sourceCamera viewport)
        if (Input.GetMouseButtonDown(0))
        {
            Vector2 mp = Input.mousePosition; // screen space (origin bottom-left)
            Rect r = sourceCamera ? sourceCamera.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            float vx = (mp.x - r.x) / Mathf.Max(1f, r.width);
            float vy = (mp.y - r.y) / Mathf.Max(1f, r.height);
            var p = new Vector2(Mathf.Clamp01(vx), Mathf.Clamp01(vy));
            positivePoints.Add(p);
            Debug.Log($"[SAM Lite] Added POS point {p}");
        }

        // Enter runs SAM
        if (Input.GetKeyDown(KeyCode.Return))
        {
            RunSAMOnce();
        }
    }

    void OnGUI()
    {
        // simple overlay to visualize positive points
        if (!Application.isPlaying) return;
        foreach (var p in positivePoints)
        {
            Rect r = sourceCamera ? sourceCamera.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            float sx = r.x + p.x * r.width;
            float sy_screenBL = r.y + p.y * r.height;   // bottom-left screen coordinates
            float sy_gui = Screen.height - sy_screenBL;  // convert to GUI's top-left origin
            DrawCrosshair(new Vector2(sx, sy_gui), new Color(0.2f, 1f, 0.3f, 0.95f));
        }
    }

    [ContextMenu("Run SAM (Lite)")]
    public void RunSAMOnce()
    {
        if (isActiveAndEnabled) StartCoroutine(RunSAMCoroutine());
    }

    IEnumerator RunSAMCoroutine()
    {
        if (!gs || !sourceCamera || !maskSelectCS || kApply < 0) yield break;
        if (!gs.HasValidAsset || !gs.HasValidRenderSetup) yield break;

        // Capture downsampled view while keeping camera aspect
        float camAspect = (sourceCamera != null && sourceCamera.pixelHeight > 0)
            ? (float)sourceCamera.pixelWidth / sourceCamera.pixelHeight
            : 1f;
        int W, H;
        if (camAspect >= 1f) { W = captureSize; H = Mathf.Max(1, Mathf.RoundToInt(captureSize / camAspect)); }
        else { H = captureSize; W = Mathf.Max(1, Mathf.RoundToInt(captureSize * camAspect)); }

        byte[] png = CaptureCameraToPNG(sourceCamera, W, H);
        if (png == null || png.Length == 0) yield break;

        var reqObj = new RequestPayload
        {
            image_b64  = Convert.ToBase64String(png),
            width      = W,
            height     = H,
            points_pos = ConvertPoints(positivePoints),
            points_neg = null,
            boxes      = null,
            use_roi    = false,
            roi        = default,
            roi_pad_x  = 0f,
            roi_pad_y  = 0f
        };

        string json = JsonUtility.ToJson(reqObj);

        if (useLocalPython)
        {
            // Save PNG to temp, call python cli, read mask png
            string tempDir = Application.temporaryCachePath;
            string inPath = System.IO.Path.Combine(tempDir, "sam_in.png");
            string outPath = System.IO.Path.Combine(tempDir, "sam_out.png");
            System.IO.File.WriteAllBytes(inPath, png);
            // Build normalized TL points string from positivePoints (convert BL->TL here)
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < positivePoints.Count; i++)
            {
                var p = positivePoints[i];
                float x = Mathf.Clamp01(p.x);
                float y_tl = Mathf.Clamp01(1f - Mathf.Clamp01(p.y));
                if (i>0) sb.Append(',');
                sb.Append(x.ToString("R")).Append(',').Append(y_tl.ToString("R"));
            }
            bool usedWorker = false;
            if (usePersistentWorker && workerProc != null && !workerProc.HasExited && workerStdin != null && workerStdout != null)
            {
                string imageJson = inPath.Replace("\\", "/");
                string outJson = outPath.Replace("\\", "/");
                string pointsStr = sb.ToString();
                string payload = "{\"image\":\"" + imageJson + "\",\"points\":\"" + pointsStr + "\",\"out\":\"" + outJson + "\"}";
                try
                {
                    var swCall = System.Diagnostics.Stopwatch.StartNew();
                    workerStdin.WriteLine(payload);
                    workerStdin.Flush();
                    string line = workerStdout.ReadLine();
                    swCall.Stop();
                    if (verboseLogs)
                        Debug.Log($"[SAM Lite] Worker call took {swCall.ElapsedMilliseconds} ms, response: {line}");
                    if (!string.IsNullOrEmpty(line) && !line.Contains("error"))
                        usedWorker = true;
                    else
                        Debug.LogWarning($"[SAM Lite] Worker response: {line}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SAM Lite] Worker IO failed: {ex.Message}");
                }
            }
            if (!usedWorker)
            {
                // One-shot fallback
                string exePath = (pythonExe ?? string.Empty).Trim().Trim('\"');
                string cliPath = (pythonCli ?? string.Empty).Trim().Trim('\"');
                if (cliPath.StartsWith("Python SAM CLI ")) cliPath = cliPath.Substring("Python SAM CLI ".Length).Trim();
                string workDir = string.Empty;
                try { workDir = System.IO.Path.GetDirectoryName(cliPath); } catch { workDir = string.Empty; }
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"\"{cliPath}\" --image \"{inPath}\" --points \"{sb}\" --out \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workDir
                };
                if (!string.IsNullOrEmpty(samModel)) psi.EnvironmentVariables["SAM_MODEL"] = samModel;
                string ckpt = samCheckpoint;
                if (string.IsNullOrEmpty(ckpt))
                {
                    try
                    {
                        string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                        string candidate = System.IO.Path.Combine(projectRoot, "sidecar", "weight", "sam_vit_h_4b8939.pth");
                        if (System.IO.File.Exists(candidate)) ckpt = candidate;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(ckpt)) psi.EnvironmentVariables["SAM_CHECKPOINT"] = ckpt;
                if (samForceCpu)
                {
                    psi.EnvironmentVariables["SAM_DEVICE"] = "cpu";
                    psi.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "";
                }
                var proc = System.Diagnostics.Process.Start(psi);
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    Debug.LogError($"[SAM Lite] Python CLI failed: {stderr}");
                    yield break;
                }
            }
            byte[] maskBytes = System.IO.File.Exists(outPath) ? System.IO.File.ReadAllBytes(outPath) : null;
            if (maskBytes == null || maskBytes.Length == 0)
            {
                Debug.LogError("[SAM Lite] Python CLI produced no mask.");
                yield break;
            }
            var maskTex = new Texture2D(W, H, TextureFormat.R8, false, true);
            maskTex.LoadImage(maskBytes, markNonReadable: false);
            maskTex.filterMode = FilterMode.Point;
            maskTex.wrapMode = TextureWrapMode.Clamp;
            maskTex.Apply(false, false);
            // Optionally open the mask file for quick inspection
            // Use forward slashes to keep Application.OpenURL happy on Windows
            // (no-op if the platform blocks it)
            // This happens only for the local Python path since we have an actual PNG file.
            if (openMaskAfterRun)
            {
                try { Application.OpenURL(outPath.Replace("\\", "/")); } catch {}
            }
            if (applySelection)
                yield return ApplyMaskToSelection(maskTex);
            yield break;
        }

        // HTTP path removed in Lite controller for simplicity and performance predictability
    }

    IEnumerator ApplyMaskToSelection(Texture2D maskTex)
    {
        if (!gs || !maskSelectCS) yield break;
        int splatCount = gs.splatCount;
        if (splatCount <= 0) yield break;

        var sel = gs.GpuEditSelected;
        if (applyMode == ApplyMode.Replace)
        {
            gs.EditDeselectAll();
            sel = gs.GpuEditSelected;
        }
        if (sel == null)
        {
            Debug.LogWarning("[SAM Lite] Selection buffer unavailable; selection update skipped.");
            yield break;
        }

        var viewBuffer = s_ViewBufferField?.GetValue(gs) as GraphicsBuffer;
        if (viewBuffer == null)
        {
            Debug.LogWarning("[SAM Lite] Unable to access splat view buffer; selection update skipped.");
            yield break;
        }

        // Configure and dispatch ApplyMaskSelection
        maskSelectCS.SetBuffer(kApply, "_SplatViewData", viewBuffer);
        maskSelectCS.SetTexture(kApply, "_MaskTex", maskTex);
        maskSelectCS.SetInt("_SplatCount", splatCount);
        maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
        maskSelectCS.SetInt("_MaskPixelCount", maskTex.width * maskTex.height);
        int renderW = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
        int renderH = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
        maskSelectCS.SetFloats("_RenderViewportSize", renderW, renderH);
        maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
        maskSelectCS.SetInt("_Mode", (int)applyMode);
        maskSelectCS.SetInt("_CollectHistogram", 0);
        maskSelectCS.SetInt("_UseDepthGate", 0);
        maskSelectCS.SetInt("_UsePerPixelDepth", 0);
        maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 0);
        maskSelectCS.SetInt("_SeedCount", 0);
        maskSelectCS.SetFloat("_DepthOcclusionBias", 0f);
        maskSelectCS.SetMatrix("_ObjectToWorld", gs.transform.localToWorldMatrix);
        maskSelectCS.SetFloat("_SeedCullEps", 0f);

        // Morphology
        int morphMode = 0; int morphRadius = 0;
        if (selectionSize > 0) { morphMode = 1; morphRadius = selectionSize; }
        else if (selectionSize < 0) { morphMode = 2; morphRadius = -selectionSize; }
        maskSelectCS.SetInt("_MorphologyMode", morphMode);
        maskSelectCS.SetInt("_MorphRadiusPx", Mathf.Clamp(morphRadius, 0, 16));

        // Disable box gating in Lite point mode
        maskSelectCS.SetInt("_UseBoxGate", 0);
        maskSelectCS.SetInt("_UseBoxGateBL", 0);
        maskSelectCS.SetFloats("_GateRectTLBR", 0f, 0f, 1f, 1f);
        maskSelectCS.SetFloats("_GateRectBL", 0f, 0f, 1f, 1f);

        // ROI disabled in Lite (sidecar already returns full-frame mask)
        maskSelectCS.SetInt("_UseROI", 0);
        maskSelectCS.SetFloats("_ROIMinMax", 0f, 0f, 1f, 1f);

        // Lite kernel avoids optional buffers; nothing extra to bind

        maskSelectCS.SetBuffer(kApply, "_SelectedBits", sel);
        int groups = Mathf.Max(1, Mathf.CeilToInt(splatCount / 128f));
        maskSelectCS.Dispatch(kApply, groups, 1, 1);
        gs.UpdateEditCountsAndBounds();
        yield return null;
    }

    // Utilities
    void DrawCrosshair(Vector2 screenPos, Color col)
    {
        var prev = GUI.color;
        GUI.color = col;
        float s = 8f;
        DrawLine(new Vector2(screenPos.x - s, screenPos.y), new Vector2(screenPos.x + s, screenPos.y), 2f);
        DrawLine(new Vector2(screenPos.x, screenPos.y - s), new Vector2(screenPos.x, screenPos.y + s), 2f);
        GUI.color = prev;
    }

    void DrawLine(Vector2 a, Vector2 b, float width)
    {
        var saved = GUI.matrix;
        var delta = b - a;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        float len = delta.magnitude;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
        GUI.matrix = saved;
    }

    byte[] CaptureCameraToPNG(Camera cam, int width, int height)
    {
        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prev;

        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false, true);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        tex.Apply(false, false);
        var png = tex.EncodeToPNG();
        Destroy(tex);
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        return png;
    }

    void EnsureSeedWorldDummy()
    {
        if (seedWorldDummy == null)
        {
            seedWorldDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 4);
        }
    }

    // Removed depth/occlusion helpers in Lite path

    void StartPythonWorker()
    {
        try
        {
            string exePath = (pythonExe ?? string.Empty).Trim().Trim('"');
            string cliPath = (pythonCli ?? string.Empty).Trim().Trim('"');
            string workDir = System.IO.Path.GetDirectoryName(cliPath);
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(cliPath) || !System.IO.File.Exists(exePath) || !System.IO.File.Exists(cliPath))
            {
                Debug.LogWarning("[SAM Lite] Worker not started: set valid Python executable and cli_sam.py paths.");
                return;
            }
            if (verboseLogs)
                Debug.Log($"[SAM Lite] Starting worker: python=\"{exePath}\", cli=\"{cliPath}\"");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"\"{cliPath}\" --loop",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };
            if (!string.IsNullOrEmpty(samModel)) psi.EnvironmentVariables["SAM_MODEL"] = samModel;
            string ckpt = samCheckpoint;
            if (string.IsNullOrEmpty(ckpt))
            {
                string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string candidate = System.IO.Path.Combine(projectRoot, "sidecar", "weight", "sam_vit_h_4b8939.pth");
                if (System.IO.File.Exists(candidate)) ckpt = candidate;
            }
            if (!string.IsNullOrEmpty(ckpt)) psi.EnvironmentVariables["SAM_CHECKPOINT"] = ckpt;
            if (samForceCpu)
            {
                psi.EnvironmentVariables["SAM_DEVICE"] = "cpu";
                psi.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "";
            }
            if (verboseLogs)
            {
                var sbEnv = new System.Text.StringBuilder();
                sbEnv.Append("SAM_MODEL=").Append(psi.EnvironmentVariables["SAM_MODEL"]).Append(", ");
                sbEnv.Append("SAM_CHECKPOINT=").Append(psi.EnvironmentVariables["SAM_CHECKPOINT"]).Append(", ");
                sbEnv.Append("SAM_DEVICE=").Append(psi.EnvironmentVariables["SAM_DEVICE"]).Append(", ");
                sbEnv.Append("CUDA_VISIBLE_DEVICES=").Append(psi.EnvironmentVariables["CUDA_VISIBLE_DEVICES"]);
                Debug.Log("[SAM Lite] Worker env: " + sbEnv.ToString());
            }
            workerProc = System.Diagnostics.Process.Start(psi);
            workerStdin = workerProc.StandardInput;
            workerStdout = workerProc.StandardOutput;
            workerStderr = workerProc.StandardError;

            // Async read stderr to surface Python errors
            System.Threading.Thread stderrThread = new System.Threading.Thread(() =>
            {
                try
                {
                    while (workerStderr != null && !workerStderr.EndOfStream)
                    {
                        string line = workerStderr.ReadLine();
                        lastWorkerStderr = line;
                        if (verboseLogs && !string.IsNullOrEmpty(line))
                            Debug.LogWarning("[SAM Lite][py-stderr] " + line);
                    }
                }
                catch { }
            });
            stderrThread.IsBackground = true;
            stderrThread.Start();

            // Warm up: send a tiny no-op request so the worker loads the model now
            // This shifts initial SAM load to Awake instead of first run.
            try
            {
                string tempDir = Application.temporaryCachePath;
                string inPath = System.IO.Path.Combine(tempDir, "sam_warmup_in.png");
                string outPath = System.IO.Path.Combine(tempDir, "sam_warmup_out.png");
                // Create a tiny 8x8 black PNG
                var tex = new Texture2D(8, 8, TextureFormat.RGB24, false, true);
                var cols = new Color32[8*8];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(0,0,0,255);
                tex.SetPixels32(cols);
                tex.Apply(false, false);
                System.IO.File.WriteAllBytes(inPath, tex.EncodeToPNG());
                Destroy(tex);
                string imageJson = inPath.Replace("\\", "/");
                string outJson = outPath.Replace("\\", "/");
                string payload = "{\"image\":\"" + imageJson + "\",\"points\":\"\",\"out\":\"" + outJson + "\"}";
                workerStdin.WriteLine(payload);
                workerStdin.Flush();
                // Read a single line response (with a timeout safeguard using async BeginRead is overkill; try non-blocking read with a short wait)
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                string line = null;
                // Allow up to 120s for initial model load on first warm-up
                while (sw.ElapsedMilliseconds < 120000)
                {
                    if (workerProc.HasExited) break;
                    if (!workerStdout.EndOfStream)
                    {
                        line = workerStdout.ReadLine();
                        lastWorkerStdout = line;
                        break;
                    }
                    System.Threading.Thread.Sleep(50);
                }
                if (string.IsNullOrEmpty(line))
                {
                    Debug.LogWarning("[SAM Lite] Worker warm-up did not return a response in time (model may still be loading).");
                    if (verboseLogs && !string.IsNullOrEmpty(lastWorkerStderr))
                        Debug.LogWarning("[SAM Lite] Last py-stderr: " + lastWorkerStderr);
                }
                else if (line.Contains("error"))
                    Debug.LogWarning($"[SAM Lite] Worker warm-up error: {line}");
                else
                {
                    Debug.Log("[SAM Lite] Worker ready (model preloaded).");
                    workerReady = true;
                }
                // Cleanup warmup files quietly
                try { if (System.IO.File.Exists(inPath)) System.IO.File.Delete(inPath); } catch { }
                try { if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath); } catch { }
            }
            catch (Exception wex)
            {
                Debug.LogWarning($"[SAM Lite] Worker warm-up failed: {wex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SAM Lite] Failed to start worker: {ex.Message}");
            workerProc = null;
            workerStdin = null;
            workerStdout = null;
        }
    }

    void OnDestroy()
    {
        seedWorldDummy?.Dispose();
        seedWorldDummy = null;
        if (workerProc != null)
        {
            try { if (!workerProc.HasExited) workerProc.Kill(); } catch { }
            workerProc.Dispose();
            workerProc = null;
        }
        workerStdin = null;
        workerStdout = null;
        workerStderr = null;
    }

    [ContextMenu("Log Worker Status (Lite)")]
    void LogWorkerStatus()
    {
        Debug.Log($"[SAM Lite] workerProc={(workerProc!=null ? (workerProc.HasExited?"exited":"running") : "null")}, ready={workerReady}, last stdout={lastWorkerStdout}, last stderr={lastWorkerStderr}");
    }

    [Serializable] class RequestPayload
    {
        public string image_b64;
        public int    width;
        public int    height;
        public Point[] points_pos;
        public Point[] points_neg;
        public Box[]   boxes;
        public bool    use_roi;
        public Box     roi;
        public float   roi_pad_x;
        public float   roi_pad_y;
    }
    [Serializable] class ResponsePayload
    {
        public string mask_b64;
        public int    width;
        public int    height;
    }
    [Serializable] struct Point { public float x; public float y; }
    [Serializable] struct Box   { public float x0; public float y0; public float x1; public float y1; }

    static Point[] ConvertPoints(List<Vector2> list)
    {
        if (list == null || list.Count == 0) return null;
        var arr = new Point[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            float nx = Mathf.Clamp01(p.x);
            float ny_bl = Mathf.Clamp01(p.y);
            // Sidecar expects top-left normalized coordinates; flip Y from BL to TL
            float ny_tl = Mathf.Clamp01(1f - ny_bl);
            arr[i] = new Point { x = nx, y = ny_tl };
        }
        return arr;
    }
}
