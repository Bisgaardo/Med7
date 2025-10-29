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
    [Tooltip("Always include splats that hit the SAM mask even if depth gates would reject them.")]
    public bool alwaysIncludeMask = false;
    [Tooltip("Front-only gate: keep splats only if their NDC depth is in front of (<=) mapped Zoe depth within tolerance. Great to stop background bleed.")]
    public bool useFrontGate = true;
    [Header("Scene Occlusion (geometry)")]
    [Tooltip("Use scene-based occlusion: first gather per-pixel nearest NDC depth over masked region from the splats, then reject splats that are behind that local front. Independent of ZoeDepth.")]
    public bool useSceneOcclusion = true;
    [Tooltip("Extra bias added to the per-pixel occlusion threshold (in NDC).")]
    [Range(0f, 0.1f)] public float sceneOcclusionBias = 0.01f;

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
    [Header("Depth (ZoeDepth)")]
    [Tooltip("Request depth from ZoeDepth alongside the mask")]
    public bool requestDepth = true;
    [Tooltip("ZoeDepth variant (ZoeD_N / ZoeD_K / ZoeD_NK)")]
    public string zoeVariant = "ZoeD_N";
    [Tooltip("Local ZoeDepth repo root (optional)")]
    public string zoeRoot = ""; // e.g., ...\\ZoeDepth-main
    [Tooltip("Tolerance for gating with external depth (abs(ndc-depth) in [0,1])")]
    [Range(0f, 0.2f)] public float depthTolerance = 0.03f;
    [Tooltip("Automatically fit ZoeDepth to NDC (computes scale & bias per run)")]
    public bool autoCalibrateDepth = true;
    [Tooltip("Lock the last good calibration (invert/scale/bias) and reuse it for subsequent runs.")]
    public bool lockCalibration = false;
    [Tooltip("Max calibration samples (pixels) used to fit depth mapping")]
    [Range(256, 10000)] public int calibrationSamples = 4000;
    [Tooltip("Linear scale applied to external depth before gating")] public float depthScale = 1f;
    [Tooltip("Linear bias applied to external depth before gating")] public float depthBias = 0f;
    [Tooltip("Invert external depth (use 1-d)")] public bool invertExternalDepth = false;
    [Tooltip("Use ZoeDepth relative band (median±k*IQR) instead of mapping to NDC")]
    public bool useZoeRelativeBand = true;
    [Tooltip("k factor for IQR when building Zoe band")]
    [Range(0.5f, 4f)] public float zoeBandK = 1.5f;
    [Tooltip("Sample ellipse neighborhood around splat center (may over-include). Off = center only.")]
    public bool useEllipseProbe = false;
    [Tooltip("Force ZoeDepth on CPU (safer on low VRAM)")]
    public bool zoeForceCpu = false;
    [Tooltip("Max image dimension sent to ZoeDepth (downscales to limit VRAM)")]
    [Range(128,2048)] public int zoeMaxDim = 512;
    [Header("Splat Shape Gates")]
    [Tooltip("Reject very elongated splats: major/minor > this (0 = off)")]
    [Range(0f, 50f)] public float maxEccentricity = 0f;
    [Tooltip("Reject very large ellipse area in pixels: major*minor > this (0 = off)")]
    public float maxAreaPx = 0f;
    [Header("Debug Overlays")]
    [Tooltip("Draw the SAM mask in screen space for inspection")] public bool debugShowMask = true;
    [Tooltip("Draw the ZoeDepth map (grayscale) in screen space for inspection")] public bool debugShowDepth = true;
    [Range(0f,1f)] public float debugOverlayAlpha = 0.35f;
    Texture2D m_DebugMaskTex;
    Texture2D m_DebugDepthTex;
    string m_LastMaskPath;
    string m_LastDepthPath;
    System.Diagnostics.Process workerProc;
    System.IO.StreamWriter workerStdin;
    System.IO.StreamReader workerStdout;
    System.IO.StreamReader workerStderr;
    volatile bool workerReady = false;
    volatile string lastWorkerStdout = string.Empty;
    volatile string lastWorkerStderr = string.Empty;

    static readonly FieldInfo s_ViewBufferField = typeof(GaussianSplatRenderer).GetField("m_GpuView", BindingFlags.NonPublic | BindingFlags.Instance);

    // Very small JSON helper to grab string values from one-line worker responses
    static void TryExtractJsonString(string json, string key, ref string path)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return;
        try
        {
            string pat = "\\\"" + key + "\\\"\\s*:\\s*\\\"";
            var m = System.Text.RegularExpressions.Regex.Match(json, pat + "(.*?)\\\"");
            if (m.Success && m.Groups.Count > 1)
            {
                string v = m.Groups[1].Value.Replace('\\','/');
                if (!string.IsNullOrEmpty(v)) path = v;
            }
        }
        catch {}
    }

    int kApply = -1;
    int kGather = -1;
    int kClear = -1;
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
            // Optional kernels for depth calibration
            int tmp;
            tmp = maskSelectCS.FindKernel("GatherMaskDepth");
            if (tmp >= 0) kGather = tmp;
            tmp = maskSelectCS.FindKernel("ClearPerPixelDepth");
            if (tmp >= 0) kClear = tmp;
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

        // Debug overlays for mask/depth
        if ((m_DebugMaskTex || m_DebugDepthTex))
        {
            Rect vr = sourceCamera ? sourceCamera.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            var prevCol = GUI.color;
            if (m_DebugMaskTex)
            {
                GUI.color = new Color(1f, 0f, 1f, Mathf.Clamp01(debugOverlayAlpha));
                GUI.DrawTexture(new Rect(vr.x, Screen.height - vr.y - vr.height, vr.width, vr.height), m_DebugMaskTex, ScaleMode.StretchToFill, alphaBlend: true);
            }
            if (m_DebugDepthTex)
            {
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(debugOverlayAlpha));
                GUI.DrawTexture(new Rect(vr.x, Screen.height - vr.y - vr.height, vr.width, vr.height), m_DebugDepthTex, ScaleMode.StretchToFill, alphaBlend: true);
            }
            GUI.color = prevCol;
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
            // Save PNG to temp, call python cli, read mask png (and optional depth)
            string tempDir = Application.temporaryCachePath;
            string token = System.DateTime.UtcNow.Ticks.ToString();
            string inPath = System.IO.Path.Combine(tempDir, $"sam_in_{token}.png");
            string outPath = System.IO.Path.Combine(tempDir, $"sam_out_{token}.png");
            string depthPath = System.IO.Path.Combine(tempDir, $"sam_depth_{token}.png");
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
                string payload;
                // Include a request id (token) so the worker echoes it back; avoids off-by-one frame usage
                string reqId = token;
                if (requestDepth)
                {
                    string depthJson = depthPath.Replace("\\", "/");
                    string zroot = (zoeRoot ?? string.Empty).Replace("\\", "/");
                    payload = "{\"req\":\"" + reqId + "\",\"image\":\"" + imageJson + "\",\"points\":\"" + pointsStr + "\",\"out\":\"" + outJson + "\",\"depth_out\":\"" + depthJson + "\",\"zoe_variant\":\"" + (zoeVariant ?? "") + "\",\"zoe_root\":\"" + zroot + "\",\"zoe_device\":\"" + (zoeForceCpu ? "cpu" : "cuda") + "\",\"zoe_max_dim\":" + zoeMaxDim + "}";
                }
                else
                {
                    payload = "{\"req\":\"" + reqId + "\",\"image\":\"" + imageJson + "\",\"points\":\"" + pointsStr + "\",\"out\":\"" + outJson + "\"}";
                }
                try
                {
                    var swCall = System.Diagnostics.Stopwatch.StartNew();
                    workerStdin.WriteLine(payload);
                    workerStdin.Flush();
                    // Some libs print progress/info to stdout. Read until JSON-like line or timeout.
                    string line = null;
                    var swWait = System.Diagnostics.Stopwatch.StartNew();
                    while (swWait.ElapsedMilliseconds < 20000) // 20s wait for a JSON line
                    {
                        if (workerProc.HasExited) break;
                        if (!workerStdout.EndOfStream)
                        {
                            var l = workerStdout.ReadLine();
                            if (!string.IsNullOrEmpty(l))
                            {
                                // Accept JSON for our request id; also accept minimal result JSON with our expected out path
                                if (l.StartsWith("{") && (l.Contains("\"req\":\"" + reqId + "\"") || l.Contains(outJson)))
                                {
                                    // Guard: ignore warmup responses if they leaked
                                    if (!l.Contains("sam_warmup"))
                                    {
                                        line = l;
                                        break;
                                    }
                                }
                                // otherwise keep waiting for the JSON line
                                if (verboseLogs) Debug.Log($"[SAM Lite] Worker stdout: {l}");
                                line = null;
                            }
                        }
                        System.Threading.Thread.Sleep(10);
                    }
                    swCall.Stop();
                    if (verboseLogs)
                        Debug.Log($"[SAM Lite] Worker call took {swCall.ElapsedMilliseconds} ms, response: {line}");
                    // Fallback: if no JSON came back, synthesize a minimal response for the known paths
                    if (string.IsNullOrEmpty(line))
                    {
                        line = "{\"req\":\"" + reqId + "\",\"out\":\"" + outJson + "\"}";
                    }
                    if (!string.IsNullOrEmpty(line) && !line.Contains("error"))
                    {
                        TryExtractJsonString(line, "out", ref outPath);
                        TryExtractJsonString(line, "depth_out", ref depthPath);
                        string depthVis = null;
                        TryExtractJsonString(line, "depth_vis", ref depthVis);
                        // Telemetry (optional): log ZoeDepth usage details if present
                        string zvar = null, zdev = null, zdim = null, ztta = null, zref = null, zms = null;
                        TryExtractJsonString(line, "zoe_variant", ref zvar);
                        TryExtractJsonString(line, "zoe_device", ref zdev);
                        TryExtractJsonString(line, "zoe_max_dim", ref zdim);
                        TryExtractJsonString(line, "zoe_tta", ref ztta);
                        TryExtractJsonString(line, "zoe_refine", ref zref);
                        TryExtractJsonString(line, "depth_ms", ref zms);
                        if (verboseLogs)
                        {
                            var sbz = new System.Text.StringBuilder();
                            sbz.Append("[SAM Lite] ZoeDepth: ");
                            if (!string.IsNullOrEmpty(zvar)) sbz.Append("variant=").Append(zvar).Append(' ');
                            if (!string.IsNullOrEmpty(zdev)) sbz.Append("device=").Append(zdev).Append(' ');
                            if (!string.IsNullOrEmpty(zdim)) sbz.Append("max_dim=").Append(zdim).Append(' ');
                            if (!string.IsNullOrEmpty(ztta)) sbz.Append("tta=").Append(ztta).Append(' ');
                            if (!string.IsNullOrEmpty(zref)) sbz.Append("refine=").Append(zref).Append(' ');
                            if (!string.IsNullOrEmpty(zms)) sbz.Append("time_ms=").Append(zms);
                            Debug.Log(sbz.ToString());
                        }

                        // Always use raw 16-bit depth for processing; keep vis only for preview
                        m_LastDepthPath = depthPath;
                        string lastDepthVisLocal = depthVis; // for preview open below
                        usedWorker = true;

                    }
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
                    Arguments = requestDepth
                        ? $"\"{cliPath}\" --image \"{inPath}\" --points \"{sb}\" --out \"{outPath}\" --depth_out \"{depthPath}\" --zoe_variant \"{zoeVariant}\"" + (string.IsNullOrEmpty(zoeRoot)?"":$" --zoe_root \"{zoeRoot}\"")
                        : $"\"{cliPath}\" --image \"{inPath}\" --points \"{sb}\" --out \"{outPath}\"",
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
            // Some systems may write the output file a few ms after the worker responds.
            // Wait briefly for the file to appear to avoid false negatives.
            byte[] maskBytes = null;
            const int waitMs = 1500;
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            while (waitSw.ElapsedMilliseconds < waitMs && (maskBytes == null || maskBytes.Length == 0))
            {
                if (System.IO.File.Exists(outPath))
                {
                    try { maskBytes = System.IO.File.ReadAllBytes(outPath); }
                    catch { maskBytes = null; }
                }
                if (maskBytes == null || maskBytes.Length == 0)
                    System.Threading.Thread.Sleep(20);
            }
            if (maskBytes == null || maskBytes.Length == 0)
            {
                Debug.LogError($"[SAM Lite] Python CLI produced no mask at: {outPath}");
                yield break;
            }
            var maskTex = new Texture2D(W, H, TextureFormat.R8, false, true);
            maskTex.LoadImage(maskBytes, markNonReadable: false);
            maskTex.filterMode = FilterMode.Point;
            maskTex.wrapMode = TextureWrapMode.Clamp;
            maskTex.Apply(false, false);
            // Keep a debug copy if requested
            if (debugShowMask)
            {
                if (m_DebugMaskTex) Destroy(m_DebugMaskTex);
                m_DebugMaskTex = new Texture2D(maskTex.width, maskTex.height, TextureFormat.R8, false, true);
                Graphics.CopyTexture(maskTex, m_DebugMaskTex);
                m_DebugMaskTex.filterMode = FilterMode.Point;
                m_DebugMaskTex.wrapMode = TextureWrapMode.Clamp;
            }
            m_LastMaskPath = outPath;
            // Optional depth
            Texture2D depthTex = null;
            if (requestDepth)
            {
                // Wait briefly for the depth file to appear, mirroring the mask wait logic
                const int depthWaitMs = 1500;
                var depthSw = System.Diagnostics.Stopwatch.StartNew();
                byte[] depthBytes = null;
                while (depthSw.ElapsedMilliseconds < depthWaitMs && (depthBytes == null || depthBytes.Length == 0))
                {
                    if (System.IO.File.Exists(depthPath))
                    {
                        try { depthBytes = System.IO.File.ReadAllBytes(depthPath); }
                        catch { depthBytes = null; }
                    }
                    if (depthBytes == null || depthBytes.Length == 0)
                        System.Threading.Thread.Sleep(20);
                }
                if (depthBytes != null && depthBytes.Length > 0)
                {
                    try
                    {
                        // Let Unity choose format on LoadImage; we'll sample .r as float
                        depthTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                        depthTex.LoadImage(depthBytes, markNonReadable: false);
                        depthTex.filterMode = FilterMode.Point;
                        depthTex.wrapMode = TextureWrapMode.Clamp;
                        depthTex.Apply(false, false);
                        if (debugShowDepth)
                        {
                            if (m_DebugDepthTex) Destroy(m_DebugDepthTex);
                            m_DebugDepthTex = new Texture2D(depthTex.width, depthTex.height, TextureFormat.RGBA32, false, true);
                            Graphics.CopyTexture(depthTex, m_DebugDepthTex);
                            m_DebugDepthTex.filterMode = FilterMode.Point;
                            m_DebugDepthTex.wrapMode = TextureWrapMode.Clamp;
                        }
                    }
                    catch (Exception dex)
                    {
                        Debug.LogWarning($"[SAM Lite] Failed to load depth PNG: {dex.Message}");
                        depthTex = null;
                    }
                }
                else if (verboseLogs)
                {
                    Debug.Log($"[SAM Lite] Depth PNG not found or empty at: {depthPath}");
                }
            }
            // Auto-calibrate external depth -> NDC mapping (scale/bias) if requested
            if (requestDepth && autoCalibrateDepth && !lockCalibration)
            {
                if (depthTex == null)
                {
                    if (verboseLogs) Debug.Log("[SAM Lite] Skip auto-calibrate: no depthTex");
                }
                else if (kGather < 0 || kClear < 0)
                {
                    if (verboseLogs) Debug.Log("[SAM Lite] Skip auto-calibrate: kernels missing (Gather/Clear)");
                }
                else
                {
                    if (verboseLogs) Debug.Log("[SAM Lite] Auto-calibrate: gathering samples...");
                    yield return CalibrateDepthMapping(maskTex, depthTex);
                }
            }
            // Optionally open the mask file for quick inspection
            // Use forward slashes to keep Application.OpenURL happy on Windows
            // (no-op if the platform blocks it)
            // This happens only for the local Python path since we have an actual PNG file.
            if (openMaskAfterRun)
            {
                if (!string.IsNullOrEmpty(outPath))
                    Application.OpenURL(outPath.Replace("\\", "/"));

                if (requestDepth)
                {
                    string vis = (System.IO.File.Exists(depthPath.Replace(".png","_vis.png")) ? depthPath.Replace(".png","_vis.png") : null);
                    if (!string.IsNullOrEmpty(vis))
                        Application.OpenURL(vis.Replace("\\", "/"));
                }
            }





            if (applySelection)
                yield return ApplyMaskToSelection(maskTex, depthTex);
            // Attempt to remove temp files (ignore failures if still in use)
            try { if (System.IO.File.Exists(inPath)) System.IO.File.Delete(inPath); } catch { }
            // Keep outputs if user asked to open; otherwise delete
            if (!openMaskAfterRun)
            {
                try { if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath); } catch { }
                if (requestDepth)
                    try { if (System.IO.File.Exists(depthPath)) System.IO.File.Delete(depthPath); } catch { }
            }
            // Clean up transient textures to avoid leaking GPU memory
            if (maskTex) Destroy(maskTex);
            if (depthTex) Destroy(depthTex);
            yield break;
        }

        // HTTP path removed in Lite controller for simplicity and performance predictability
    }

    IEnumerator ApplyMaskToSelection(Texture2D maskTex, Texture2D depthTex)
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

        // Optional: build per-pixel min/max depth within the masked region from the current view's splats
        ComputeBuffer perPixelDepth = null;
        if (useSceneOcclusion && kGather >= 0 && kClear >= 0)
        {
            int pxCount = maskTex.width * maskTex.height;
            perPixelDepth = new ComputeBuffer(pxCount * 2, sizeof(uint));
            // Clear
            maskSelectCS.SetInt("_MaskPixelCount", pxCount);
            maskSelectCS.SetBuffer(kClear, "_PerPixelDepthRange", perPixelDepth);
            int groupsClear = Mathf.Max(1, Mathf.CeilToInt(pxCount / 256f));
            maskSelectCS.Dispatch(kClear, groupsClear, 1, 1);
            // Gather
            maskSelectCS.SetBuffer(kGather, "_SplatViewData", viewBuffer);
            maskSelectCS.SetTexture(kGather, "_MaskTex", maskTex);
            maskSelectCS.SetInt("_SplatCount", splatCount);
            maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
            int renderWg = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
            int renderHg = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
            maskSelectCS.SetFloats("_RenderViewportSize", renderWg, renderHg);
            maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
            maskSelectCS.SetInt("_UseROI", 0);
            maskSelectCS.SetInt("_UseBoxGate", 0);
            maskSelectCS.SetInt("_UseBoxGateBL", 0);
            maskSelectCS.SetBuffer(kGather, "_PerPixelDepthRange", perPixelDepth);
            // Scratch buffers to satisfy kernel signature
            var depthRangeScratch = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Raw);
            var depthHistScratch = new ComputeBuffer(256, sizeof(uint), ComputeBufferType.Raw);
            maskSelectCS.SetBuffer(kGather, "_DepthRange", depthRangeScratch);
            maskSelectCS.SetBuffer(kGather, "_DepthHistogram", depthHistScratch);
            maskSelectCS.SetInt("_CollectHistogram", 0);
            int groupsGather = Mathf.Max(1, Mathf.CeilToInt(splatCount / 128f));
            maskSelectCS.Dispatch(kGather, groupsGather, 1, 1);
            depthRangeScratch.Dispose();
            depthHistScratch.Dispose();
            // No CPU readback needed; buffer used by apply kernel directly
        }

        // Configure and dispatch ApplyMaskSelection
        maskSelectCS.SetBuffer(kApply, "_SplatViewData", viewBuffer);
        maskSelectCS.SetTexture(kApply, "_MaskTex", maskTex);
        if (depthTex != null)
        {
            maskSelectCS.SetTexture(kApply, "_DepthTex", depthTex);
            if (useZoeRelativeBand)
            {
                // Compute Zoe band (median ± k*IQR) inside mask
                float c, hw; ComputeZoeBand(maskTex, depthTex, Mathf.Clamp01(maskThreshold), zoeBandK, out c, out hw);
                maskSelectCS.SetInt("_UseZoeBand", 1);
                maskSelectCS.SetFloat("_ZoeCenter", c);
                maskSelectCS.SetFloat("_ZoeHalfWidth", hw);
                maskSelectCS.SetInt("_UseExtDepth", 0);
                maskSelectCS.SetFloat("_DepthTolerance", 0f);
            }
            else
            {
                maskSelectCS.SetInt("_UseZoeBand", 0);
                maskSelectCS.SetInt("_UseExtDepth", 1);
                maskSelectCS.SetFloat("_DepthTolerance", Mathf.Clamp01(depthTolerance));
                maskSelectCS.SetFloat("_DepthScale", depthScale);
                maskSelectCS.SetFloat("_DepthBias", depthBias);
                maskSelectCS.SetInt("_DepthInvert", invertExternalDepth ? 1 : 0);
            }
        }
        else
        {
            // Bind a 1x1 black texture to satisfy kernel requirements even if not used
            var dummy = Texture2D.blackTexture;
            maskSelectCS.SetTexture(kApply, "_DepthTex", dummy);
            maskSelectCS.SetInt("_UseExtDepth", 0);
            maskSelectCS.SetInt("_UseZoeBand", 0);
            maskSelectCS.SetFloat("_DepthTolerance", 0f);
            maskSelectCS.SetFloat("_DepthScale", 1f);
            maskSelectCS.SetFloat("_DepthBias", 0f);
            maskSelectCS.SetInt("_DepthInvert", 0);
        }
        maskSelectCS.SetInt("_SplatCount", splatCount);
        maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
        maskSelectCS.SetInt("_MaskPixelCount", maskTex.width * maskTex.height);
        int renderW = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
        int renderH = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
        maskSelectCS.SetFloats("_RenderViewportSize", renderW, renderH);
        maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
        maskSelectCS.SetInt("_Mode", (int)applyMode);
        maskSelectCS.SetInt("_AlwaysIncludeMask", alwaysIncludeMask ? 1 : 0);
        maskSelectCS.SetInt("_UseFrontGate", useFrontGate ? 1 : 0);
        maskSelectCS.SetInt("_CollectHistogram", 0);
        maskSelectCS.SetInt("_UseDepthGate", 0);
        if (useSceneOcclusion && perPixelDepth != null)
        {
            maskSelectCS.SetInt("_UsePerPixelDepth", 1);
            maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 1);
            maskSelectCS.SetBuffer(kApply, "_PerPixelDepthRange", perPixelDepth);
            maskSelectCS.SetFloat("_DepthOcclusionBias", Mathf.Max(0f, sceneOcclusionBias));
        }
        else
        {
            maskSelectCS.SetInt("_UsePerPixelDepth", 0);
            maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 0);
        }
        maskSelectCS.SetInt("_SeedCount", 0);
        maskSelectCS.SetFloat("_DepthOcclusionBias", 0f);
        maskSelectCS.SetMatrix("_ObjectToWorld", gs.transform.localToWorldMatrix);
        maskSelectCS.SetFloat("_SeedCullEps", 0f);
        maskSelectCS.SetInt("_UseEllipseProbe", useEllipseProbe ? 1 : 0);
        maskSelectCS.SetInt("_MaxRadiusPx", 0);
        maskSelectCS.SetInt("_MinRadiusPx", 0);
        maskSelectCS.SetFloat("_MaxEccentricity", Mathf.Max(0f, maxEccentricity));
        maskSelectCS.SetFloat("_MaxAreaPx", Mathf.Max(0f, maxAreaPx));

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
        if (verboseLogs)
        {
            // If API exposes counts, log them; otherwise just note dispatch done
            Debug.Log("[SAM Lite] ApplyMask dispatch complete (mask+depth applied).");
        }
        yield return null;
        if (perPixelDepth != null) { perPixelDepth.Dispose(); perPixelDepth = null; }
    }

    static void ComputeZoeBand(Texture2D maskTex, Texture2D depthTex, float thresh, float k, out float center, out float halfWidth)
    {
        int w = maskTex.width, h = maskTex.height; int N = w*h;
        var maskPixels = maskTex.GetPixels();
        var depthPixels = depthTex.GetPixels();
        // Sample up to 20000 pixels for robustness
        int maxS = 20000; int stride = Mathf.Max(1, N / maxS);
        List<float> vals = new List<float>(Mathf.Min(maxS, N));
        for (int i=0;i<N;i+=stride)
        {
            if (maskPixels[i].r >= thresh)
            {
                float v = depthPixels[i].r;
                vals.Add(v);
            }
        }
        if (vals.Count < 8)
        {
            center = 0.5f; halfWidth = 0.1f; return;
        }
        vals.Sort();
        float p50 = Percentile(vals, 0.5f);
        float p25 = Percentile(vals, 0.25f);
        float p75 = Percentile(vals, 0.75f);
        float iqr = Mathf.Max(1e-3f, p75 - p25);
        center = p50;
        halfWidth = Mathf.Clamp(k * iqr, 0.02f, 0.25f);
    }

    static float Percentile(List<float> sorted, float p)
    {
        if (sorted.Count == 0) return 0f;
        float idx = Mathf.Clamp01(p) * (sorted.Count - 1);
        int lo = Mathf.FloorToInt(idx);
        int hi = Mathf.Min(sorted.Count - 1, lo + 1);
        float t = idx - lo;
        return Mathf.Lerp(sorted[lo], sorted[hi], t);
    }

    IEnumerator CalibrateDepthMapping(Texture2D maskTex, Texture2D depthTex)
    {
        int splatCount = gs.splatCount;
        if (splatCount <= 0) yield break;
        if (kGather < 0 || kClear < 0) yield break;

        // Allocate per-pixel depth range buffer: two uints per pixel
        int pxCount = maskTex.width * maskTex.height;
        var perPixel = new ComputeBuffer(pxCount * 2, sizeof(uint));

        var viewBuffer = s_ViewBufferField?.GetValue(gs) as GraphicsBuffer;
        if (viewBuffer == null) { perPixel.Dispose(); yield break; }

        // Clear
        maskSelectCS.SetInt("_MaskPixelCount", pxCount);
        maskSelectCS.SetBuffer(kClear, "_PerPixelDepthRange", perPixel);
        int groupsClear = Mathf.Max(1, Mathf.CeilToInt(pxCount / 256f));
        maskSelectCS.Dispatch(kClear, groupsClear, 1, 1);

        // Gather NDC per-pixel range over masked area
        maskSelectCS.SetBuffer(kGather, "_SplatViewData", viewBuffer);
        maskSelectCS.SetTexture(kGather, "_MaskTex", maskTex);
        maskSelectCS.SetInt("_SplatCount", splatCount);
        maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
        int renderW = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
        int renderH = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
        maskSelectCS.SetFloats("_RenderViewportSize", renderW, renderH);
        maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
        maskSelectCS.SetInt("_UseROI", 0);
        maskSelectCS.SetInt("_UseBoxGate", 0);
        maskSelectCS.SetInt("_UseBoxGateBL", 0);
        maskSelectCS.SetBuffer(kGather, "_PerPixelDepthRange", perPixel);
        maskSelectCS.SetInt("_UsePerPixelDepth", 1);
        // Bind tiny scratch buffers for optional writes in the kernel
        var depthRangeScratch = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Raw);
        var depthHistScratch = new ComputeBuffer(256, sizeof(uint), ComputeBufferType.Raw);
        maskSelectCS.SetBuffer(kGather, "_DepthRange", depthRangeScratch);
        maskSelectCS.SetBuffer(kGather, "_DepthHistogram", depthHistScratch);
        maskSelectCS.SetInt("_CollectHistogram", 0);
        int groupsGather = Mathf.Max(1, Mathf.CeilToInt(splatCount / 128f));
        maskSelectCS.Dispatch(kGather, groupsGather, 1, 1);

        // Ensure GPU work is finished before readback
        yield return new WaitForEndOfFrame();

        // Read back and build sample pairs (extDepth, ndc)
        uint[] range = new uint[pxCount * 2];
        perPixel.GetData(range);
        perPixel.Dispose();
        depthRangeScratch.Dispose();
        depthHistScratch.Dispose();

        // Access external depth from Texture2D (r channel)
        var colors = depthTex.GetPixels();

        // Reservoir sample up to calibrationSamples
        int target = Mathf.Clamp(calibrationSamples, 256, 10000);
        System.Random rng = new System.Random(12345);
        double sumX=0, sumY=0, sumXX=0, sumXY=0; int n=0;
        for (int i = 0; i < pxCount; i++)
        {
            uint minStored = range[(i<<1)+0];
            if (minStored == 0xFFFFFFFFu) continue; // invalid
            uint maxStored = range[(i<<1)+1];
            float ndcMin = minStored / 65535f;
            float ndcMax = Mathf.Max(ndcMin, maxStored / 65535f);
            float ndc = 0.5f*(ndcMin + ndcMax);
            float ext = colors[i].r; // 0..1 from PNG
            if (invertExternalDepth) ext = 1f - ext;

            // Reservoir sample: accept with probability target/(remaining)
            if (n < target || rng.NextDouble() < (double)target / (i+1))
            {
                // running sums
                sumX += ext; sumY += ndc; sumXX += (double)ext*ext; sumXY += (double)ext*ndc; n++;
                if (n > target) { /* keep sums simple; we accept without replacement above */ }
            }
        }

        if (verboseLogs) Debug.Log($"[SAM Lite] Auto-calibrate: collected {n} samples (target {target})");
        if (n >= 8)
        {
            // Fit two models: ext -> ndc and (1-ext) -> ndc; pick the one with lower RMSE.
            // This removes manual polarity confusion and stabilizes gating.
            System.Func<bool, (float a, float b, float rmse)> Fit = (bool invert) =>
            {
                double meanX = sumX / n, meanY = sumY / n;
                double varX = sumXX / n - meanX*meanX;
                double covXY = sumXY / n - meanX*meanY;
                // If invert, adjust first/second moments approximately by substituting x' = 1-x
                if (invert)
                {
                    meanX = 1.0 - meanX;
                    // var is unchanged for (1-x)
                    covXY = -covXY; // Cov(1-x, y) = -Cov(x,y)
                }
                float a = (varX > 1e-6) ? (float)(covXY / varX) : 1f;
                float b = (float)(meanY - a*meanX);
                a = Mathf.Clamp(a, 0.3f, 2.0f);
                b = Mathf.Clamp(b, -0.3f, 0.3f);
                // RMSE over all valid pixels
                double sse = 0.0; int m = 0;
                for (int i = 0; i < pxCount; i++)
                {
                    uint minStored = range[(i<<1)+0];
                    if (minStored == 0xFFFFFFFFu) continue;
                    uint maxStored = range[(i<<1)+1];
                    float ndcMin = minStored / 65535f;
                    float ndcMax = Mathf.Max(ndcMin, maxStored / 65535f);
                    float ndc = 0.5f*(ndcMin + ndcMax);
                    float ext = colors[i].r;
                    if (invert) ext = 1f - ext;
                    float mapped = Mathf.Clamp01(a*ext + b);
                    float r = ndc - mapped;
                    sse += (double)r * r; m++;
                }
                float rmse = (m > 0) ? Mathf.Sqrt((float)(sse / m)) : 1f;
                return (a,b,rmse);
            };

            var fitA = Fit(false);
            var fitB = Fit(true);
            bool useInvert = fitB.rmse + 1e-4f < fitA.rmse; // prefer inverted if clearly better
            float aBest = useInvert ? fitB.a : fitA.a;
            float bBest = useInvert ? fitB.b : fitA.b;
            float rmseBest = useInvert ? fitB.rmse : fitA.rmse;

            // Smooth calibration across runs (prevents oscillation)
            const float alpha = 0.35f; // EMA factor
            if (invertExternalDepth != useInvert)
            {
                // Only flip polarity if improvement is significant (10%)
                float rmseOther = useInvert ? fitA.rmse : fitB.rmse;
                if (rmseBest < 0.9f * rmseOther)
                    invertExternalDepth = useInvert;
            }
            depthScale = Mathf.Lerp(depthScale, aBest, alpha);
            depthBias  = Mathf.Lerp(depthBias,  bBest, alpha);
            // Do NOT override user-set tolerance; only suggest a value for reference.
            float suggestedTol = Mathf.Clamp(2.0f * rmseBest, 0.01f, 0.06f);
            if (verboseLogs) Debug.Log($"[SAM Lite] Auto depth fit: invert={(useInvert?1:0)}, scale={aBest:F3}, bias={bBest:F3}, samples={n}, tol_suggest={suggestedTol:F3} (keeping depthTolerance={depthTolerance:F3})");
        }
        else if (verboseLogs)
        {
            Debug.LogWarning($"[SAM Lite] Auto depth fit: insufficient samples ({n})");
        }
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

            // Warmup no longer generates temp files. Model load already happens on worker start.
            workerReady = true;
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
        if (m_DebugMaskTex) Destroy(m_DebugMaskTex); m_DebugMaskTex = null;
        if (m_DebugDepthTex) Destroy(m_DebugDepthTex); m_DebugDepthTex = null;
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

    [ContextMenu("Open Last Mask PNG")]
    void OpenLastMask()
    {
        if (!string.IsNullOrEmpty(m_LastMaskPath) && System.IO.File.Exists(m_LastMaskPath))
            Application.OpenURL(m_LastMaskPath.Replace("\\", "/"));
        else
            Debug.LogWarning("[SAM Lite] No mask PNG available.");
    }

    [ContextMenu("Open Last Depth PNG")]
    void OpenLastDepth()
    {
        if (!string.IsNullOrEmpty(m_LastDepthPath) && System.IO.File.Exists(m_LastDepthPath))
            Application.OpenURL(m_LastDepthPath.Replace("\\", "/"));
        else
            Debug.LogWarning("[SAM Lite] No depth PNG available.");
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
