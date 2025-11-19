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
    [Tooltip("Front-only gate: keep splats only if their NDC depth is in front of (<=) Zoe depth within tolerance.")]
    public bool useFrontGate = true;
    [Tooltip("Enable experimental Zoe depth gating (front gate, focus depth, etc.). When disabled, SAM selection ignores Zoe depth.")]
    public bool enableDepthGating = true;
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
    [Tooltip("Front-gate tolerance (NDC) when rejecting splats behind the Zoe depth")]
    [Range(0f, 0.2f)] public float depthTolerance = 0.03f;
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
    [Header("Diagnostics")]
    [Tooltip("When enabled, each SAM run writes depth diagnostics (mask, Zoe map, per-pixel depth samples) to DepthDiagnostics/*.txt instead of spamming the console.")]
    public bool writeDepthDiagnostics = false;
    [Header("Depth Overrides")]
    [Tooltip("When enabled, only the SAM mask and probe depth clip can change selection. All other depth gates are disabled.")]
    public bool probeOnlyMode = true;
    [Header("Multi-View Refine (experimental)")]
    [Tooltip("Prepare 3D seeds (and optional extra SAM passes) to refine selections from multiple views.")]
    public bool enableMultiViewRefine = false;
    [Tooltip("Display gizmos for computed 3D seeds so you can verify they sit on the target object.")]
    public bool showSeedGizmos = true;
    [Range(0.005f, 0.25f)] public float seedGizmoRadius = 0.03f;
    [Header("Zoe Depth Probe")]
    [Tooltip("Key that triggers a one-shot Zoe depth probe (highlights splats near Zoe depth at first positive point).")]
    public KeyCode zoeProbeHotkey = KeyCode.P;
    [Tooltip("How close (in Zoe depth units) a splat must be to the probe depth to stay highlighted.")]
    [Range(0.001f, 0.1f)] public float probeDepthTolerance = 0.02f;
    [Tooltip("Manual NDC slack for the CPU depth cull. Negative values push the clip plane forward.")]
    public float probeDepthPlaneSlackNdc = 0.02f;
    [Tooltip("When enabled, the CPU depth plane uses Zoe's auto tolerance instead of the custom slack above.")]
    public bool probeDepthPlaneUseAutoSlack = false;
    [Tooltip("Enable CPU depth plane culling after the probe runs.")]
    public bool useProbeDepthPlaneCull = true;
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
    readonly List<Vector3> m_LatestSeedWorlds = new List<Vector3>();
    bool m_ZoeProbePending = false;
    bool m_ZoeProbeActive = false;
    float m_ZoeProbeDepthValue = 0f;
    Vector3 m_ZoeProbeWorld = Vector3.zero;
    Ray m_ZoeProbeRay;
    bool m_ProbeClipActive = false;
    float m_ProbeClipDepthNdc = 0f;
    float m_ProbeClipTolNdc = 0f;
    float m_ProbeClipDepthMetric = 0f;
    float m_ProbeClipTolMeters = 0f;
    bool m_DebugFocusDepthOnly = false;
    float m_LastZoeDepthMin = 0f;
    float m_LastZoeDepthRange = 1f;
    bool m_LastZoeDepthStatsValid = false;

    static readonly FieldInfo s_ViewBufferField = typeof(GaussianSplatRenderer).GetField("m_GpuView", BindingFlags.NonPublic | BindingFlags.Instance);

    struct PointDepthSample
    {
        public Vector2 point;
        public bool hasDepth;
        public float ndcMin;
        public float ndcMax;
    }

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

    static bool TryExtractJsonFloat(string json, string key, ref float value)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return false;
        try
        {
            string pat = "\\\"" + key + "\\\"\\s*:\\s*([-+0-9.eE]+)";
            var m = System.Text.RegularExpressions.Regex.Match(json, pat);
            if (m.Success && m.Groups.Count > 1)
            {
                if (float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                {
                    value = parsed;
                    return true;
                }
            }
        }
        catch {}
        return false;
    }

    void ProcessZoeMetadataJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;
        float minVal = m_LastZoeDepthMin;
        float rangeVal = m_LastZoeDepthRange;
        bool minFound = TryExtractJsonFloat(json, "depth_min", ref minVal);
        bool rangeFound = TryExtractJsonFloat(json, "depth_range", ref rangeVal);
        if (!rangeFound)
        {
            float maxVal = 0f;
            if (TryExtractJsonFloat(json, "depth_max", ref maxVal) && minFound)
            {
                rangeVal = Mathf.Max(maxVal - minVal, 1e-6f);
                rangeFound = true;
            }
        }
        if (minFound || rangeFound)
        {
            if (!rangeFound)
                rangeVal = Mathf.Max(rangeVal, 1e-6f);
            UpdateZoeDepthStats(minVal, rangeVal);
        }
    }

    void UpdateZoeDepthStats(float depthMin, float depthRange)
    {
        m_LastZoeDepthMin = depthMin;
        m_LastZoeDepthRange = Mathf.Max(1e-6f, depthRange);
        m_LastZoeDepthStatsValid = true;
    }

    void TryLoadDepthMeta(string depthPath)
    {
        string finalMetaPath = depthPath + "_meta.json";
        string metaJson = TryReadFileWithWait(finalMetaPath, 500);
        if (string.IsNullOrEmpty(metaJson))
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(depthPath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(depthPath);
                var candidates = System.IO.Directory.GetFiles(dir, baseName + "*_meta.json");
                if (candidates != null && candidates.Length > 0)
                {
                    Array.Sort(candidates, (a, b) => System.IO.File.GetLastWriteTimeUtc(b).CompareTo(System.IO.File.GetLastWriteTimeUtc(a)));
                    metaJson = TryReadFileWithWait(candidates[0], 200);
                }
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(metaJson))
        {
            try
            {
                ProcessZoeMetadataJson(metaJson);
            }
            catch (Exception ex)
            {
                if (verboseLogs)
                    Debug.LogWarning($"[SAM Lite] Failed to parse Zoe depth meta: {ex.Message}");
            }
        }
        else if (verboseLogs)
        {
            Debug.LogWarning("[SAM Lite] Zoe depth meta not found for " + depthPath);
        }
    }

    static string TryReadFileWithWait(string path, int waitMs)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            if (System.IO.File.Exists(path))
            {
                try { return System.IO.File.ReadAllText(path); }
                catch { }
            }
            System.Threading.Thread.Sleep(10);
        }
        return null;
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

        if (Input.GetKeyDown(zoeProbeHotkey))
        {
            TriggerZoeProbe();
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
        if (showSeedGizmos && Application.isPlaying && sourceCamera && m_LatestSeedWorlds.Count > 0)
        {
            foreach (var seed in m_LatestSeedWorlds)
            {
                Vector3 sp = sourceCamera.WorldToScreenPoint(seed);
                if (sp.z < 0f)
                    continue;
                float sy_gui = Screen.height - sp.y;
                DrawCrosshair(new Vector2(sp.x, sy_gui), new Color(1f, 0.2f, 1f, 0.95f));
            }
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
        m_LastZoeDepthStatsValid = false;
        m_ProbeClipActive = false;
        m_ProbeClipDepthMetric = 0f;
        m_ProbeClipTolMeters = 0f;

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

                        ProcessZoeMetadataJson(line);

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
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    Debug.LogError($"[SAM Lite] Python CLI failed: {stderr}");
                    yield break;
                }
                if (!string.IsNullOrEmpty(stdout))
                    ProcessZoeMetadataJson(stdout);
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
                        TryLoadDepthMeta(depthPath);
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
        var posBuffer = gs ? gs.GetPosBuffer() : null;
        if (viewBuffer == null)
        {
            Debug.LogWarning("[SAM Lite] Unable to access splat view buffer; selection update skipped.");
            yield break;
        }

        // Optional: build per-pixel min/max depth within the masked region from the current view's splats
        float depthScaleUniform = 1f;
        float depthBiasUniform = 0f;
        bool depthMappingReady = false;
        ComputeBuffer perPixelDepth = null;
        int positiveCount = positivePoints != null ? positivePoints.Count : 0;
        bool depthAvailable = depthTex != null;
        if (m_DebugFocusDepthOnly && positiveCount == 0)
            Debug.LogWarning("[SAM Lite] Focus-depth debug highlighting requires at least one positive point.");
        bool depthFeaturesAllowed = depthAvailable && enableDepthGating;
        bool depthFeaturesActive = depthAvailable && (enableDepthGating || m_DebugFocusDepthOnly);
        bool wantSeedDepth = (positiveCount > 0) && (depthFeaturesActive || enableMultiViewRefine || showSeedGizmos || writeDepthDiagnostics);
        bool useBand = depthFeaturesAllowed && useZoeRelativeBand && !m_DebugFocusDepthOnly;
        bool hasFocusDepthSample = false;
        bool focusDepthFromPerPixel = false;
        float focusDepthNdc = 0f;
        float focusDepthNdcMax = 0f;
        List<PointDepthSample> pointDepthSamples = (positiveCount > 0) ? new List<PointDepthSample>(positiveCount) : null;
        PointDepthSample medianDepthSample = default;
        bool medianDepthSampleValid = false;
        bool needPerPixelDepth = (
            (depthFeaturesAllowed && (useFrontGate || useZoeRelativeBand)) ||
            useSceneOcclusion ||
            ((enableMultiViewRefine || showSeedGizmos || writeDepthDiagnostics) && positiveCount > 0)
        ) && kGather >= 0 && kClear >= 0;
        if (probeOnlyMode && positiveCount > 0)
            needPerPixelDepth = kGather >= 0 && kClear >= 0;
        int sampleRadiusPx = 24;
        if (needPerPixelDepth)
        {
            int pxCount = maskTex.width * maskTex.height;
            perPixelDepth = new ComputeBuffer(pxCount * 2, sizeof(uint));
            maskSelectCS.SetInt("_MaskPixelCount", pxCount);
            maskSelectCS.SetBuffer(kClear, "_PerPixelDepthRange", perPixelDepth);
            int groupsClear = Mathf.Max(1, Mathf.CeilToInt(pxCount / 256f));
            maskSelectCS.Dispatch(kClear, groupsClear, 1, 1);
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
            maskSelectCS.SetInt("_UsePerPixelDepth", 1);
            maskSelectCS.SetInt("_UseEllipseProbe", useEllipseProbe ? 1 : 0);
            maskSelectCS.SetInt("_UseCenterMask", 0);
            maskSelectCS.SetInt("_MaxRadiusPx", 0);
            maskSelectCS.SetInt("_MinRadiusPx", 0);
            maskSelectCS.SetFloat("_MaxEccentricity", Mathf.Max(0f, maxEccentricity));
            maskSelectCS.SetFloat("_MaxAreaPx", Mathf.Max(0f, maxAreaPx));
            maskSelectCS.SetBuffer(kGather, "_PerPixelDepthRange", perPixelDepth);
            var depthRangeScratch = new ComputeBuffer(2, sizeof(uint), ComputeBufferType.Raw);
            var depthHistScratch = new ComputeBuffer(256, sizeof(uint), ComputeBufferType.Raw);
            maskSelectCS.SetBuffer(kGather, "_DepthRange", depthRangeScratch);
            maskSelectCS.SetBuffer(kGather, "_DepthHistogram", depthHistScratch);
            maskSelectCS.SetInt("_CollectHistogram", 0);
            int groupsGather = Mathf.Max(1, Mathf.CeilToInt(splatCount / 128f));
            maskSelectCS.Dispatch(kGather, groupsGather, 1, 1);
            depthRangeScratch.Dispose();
            depthHistScratch.Dispose();

            if (positivePoints != null && positivePoints.Count > 0 && pointDepthSamples != null)
            {
                pointDepthSamples.Clear();
                for (int i = 0; i < positivePoints.Count; ++i)
                {
                    var pt = positivePoints[i];
                    var sample = new PointDepthSample { point = pt, hasDepth = false, ndcMin = 0f, ndcMax = 0f };
                    if (TrySamplePerPixelDepthExpanded(perPixelDepth, maskTex.width, maskTex.height, pt, sampleRadiusPx, out var ndcMin, out var ndcMax))
                    {
                        sample.hasDepth = true;
                        sample.ndcMin = ndcMin;
                        sample.ndcMax = ndcMax;
                    }
                    pointDepthSamples.Add(sample);
                }
            }
        }
        if (pointDepthSamples != null && pointDepthSamples.Count > 0)
        {
            List<float> ndcValues = new List<float>(pointDepthSamples.Count);
            float widestSpan = 0f;
            foreach (var sample in pointDepthSamples)
            {
                if (!sample.hasDepth)
                    continue;
                ndcValues.Add(sample.ndcMin);
                float span = Mathf.Abs(sample.ndcMax - sample.ndcMin);
                if (span > widestSpan)
                    widestSpan = span;
            }
            if (ndcValues.Count > 0)
            {
                ndcValues.Sort();
                float median = ndcValues[ndcValues.Count / 2];
                focusDepthNdc = median;
                focusDepthNdcMax = focusDepthNdc + Mathf.Max(widestSpan, 1e-5f);
                hasFocusDepthSample = true;
                focusDepthFromPerPixel = true;
                float bestDiff = float.MaxValue;
                foreach (var sample in pointDepthSamples)
                {
                    if (!sample.hasDepth)
                        continue;
                    float diff = Mathf.Abs(sample.ndcMin - median);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        medianDepthSample = sample;
                        medianDepthSampleValid = true;
                    }
                }
            }
        }
        bool focusDepthSampleValid = hasFocusDepthSample;
        List<Vector3> seedWorldsForDisplay = null;
        if ((enableMultiViewRefine || showSeedGizmos) && positiveCount > 0)
        {
            seedWorldsForDisplay = new List<Vector3>(positiveCount);
            for (int i = 0; i < positivePoints.Count; ++i)
            {
                var pt = positivePoints[i];
                bool added = false;
                if (pointDepthSamples != null && pointDepthSamples.Count > i && pointDepthSamples[i].hasDepth)
                {
                    if (TryProjectDepthToWorld(sourceCamera, pt, pointDepthSamples[i].ndcMin, out var seedWorld))
                    {
                        seedWorldsForDisplay.Add(seedWorld);
                        added = true;
                    }
                }
                if (!added && depthTex != null)
                {
                    int px = Mathf.Clamp(Mathf.RoundToInt(pt.x * (depthTex.width - 1)), 0, depthTex.width - 1);
                    int py = Mathf.Clamp(Mathf.RoundToInt((1f - pt.y) * (depthTex.height - 1)), 0, depthTex.height - 1);
                    float zoeDepth = depthTex.GetPixel(px, py).r;
                    if (TryComputeZoeProbeData(pt, zoeDepth, out _, out _, out var seedFromZoe))
                    {
                        seedWorldsForDisplay.Add(seedFromZoe);
                        added = true;
                    }
                }
                if (!added && sourceCamera)
                {
                    var ray = sourceCamera.ViewportPointToRay(new Vector3(pt.x, pt.y, 0f));
                    seedWorldsForDisplay.Add(ray.origin + ray.direction * 1.0f);
                }
            }
            CacheSeedWorlds(seedWorldsForDisplay);
        }
        else
        {
            CacheSeedWorlds(null);
        }

        m_ZoeProbeActive = false;
        m_DebugFocusDepthOnly = false;
        if (m_ZoeProbePending)
        {
            m_ZoeProbePending = false;
            if (!depthAvailable)
            {
                Debug.LogWarning("[SAM Lite] Zoe depth probe requires depth output (enable Request Depth).");
            }
            else if (positivePoints == null || positivePoints.Count == 0)
            {
                Debug.LogWarning("[SAM Lite] Zoe depth probe requires at least one positive point.");
            }
            else if (!TryGetZoeDepthAtPoint(depthTex, positivePoints[0], out m_ZoeProbeDepthValue))
            {
                Debug.LogWarning("[SAM Lite] Zoe depth probe failed: unable to sample Zoe depth at the positive point.");
            }
            else
            {
                Vector2 probePt = medianDepthSampleValid ? medianDepthSample.point : positivePoints[0];
                bool probeHasPerPixel = false;
                float probeNdcPerPixel = 0f;
                float probeNdcMax = 0f;
                if (medianDepthSampleValid && medianDepthSample.hasDepth)
                {
                    probeHasPerPixel = true;
                    probeNdcPerPixel = medianDepthSample.ndcMin;
                    probeNdcMax = medianDepthSample.ndcMax;
                }
                else if (perPixelDepth != null)
                {
                    probeHasPerPixel = TrySamplePerPixelDepthExpanded(perPixelDepth, maskTex.width, maskTex.height, probePt, sampleRadiusPx, out probeNdcPerPixel, out probeNdcMax);
                    if (probeHasPerPixel)
                    {
                        hasFocusDepthSample = true;
                        focusDepthFromPerPixel = true;
                        focusDepthNdc = probeNdcPerPixel;
                        focusDepthNdcMax = Mathf.Max(probeNdcPerPixel, probeNdcMax);
                    }
                }

                if (!TryComputeZoeProbeData(probePt, m_ZoeProbeDepthValue, out float probeMetric, out float probeNdc, out Vector3 probeWorld))
                {
                    Debug.LogWarning("[SAM Lite] Zoe depth probe failed: missing Zoe depth metadata from worker response.");
                }
                else
                {
                    m_ZoeProbeActive = true;
                    m_DebugFocusDepthOnly = true;
                    focusDepthSampleValid = true;
                    if (!probeHasPerPixel)
                        focusDepthNdc = m_ZoeProbeDepthValue;

                    float clipDepthNdc = probeHasPerPixel ? probeNdcPerPixel : probeNdc;
                    float clipTolNdc = 0f;
                    float probeTolMeters = Mathf.Max(1e-4f, probeDepthTolerance);
                    if (probeTolMeters > 0f)
                        clipTolNdc = ComputeProbeTolNdc(probeMetric, probeTolMeters);
                    Vector3 clipWorld = probeWorld;
                    if (probeHasPerPixel)
                    {
                        float localRange = Mathf.Abs(focusDepthNdcMax - focusDepthNdc);
                        if (localRange < 1e-4f)
                            localRange = Mathf.Clamp(probeDepthTolerance * 0.1f, 0.0005f, 0.05f);
                        clipTolNdc = Mathf.Max(localRange, clipTolNdc);
                        if (TryProjectDepthToWorld(sourceCamera, probePt, focusDepthNdc, out var worldFromDepth))
                            clipWorld = worldFromDepth;
                    }
                    m_ZoeProbeWorld = clipWorld;
                    m_ProbeClipDepthNdc = clipDepthNdc;
                    m_ProbeClipTolNdc = clipTolNdc;
                    m_ProbeClipActive = true;
                    m_ProbeClipDepthMetric = probeMetric;
                    m_ProbeClipTolMeters = probeTolMeters;
                    CacheSeedWorlds(new List<Vector3> { clipWorld });
                    float unityDepth = sourceCamera ? sourceCamera.WorldToViewportPoint(clipWorld).z : probeMetric;
                    Debug.Log($"[SAM Lite] Zoe probe sample at ({probePt.x:F3},{probePt.y:F3}) = norm {m_ZoeProbeDepthValue:F4}, metric {probeMetric:F3}, ndc {clipDepthNdc:F4}, unityZ {unityDepth:F3}");
                }
            }
        }

        if (depthFeaturesAllowed && useFrontGate && perPixelDepth != null)
        {
            int mappingSamples;
            depthMappingReady = ComputeDepthMapping(maskTex, depthTex, perPixelDepth, out depthScaleUniform, out depthBiasUniform, out mappingSamples);
            if (depthMappingReady)
            {
                if (verboseLogs)
                    Debug.Log($"[SAM Lite] Zoe->NDC mapping: scale={depthScaleUniform:F3} bias={depthBiasUniform:F3} samples={mappingSamples}");
            }
            else
            {
                if (useBand && positivePoints != null && positivePoints.Count > 0)
                    Debug.Log($"[SAM Lite] Zoe front gate skipped (samples={mappingSamples}); falling back to focus band only.");
                else
                    Debug.LogWarning($"[SAM Lite] Zoe front gate disabled: unable to correlate Zoe depth to NDC (samples={mappingSamples}).");
            }
        }
        else if (depthFeaturesAllowed && useFrontGate && perPixelDepth == null)
        {
            Debug.LogWarning("[SAM Lite] Zoe front gate disabled: per-pixel depth buffer unavailable.");
        }

        // Configure and dispatch ApplyMaskSelection
        maskSelectCS.SetBuffer(kApply, "_SplatViewData", viewBuffer);
        if (posBuffer != null)
            maskSelectCS.SetBuffer(kApply, "_SplatPos", posBuffer);
        maskSelectCS.SetTexture(kApply, "_MaskTex", maskTex);
        var depthTexture = depthAvailable ? depthTex : Texture2D.blackTexture;
        maskSelectCS.SetTexture(kApply, "_DepthTex", depthTexture);
        float diagZoeCenter = 0.5f;
        float diagZoeHalfWidth = 0.5f;
        bool diagBandActive = false;
        bool enforceProbeOnly = probeOnlyMode;
        if (!enforceProbeOnly && useBand)
        {
            float c, hw;
            if (perPixelDepth != null)
                ComputeZoeBandFocused(maskTex, depthTex, perPixelDepth, Mathf.Clamp01(maskThreshold), zoeBandK, out c, out hw);
            else
                ComputeZoeBand(maskTex, depthTex, Mathf.Clamp01(maskThreshold), zoeBandK, positivePoints, 0.08f, out c, out hw);
            if (positivePoints != null && positivePoints.Count > 0)
            {
                var ptFocus = positivePoints[0];
                int px = Mathf.Clamp(Mathf.RoundToInt(ptFocus.x * (depthTex.width - 1)), 0, depthTex.width - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt((1f - ptFocus.y) * (depthTex.height - 1)), 0, depthTex.height - 1);
                float focusSample = depthTex.GetPixel(px, py).r;
                float focusWidth = Mathf.Clamp(depthTolerance * 0.75f, 0.003f, 0.08f);
                c = Mathf.Lerp(c, focusSample, 0.6f);
                hw = Mathf.Clamp(Mathf.Max(hw, focusWidth), focusWidth, 0.2f);
            }
            maskSelectCS.SetInt("_UseZoeBand", 1);
            maskSelectCS.SetFloat("_ZoeCenter", c);
            maskSelectCS.SetFloat("_ZoeHalfWidth", hw);
            if (positivePoints != null && positivePoints.Count > 0)
            {
                var pt = positivePoints[0];
                int px = Mathf.Clamp(Mathf.RoundToInt(pt.x * (depthTex.width - 1)), 0, depthTex.width - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt((1f - pt.y) * (depthTex.height - 1)), 0, depthTex.height - 1);
                float zoeSample = depthTex.GetPixel(px, py).r;
                Debug.Log($"[SAM Lite] Zoe sample at ({pt.x:F3},{pt.y:F3}) = {zoeSample:F4}, band {c - hw:F4}-{c + hw:F4}");
            }
            diagBandActive = true;
            diagZoeCenter = c;
            diagZoeHalfWidth = hw;
        }
        else
        {
            maskSelectCS.SetInt("_UseZoeBand", 0);
            maskSelectCS.SetFloat("_ZoeCenter", 0.5f);
            maskSelectCS.SetFloat("_ZoeHalfWidth", 0.5f);
        }
        bool enableFrontGate = depthFeaturesAllowed && useFrontGate && depthMappingReady && !m_DebugFocusDepthOnly;
        if (enforceProbeOnly)
            enableFrontGate = false;
        maskSelectCS.SetInt("_UseExtDepth", enableFrontGate ? 1 : 0);
        maskSelectCS.SetFloat("_DepthTolerance", enableFrontGate ? Mathf.Clamp01(depthTolerance) : 0f);
        maskSelectCS.SetFloat("_DepthScale", depthScaleUniform);
        maskSelectCS.SetFloat("_DepthBias", depthBiasUniform);
        bool focusGateActive = focusDepthSampleValid && ((depthFeaturesActive && !m_DebugFocusDepthOnly) || m_DebugFocusDepthOnly);
        if (enforceProbeOnly)
            focusGateActive = false;
        if (m_DebugFocusDepthOnly && !focusGateActive)
            Debug.LogWarning("[SAM Lite] Focus-depth debug requested but no valid Zoe/per-pixel depth sample was available.");
        maskSelectCS.SetInt("_UseFocusDepth", focusGateActive ? 1 : 0);
        maskSelectCS.SetFloat("_FocusDepth", focusDepthNdc);
        float focusTol = Mathf.Max(0.003f, depthTolerance * 0.6f);
        float focusTolWide = Mathf.Max(focusTol * 2f, depthTolerance);
        if (m_ZoeProbeActive)
        {
            float baseTol = Mathf.Clamp(probeDepthTolerance, 0.001f, 0.1f);
            focusTol = baseTol * 0.5f;
            focusTolWide = baseTol;
        }
        maskSelectCS.SetFloat("_FocusDepthTol", focusGateActive ? focusTol : 0f);
        maskSelectCS.SetFloat("_FocusDepthTolWide", focusGateActive ? focusTolWide : 0f);
        bool forceMask = (!m_ZoeProbeActive && m_DebugFocusDepthOnly);
        maskSelectCS.SetInt("_ForceMaskOn", (forceMask && focusGateActive) ? 1 : 0);
        maskSelectCS.SetInt("_FocusDepthUseZoe", (m_ZoeProbeActive && focusGateActive) ? 1 : 0);
        maskSelectCS.SetFloat("_FocusDepthNorm", (m_ZoeProbeActive && focusGateActive) ? m_ZoeProbeDepthValue : 0f);
        maskSelectCS.SetInt("_UseProbeDepthClip", m_ProbeClipActive ? 1 : 0);
        maskSelectCS.SetFloat("_ProbeDepthClip", m_ProbeClipDepthNdc);
        maskSelectCS.SetFloat("_ProbeDepthClipTol", m_ProbeClipTolNdc);
        maskSelectCS.SetInt("_SplatCount", splatCount);
        maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
        maskSelectCS.SetInt("_MaskPixelCount", maskTex.width * maskTex.height);
        int renderW = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
        int renderH = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
        maskSelectCS.SetFloats("_RenderViewportSize", renderW, renderH);
        maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
        maskSelectCS.SetInt("_Mode", (int)applyMode);
        bool allowMaskOverride = (!enforceProbeOnly && alwaysIncludeMask && !m_ProbeClipActive);
        maskSelectCS.SetInt("_AlwaysIncludeMask", allowMaskOverride ? 1 : 0);
        maskSelectCS.SetInt("_CollectHistogram", 0);
        maskSelectCS.SetInt("_UseDepthGate", 0);
        bool hasPerPixelDepth = perPixelDepth != null;
        maskSelectCS.SetInt("_UsePerPixelDepth", hasPerPixelDepth ? 1 : 0);
        bool applyPerPixelOcclusion = hasPerPixelDepth && (useSceneOcclusion || (depthFeaturesAllowed && !m_DebugFocusDepthOnly));
        if (enforceProbeOnly)
            applyPerPixelOcclusion = false;
        if (applyPerPixelOcclusion)
        {
            maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 1);
            maskSelectCS.SetBuffer(kApply, "_PerPixelDepthRange", perPixelDepth);
            float occlusionBias = Mathf.Max(0.001f, sceneOcclusionBias);
            if (focusGateActive)
                occlusionBias = Mathf.Max(occlusionBias, focusTolWide);
            else if (enableFrontGate || useBand)
                occlusionBias = Mathf.Max(occlusionBias, Mathf.Clamp01(depthTolerance));
            maskSelectCS.SetFloat("_DepthOcclusionBias", occlusionBias);
        }
        else
        {
            maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 0);
            if (hasPerPixelDepth)
                maskSelectCS.SetBuffer(kApply, "_PerPixelDepthRange", perPixelDepth);
            maskSelectCS.SetFloat("_DepthOcclusionBias", 0f);
        }
        maskSelectCS.SetInt("_SeedCount", 0);
        maskSelectCS.SetMatrix("_ObjectToWorld", gs.transform.localToWorldMatrix);
        maskSelectCS.SetFloat("_SeedCullEps", 0f);
        maskSelectCS.SetInt("_UseEllipseProbe", useEllipseProbe ? 1 : 0);
        maskSelectCS.SetInt("_UseCenterMask", depthAvailable ? 1 : 0);
        maskSelectCS.SetInt("_MaxRadiusPx", 0);
        maskSelectCS.SetInt("_MinRadiusPx", 0);
        maskSelectCS.SetFloat("_MaxEccentricity", Mathf.Max(0f, maxEccentricity));
        maskSelectCS.SetFloat("_MaxAreaPx", Mathf.Max(0f, maxAreaPx));

        maskSelectCS.SetInt("_UseProbeSphere", 0);
        maskSelectCS.SetFloats("_ProbeSphereCenter", 0f, 0f, 0f, 0f);
        maskSelectCS.SetFloat("_ProbeSphereRadius", 0f);

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
        if (writeDepthDiagnostics && depthAvailable)
        {
            WriteDepthDiagnostics(maskTex, depthTex, perPixelDepth, focusGateActive, focusDepthNdc, focusTol, focusTolWide, depthFeaturesAllowed && diagBandActive, diagZoeCenter, diagZoeHalfWidth, enableFrontGate, Mathf.Clamp01(depthTolerance), depthScaleUniform, depthBiasUniform, m_ProbeClipActive, m_ProbeClipDepthNdc, m_ProbeClipTolNdc, m_ProbeClipDepthMetric, m_ProbeClipTolMeters, pointDepthSamples);
        }
        if (useProbeDepthPlaneCull && m_ZoeProbeActive && posBuffer != null && sel != null && sourceCamera)
        {
            float planeTolNdc = probeDepthPlaneUseAutoSlack ? m_ProbeClipTolNdc : probeDepthPlaneSlackNdc;
            bool cpuCull = ApplyProbeDepthCullCPU(sel, posBuffer, splatCount, sourceCamera, m_ZoeProbeWorld, m_ProbeClipDepthNdc, planeTolNdc);
            if (cpuCull && verboseLogs)
                Debug.Log($"[SAM Lite] Probe depth CPU cull removed splats behind ndc {m_ProbeClipDepthNdc:F4} tol {planeTolNdc:F5}.");
        }
        gs.UpdateEditCountsAndBounds();
        if (verboseLogs)
        {
            // If API exposes counts, log them; otherwise just note dispatch done
            Debug.Log("[SAM Lite] ApplyMask dispatch complete (mask+depth applied).");
        }
        yield return null;
        if (perPixelDepth != null) { perPixelDepth.Dispose(); perPixelDepth = null; }
        m_DebugFocusDepthOnly = false;
        m_ZoeProbeActive = false;
        m_ProbeClipActive = false;
        m_ProbeClipDepthMetric = 0f;
        m_ProbeClipTolMeters = 0f;
    }

    static void ComputeZoeBand(Texture2D maskTex, Texture2D depthTex, float thresh, float k, List<Vector2> focusPoints, float focusRadiusFrac, out float center, out float halfWidth)
    {
        int w = maskTex.width, h = maskTex.height; int N = w * h;
        var maskPixels = maskTex.GetPixels();
        var depthPixels = depthTex.GetPixels();
        int maxS = 20000; int stride = Mathf.Max(1, N / maxS);
        bool useFocus = focusPoints != null && focusPoints.Count > 0 && focusRadiusFrac > 0f;
        float radiusPx = Mathf.Clamp(focusRadiusFrac * Mathf.Max(w, h), 4f, Mathf.Max(w, h));
        float radius2 = radiusPx * radiusPx;
        List<Vector2> focusPx = null;
        if (useFocus)
        {
            focusPx = new List<Vector2>(focusPoints.Count);
            foreach (var p in focusPoints)
            {
                float fx = Mathf.Clamp01(p.x) * (w - 1);
                float fy = Mathf.Clamp01(p.y) * (h - 1);
                focusPx.Add(new Vector2(fx, fy));
            }
        }
        List<float> vals = new List<float>(Mathf.Min(maxS, N));
        for (int i = 0; i < N; i += stride)
        {
            if (maskPixels[i].r < thresh)
                continue;
            if (useFocus)
            {
                int px = i % w;
                int py = i / w;
                bool inside = false;
                for (int f = 0; f < focusPx.Count; f++)
                {
                    var fp = focusPx[f];
                    float dx = px - fp.x;
                    float dy = py - fp.y;
                    if (dx * dx + dy * dy <= radius2)
                    {
                        inside = true;
                        break;
                    }
                }
                if (!inside)
                    continue;
            }
            vals.Add(depthPixels[i].r);
        }
        if (vals.Count < 8)
        {
            center = 0.5f;
            halfWidth = 0.1f;
            return;
        }
        vals.Sort();
        float p50 = Percentile(vals, 0.5f);
        float p25 = Percentile(vals, 0.25f);
        float p75 = Percentile(vals, 0.75f);
        float iqr = Mathf.Max(1e-3f, p75 - p25);
        center = p50;
        halfWidth = Mathf.Clamp(k * iqr, 0.02f, 0.25f);
    }

    static void ComputeZoeBandFocused(Texture2D maskTex, Texture2D depthTex, ComputeBuffer perPixelDepth, float thresh, float k, out float center, out float halfWidth)
    {
        center = 0.5f;
        halfWidth = 0.1f;
        if (maskTex == null || depthTex == null || perPixelDepth == null)
        {
            ComputeZoeBand(maskTex, depthTex, thresh, k, null, 0f, out center, out halfWidth);
            return;
        }

        int w = maskTex.width, h = maskTex.height; int N = w * h;
        var maskPixels = maskTex.GetPixels();
        var depthPixels = depthTex.GetPixels();
        uint[] ndcRanges = new uint[N * 2];
        perPixelDepth.GetData(ndcRanges);

        const int maxSamples = 50000;
        int stride = Mathf.Max(1, N / maxSamples);
        List<(float ndc, float zoe)> samples = new List<(float ndc, float zoe)>(Mathf.Min(maxSamples, N / stride + 1));

        for (int i = 0; i < N; i += stride)
        {
            if (maskPixels[i].r < thresh)
                continue;
            uint minStored = ndcRanges[(i << 1) + 0];
            if (minStored == 0xFFFFFFFFu)
                continue;
            uint maxStored = ndcRanges[(i << 1) + 1];
            float ndcMin = minStored / 65535f;
            float ndcMax = Mathf.Max(ndcMin, maxStored / 65535f);
            float ndc = ndcMin;
            float zoe = depthPixels[i].r;
            samples.Add((ndc, zoe));
        }

        if (samples.Count < 32)
        {
            ComputeZoeBand(maskTex, depthTex, thresh, k, null, 0f, out center, out halfWidth);
            return;
        }

        samples.Sort((a, b) => a.ndc.CompareTo(b.ndc));
        int keepCount = Mathf.Max(32, (int)(samples.Count * 0.35f));
        keepCount = Mathf.Min(keepCount, samples.Count);

        List<float> zoeVals = new List<float>(keepCount);
        for (int i = 0; i < keepCount; i++)
            zoeVals.Add(samples[i].zoe);
        zoeVals.Sort();

        float p50 = Percentile(zoeVals, 0.5f);
        float p25 = Percentile(zoeVals, 0.25f);
        float p75 = Percentile(zoeVals, 0.75f);
        float iqr = Mathf.Max(1e-3f, p75 - p25);
        center = p50;
        halfWidth = Mathf.Clamp(k * iqr, 0.01f, 0.15f);
    }

    static bool ComputeDepthMapping(Texture2D maskTex, Texture2D depthTex, ComputeBuffer perPixelDepth, out float slope, out float intercept, out int sampleCount)
    {
        slope = 1f;
        intercept = 0f;
        sampleCount = 0;
        if (maskTex == null || depthTex == null || perPixelDepth == null)
            return false;

        int w = maskTex.width;
        int h = maskTex.height;
        int N = w * h;
        var maskPixels = maskTex.GetPixels();
        var depthPixels = depthTex.GetPixels();
        uint[] ndcRanges = new uint[N * 2];
        perPixelDepth.GetData(ndcRanges);

        const int maxSamples = 50000;
        int stride = Mathf.Max(1, N / maxSamples);
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        int count = 0;
        float minZoe = float.MaxValue;
        float maxZoe = float.MinValue;
        float minNdc = float.MaxValue;
        float maxNdc = float.MinValue;
        float nearZoe = float.MaxValue;
        float nearNdc = 0f;
        float farZoe = float.MinValue;
        float farNdc = 0f;
        for (int i = 0; i < N; i += stride)
        {
            if (maskPixels[i].r < 0.5f)
                continue;
            uint minStored = ndcRanges[(i << 1) + 0];
            if (minStored == 0xFFFFFFFFu)
                continue;
            uint maxStored = ndcRanges[(i << 1) + 1];
            float ndcMin = minStored / 65535f;
            float ndcMax = Mathf.Max(ndcMin, maxStored / 65535f);
            float ndc = 0.5f * (ndcMin + ndcMax);
            float zoe = depthPixels[i].r;
            sumX += zoe;
            sumY += ndc;
            sumXX += zoe * zoe;
            sumXY += zoe * ndc;
            count++;
            if (zoe < minZoe) minZoe = zoe;
            if (zoe > maxZoe) maxZoe = zoe;
            if (ndc < minNdc) minNdc = ndc;
            if (ndc > maxNdc) maxNdc = ndc;
            if (zoe < nearZoe) { nearZoe = zoe; nearNdc = ndc; }
            if (zoe > farZoe) { farZoe = zoe; farNdc = ndc; }
        }
        sampleCount = count;
        if (count < 16)
            return false;
        float zoeSpan = Mathf.Max(0f, farZoe - nearZoe);
        float ndcSpan = Mathf.Max(0f, farNdc - nearNdc);
        if (zoeSpan < 1e-4f || ndcSpan < 1e-4f)
        {
            slope = 0f;
            intercept = nearNdc;
            return float.IsFinite(intercept);
        }
        double meanX = sumX / count;
        double meanY = sumY / count;
        double varX = sumXX / count - meanX * meanX;
        double covXY = sumXY / count - meanX * meanY;
        if (varX < 1e-8)
        {
            float zoeRange = Mathf.Max(1e-4f, maxZoe - minZoe);
            float ndcRange = Mathf.Max(1e-4f, maxNdc - minNdc);
            slope = Mathf.Clamp(ndcRange / zoeRange, 0f, 1e3f);
            intercept = minNdc - slope * minZoe;
        }
        else
        {
            slope = (float)(covXY / varX);
            intercept = (float)(meanY - slope * meanX);
        }
        if (float.IsNaN(slope) || float.IsInfinity(slope))
            return false;
        if (float.IsNaN(intercept) || float.IsInfinity(intercept))
            return false;
        return true;
    }

    void WriteDepthDiagnostics(Texture2D maskTex, Texture2D depthTex, ComputeBuffer perPixelDepth, bool focusActive, float focusDepth, float focusTol, float focusTolWide, bool bandActive, float bandCenter, float bandHalfWidth, bool frontGateActive, float depthToleranceNdc, float depthScale, float depthBias, bool probeClipActive, float probeClipDepthNdc, float probeClipTolNdc, float probeClipDepthMetric, float probeClipTolMeters, List<PointDepthSample> pointSamples)
    {
        try
        {
            string projectDir = System.IO.Path.GetDirectoryName(Application.dataPath);
            string diagDir = System.IO.Path.Combine(projectDir, "DepthDiagnostics");
            System.IO.Directory.CreateDirectory(diagDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            string txtPath = System.IO.Path.Combine(diagDir, $"diag_{stamp}.txt");
            using (var sw = new System.IO.StreamWriter(txtPath))
            {
                sw.WriteLine($"Timestamp: {DateTime.Now:O}");
                sw.WriteLine($"Positive points: {(positivePoints != null ? positivePoints.Count : 0)}");
                sw.WriteLine($"FocusDepthActive: {focusActive} depth={focusDepth:F4} tol={focusTol:F4} tolWide={focusTolWide:F4}");
                sw.WriteLine($"ZoeBandActive: {bandActive} center={bandCenter:F4} halfWidth={bandHalfWidth:F4}");
                sw.WriteLine($"FrontGateActive: {frontGateActive} depthTol={depthToleranceNdc:F4} scale={depthScale:F4} bias={depthBias:F4}");
                sw.WriteLine($"ProbeClipActive: {probeClipActive} ndc={probeClipDepthNdc:F4} tolNdc={probeClipTolNdc:F5} metric={probeClipDepthMetric:F3} tolMeters={probeClipTolMeters:F3}");
                sw.WriteLine($"PerPixelDepthAvailable: {(perPixelDepth != null)}");
                if (positivePoints != null && positivePoints.Count > 0)
                {
                    sw.WriteLine("Samples:");
                    for (int i = 0; i < positivePoints.Count; ++i)
                    {
                        var pt = positivePoints[i];
                        int px = Mathf.Clamp(Mathf.RoundToInt(pt.x * (depthTex.width - 1)), 0, depthTex.width - 1);
                        int py = Mathf.Clamp(Mathf.RoundToInt((1f - pt.y) * (depthTex.height - 1)), 0, depthTex.height - 1);
                        float zoeSample = depthTex.GetPixel(px, py).r;
                        float ndcMin = 0f, ndcMax = 0f;
                        bool hasDepth = false;
                        if (pointSamples != null && pointSamples.Count > i)
                        {
                            hasDepth = pointSamples[i].hasDepth;
                            ndcMin = pointSamples[i].ndcMin;
                            ndcMax = pointSamples[i].ndcMax;
                        }
                        else
                        {
                            hasDepth = TrySamplePerPixelDepth(perPixelDepth, maskTex.width, maskTex.height, pt, out ndcMin, out ndcMax);
                        }
                        sw.WriteLine($"  pt=({pt.x:F3},{pt.y:F3}) zoe={zoeSample:F5} perPixel={(hasDepth ? ndcMin.ToString("F5") : "n/a")} ndcMax={(hasDepth ? ndcMax.ToString("F5") : "n/a")}");
                    }
                }
            }
            try
            {
                string maskPng = System.IO.Path.Combine(diagDir, $"diag_{stamp}_mask.png");
                string depthPng = System.IO.Path.Combine(diagDir, $"diag_{stamp}_zoe.png");
                System.IO.File.WriteAllBytes(maskPng, maskTex.EncodeToPNG());
                System.IO.File.WriteAllBytes(depthPng, depthTex.EncodeToPNG());
            }
            catch { }
            Debug.Log($"[SAM Lite] Depth diagnostics saved to {txtPath}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SAM Lite] Failed to write depth diagnostics: {ex.Message}");
        }
    }

    static bool TrySamplePerPixelDepth(ComputeBuffer perPixelDepth, int width, int height, Vector2 ptBL, out float ndcMin, out float ndcMax)
    {
        ndcMin = ndcMax = 0f;
        if (perPixelDepth == null)
            return false;
        int px = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(ptBL.x) * (width - 1)), 0, width - 1);
        int py = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(1f - ptBL.y) * (height - 1)), 0, height - 1);
        int pixelIndex = py * width + px;
        uint[] pair = new uint[2];
        try
        {
            perPixelDepth.GetData(pair, 0, pixelIndex * 2, 2);
        }
        catch
        {
            return false;
        }
        if (pair[0] == 0xFFFFFFFFu)
            return false;
        ndcMin = pair[0] / 65535f;
        uint maxQuant = pair[1] > pair[0] ? pair[1] : pair[0];
        ndcMax = maxQuant / 65535f;
        return true;
    }

    static bool TrySamplePerPixelDepthExpanded(ComputeBuffer perPixelDepth, int width, int height, Vector2 ptBL, int maxRadiusPx, out float ndcMin, out float ndcMax)
    {
        if (TrySamplePerPixelDepth(perPixelDepth, width, height, ptBL, out ndcMin, out ndcMax))
            return true;
        return TrySamplePerPixelDepthNeighborhood(perPixelDepth, width, height, ptBL, Math.Max(1, maxRadiusPx), out ndcMin, out ndcMax);
    }

    static bool TrySamplePerPixelDepthNeighborhood(ComputeBuffer perPixelDepth, int width, int height, Vector2 ptBL, int maxRadiusPx, out float ndcMin, out float ndcMax)
    {
        ndcMin = ndcMax = 0f;
        if (perPixelDepth == null)
            return false;
        maxRadiusPx = Mathf.Max(0, maxRadiusPx);
        int baseX = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(ptBL.x) * (width - 1)), 0, width - 1);
        int baseY = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(1f - ptBL.y) * (height - 1)), 0, height - 1);
        uint[] pair = new uint[2];
        for (int radius = 0; radius <= maxRadiusPx; radius++)
        {
            int minX = Mathf.Max(0, baseX - radius);
            int maxX = Mathf.Min(width - 1, baseX + radius);
            int minY = Mathf.Max(0, baseY - radius);
            int maxY = Mathf.Min(height - 1, baseY + radius);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int pixelIndex = y * width + x;
                    try
                    {
                        perPixelDepth.GetData(pair, 0, pixelIndex * 2, 2);
                    }
                    catch
                    {
                        continue;
                    }
                    if (pair[0] == 0xFFFFFFFFu)
                        continue;
                    ndcMin = pair[0] / 65535f;
                    uint maxQuant = pair[1] > pair[0] ? pair[1] : pair[0];
                    ndcMax = maxQuant / 65535f;
                    return true;
                }
            }
        }
        return false;
    }

    bool TryGetZoeDepthAtPoint(Texture2D depthTex, Vector2 pt, out float zoeDepth)
    {
        zoeDepth = 0f;
        if (!depthTex)
            return false;
        int px = Mathf.Clamp(Mathf.RoundToInt(pt.x * (depthTex.width - 1)), 0, depthTex.width - 1);
        int py = Mathf.Clamp(Mathf.RoundToInt((1f - pt.y) * (depthTex.height - 1)), 0, depthTex.height - 1);
        zoeDepth = depthTex.GetPixel(px, py).r;
        return true;
    }

    bool TryComputeZoeProbeData(Vector2 pt, float zoeDepthNorm, out float metricDepth, out float ndcDepth, out Vector3 worldPos)
    {
        metricDepth = 0f;
        ndcDepth = 0f;
        worldPos = Vector3.zero;
        if (!m_LastZoeDepthStatsValid)
            return false;
        if (!sourceCamera)
            return false;
        float clamped = Mathf.Clamp01(zoeDepthNorm);
        metricDepth = m_LastZoeDepthMin + clamped * m_LastZoeDepthRange;
        if (metricDepth <= 1e-4f)
            return false;
        m_ZoeProbeRay = sourceCamera.ViewportPointToRay(new Vector3(pt.x, pt.y, 0f));
        worldPos = m_ZoeProbeRay.GetPoint(metricDepth);
        ndcDepth = WorldToNdc01(worldPos);
        return true;
    }

    float WorldToNdc01(Vector3 world)
    {
        if (!sourceCamera)
            return 0.5f;
        Matrix4x4 view = sourceCamera.worldToCameraMatrix;
        Matrix4x4 proj = GL.GetGPUProjectionMatrix(sourceCamera.projectionMatrix, true);
        Vector4 clip = proj * (view * new Vector4(world.x, world.y, world.z, 1f));
        if (Mathf.Abs(clip.w) < 1e-5f)
            return 0.5f;
        float ndc = clip.z / clip.w;
        return ndc * 0.5f + 0.5f;
    }

    float ComputeProbeTolNdc(float metricDepth, float toleranceMeters)
    {
        if (toleranceMeters <= 0f || !sourceCamera)
            return 0f;
        if (m_ZoeProbeRay.direction == Vector3.zero)
            return 0f;
        Vector3 basePos = m_ZoeProbeRay.GetPoint(metricDepth);
        Vector3 farPos = m_ZoeProbeRay.GetPoint(metricDepth + toleranceMeters);
        float ndcBase = WorldToNdc01(basePos);
        float ndcFar = WorldToNdc01(farPos);
        return Mathf.Abs(ndcFar - ndcBase);
    }

    void CacheSeedWorlds(IList<Vector3> seeds)
    {
        m_LatestSeedWorlds.Clear();
        if (seeds == null)
            return;
        m_LatestSeedWorlds.AddRange(seeds);
    }

    bool TryProjectDepthToWorld(Camera cam, Vector2 ptBL, float ndcDepth, out Vector3 worldPos)
    {
        worldPos = Vector3.zero;
        if (!cam)
            return false;
        Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        Matrix4x4 view = cam.worldToCameraMatrix;
        Matrix4x4 invViewProj = (proj * view).inverse;
        float clipX = ptBL.x * 2f - 1f;
        float clipY = (1f - ptBL.y) * 2f - 1f;
        float clipZ = ndcDepth * 2f - 1f;
        Vector4 clip = new Vector4(clipX, clipY, clipZ, 1f);
        Vector4 worldH = invViewProj * clip;
        if (Mathf.Abs(worldH.w) < 1e-5f)
            return false;
        worldPos = new Vector3(worldH.x, worldH.y, worldH.z) / worldH.w;
        return true;
    }

    bool ApplyProbeDepthCullCPU(GraphicsBuffer selectedBits, GraphicsBuffer posBuffer, int splatCount, Camera cam, Vector3 centerWorld, float probeDepthNdc, float tolNdc)
    {
        if (selectedBits == null || posBuffer == null)
            return false;
        int wordCount = selectedBits.count;
        if (wordCount <= 0 || splatCount <= 0)
            return false;
        int vectorCount = Mathf.Min(posBuffer.count, splatCount);
        if (vectorCount <= 0)
            return false;
        uint[] bitData = new uint[wordCount];
        Vector3[] localPositions = new Vector3[vectorCount];
        try
        {
            selectedBits.GetData(bitData, 0, 0, wordCount);
            posBuffer.GetData(localPositions, 0, 0, vectorCount);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SAM Lite] Probe sphere CPU cull failed: {ex.Message}");
            return false;
        }
        var l2w = gs ? gs.transform.localToWorldMatrix : Matrix4x4.identity;
        Matrix4x4 view = cam.worldToCameraMatrix;
        Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        Matrix4x4 viewProj = proj * view;
        float clipThreshold = Mathf.Clamp01(probeDepthNdc + tolNdc) * 2f - 1f;
        bool modified = false;
        int maxIndex = Mathf.Min(localPositions.Length, splatCount);
        for (int i = 0; i < maxIndex; ++i)
        {
            int word = i >> 5;
            uint mask = 1u << (i & 31);
            if (word >= bitData.Length)
                break;
            if ((bitData[word] & mask) == 0)
                continue;
            Vector3 worldPos = l2w.MultiplyPoint3x4(localPositions[i]);
            Vector4 clip = viewProj * new Vector4(worldPos.x, worldPos.y, worldPos.z, 1f);
            if (Mathf.Abs(clip.w) < 1e-5f)
                continue;
            float ndcZ = clip.z / clip.w;
            if (ndcZ > clipThreshold)
            {
                bitData[word] &= ~mask;
                modified = true;
            }
        }
        if (modified)
        {
            selectedBits.SetData(bitData, 0, 0, wordCount);
        }
        return modified;
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

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !showSeedGizmos)
            return;
        if (m_LatestSeedWorlds == null || m_LatestSeedWorlds.Count == 0)
            return;
        Gizmos.color = Color.magenta;
        float radius = Mathf.Max(0.001f, seedGizmoRadius);
        foreach (var seed in m_LatestSeedWorlds)
            Gizmos.DrawSphere(seed, radius);
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

    [ContextMenu("Run Zoe Depth Probe")]
    void ContextRunZoeProbe()
    {
        TriggerZoeProbe();
    }

    void TriggerZoeProbe()
    {
        if (!Application.isPlaying)
            return;
        if (!requestDepth)
        {
            Debug.LogWarning("[SAM Lite] Zoe depth probe requires Request Depth to be enabled.");
            return;
        }
        if (positivePoints == null || positivePoints.Count == 0)
        {
            Debug.LogWarning("[SAM Lite] Add at least one positive point before running the Zoe depth probe.");
            return;
        }
        m_ZoeProbePending = true;
        RunSAMOnce();
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
