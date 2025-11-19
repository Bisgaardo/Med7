// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using GaussianSplatting.Runtime;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Minimal SAM client that captures the camera, sends to sidecar /sam, and applies the mask.
// No k-means, no internal depth gating, no seed occlusion or per-pixel depth.
public class SAMBase : MonoBehaviour
{
    [Header("References")]
    public GaussianSplatRenderer gs;
    public ComputeShader maskSelectCS;
    public Camera sourceCamera;

    [Header("Capture")]
    [Range(64, 4096)] public int captureSize = 1024;

    [Header("Mask Apply")]
    [Range(0f,1f)] public float maskThreshold = 0.5f;
    public bool applySelection = true;

    [Header("Prompts (points)")]
    public List<Vector2> positivePoints = new List<Vector2>(); // normalized [0,1] in BL coordinates

    [Header("Local Python (required)")]
    [Tooltip("Absolute path to Python executable (venv)")]
    public string pythonExe = ""; // e.g., ...\sidecar\.venv\Scripts\python.exe
    [Tooltip("Absolute path to cli_sam.py")]
    public string pythonCli = ""; // e.g., ...\sidecar\cli_sam.py
    [Tooltip("Absolute path to SAM vit_h checkpoint .pth (optional; auto-detected if empty)")]
    public string samCheckpoint = "";
    [Tooltip("Model type for SAM (vit_h/vit_l/vit_b or mobile variant)")]
    public string samModel = "vit_h";
    [Header("Depth (ZoeDepth)")]
    [Tooltip("ZoeDepth variant (ZoeD_N / ZoeD_K / ZoeD_NK)")]
    public string zoeVariant = "ZoeD_N";
    [Tooltip("Local ZoeDepth repo root (optional)")]
    public string zoeRoot = ""; // e.g., ...\\ZoeDepth-main
    [Tooltip("Max image dimension sent to ZoeDepth (downscales to limit VRAM)")]
    [Range(128,2048)] public int zoeMaxDim = 512;
    [Tooltip("Additional NDC slack when comparing Zoe depth to splat depth.")]
    [Range(0f, 0.2f)] public float zoeDepthCullOffset = 0.01f;
    [Header("Zoe Depth Probe")]
    [Tooltip("Key that triggers a one-shot Zoe depth probe (highlights splats near Zoe depth at first positive point).")]
    public KeyCode zoeProbeHotkey = KeyCode.P;
    [Tooltip("How close (in Zoe depth units) a splat must be to the probe depth to stay highlighted.")]
    [Range(0.001f, 0.1f)] public float probeDepthTolerance = 0.02f;
    [Tooltip("Manual NDC slack for the CPU depth cull. Negative values push the clip plane forward.")]
    public float probeDepthPlaneSlackNdc = 0.02f;
    [Tooltip("When enabled, rely solely on Probe Depth Tolerance; disable to add the manual slack above (can be negative).")]
    public bool probeDepthPlaneUseAutoSlack = true;
    [Tooltip("Enable CPU depth plane culling after the probe runs.")]
    public bool useProbeDepthPlaneCull = true;
    [Header("Visualization")]
    [Tooltip("Draw the Zoe depth plane and clip band as gizmos in the Scene/Game views.")]
    public bool showProbeDepthPlane = true;
    [Tooltip("Color used for the base Zoe plane gizmo.")]
    public Color basePlaneColor = new Color(0.0f, 0.8f, 1.0f, 0.35f);
    [Tooltip("Color used for the clip plane gizmo (after tolerance).")]
    public Color clipPlaneColor = new Color(1.0f, 0.2f, 0.2f, 0.45f);
    System.Diagnostics.Process workerProc;
    System.IO.StreamWriter workerStdin;
    System.IO.StreamReader workerStdout;
    System.IO.StreamReader workerStderr;
    float m_ZoeProbeDepthValue = 0f;
    Vector3 m_ZoeProbeWorld = Vector3.zero;
    Ray m_ZoeProbeRay;
    float m_ProbeClipDepthNdc = 0f;
    float m_ProbeClipTolNdc = 0f;
    float m_ProbeForwardDepth = 0f;
    Texture2D m_LastMaskTex;
    Texture2D m_LastDepthTex;
    float m_LastZoeDepthCullOffset = -1f;
    bool m_ReapplyPending;
    float m_LastProbeDepthTolerance = -1f;
    Coroutine m_ProbeCullCoroutine;
    float m_LastProbeCullTolNdc = 0f;
    float m_LastProbeCullForwardDepth = 0f;

    static readonly FieldInfo s_ViewBufferField = typeof(GaussianSplatRenderer).GetField("m_GpuView", BindingFlags.NonPublic | BindingFlags.Instance);
    int kApply = -1;

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
            if (kApply < 0)
                kApply = maskSelectCS.FindKernel("ApplyMaskSelection");
        }
        StartPythonWorker();
        m_LastProbeDepthTolerance = probeDepthTolerance;
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

        // Enter runs SAM, probe hotkey reuses cached data for depth cull
        if (Input.GetKeyDown(KeyCode.Return))
        {
            RunSAMOnce();
        }
        else if (Input.GetKeyDown(zoeProbeHotkey))
        {
            RunProbeCull(logWarnings: true);
        }

        if (!m_ReapplyPending &&
            m_LastMaskTex != null &&
            m_LastDepthTex != null &&
            Mathf.Abs(m_LastZoeDepthCullOffset - zoeDepthCullOffset) > 1e-4f)
        {
            m_LastZoeDepthCullOffset = zoeDepthCullOffset;
            m_ReapplyPending = true;
            StartCoroutine(ReapplyCachedMask());
        }

        if (Mathf.Abs(m_LastProbeDepthTolerance - probeDepthTolerance) > 1e-4f)
        {
            m_LastProbeDepthTolerance = probeDepthTolerance;
            if (m_LastDepthTex && useProbeDepthPlaneCull)
            {
                RunProbeCull(logWarnings: false);
            }
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
            if (workerProc != null && !workerProc.HasExited && workerStdin != null && workerStdout != null)
            {
                string imageJson = inPath.Replace("\\", "/");
                string outJson = outPath.Replace("\\", "/");
                string pointsStr = sb.ToString();
                // Include a request id (token) so the worker echoes it back; avoids off-by-one frame usage
                string reqId = token;
                string depthJson = depthPath.Replace("\\", "/");
                string zroot = (zoeRoot ?? string.Empty).Replace("\\", "/");
            string payload = "{\"req\":\"" + reqId + "\",\"image\":\"" + imageJson + "\",\"points\":\"" + pointsStr + "\",\"out\":\"" + outJson + "\",\"depth_out\":\"" + depthJson + "\",\"zoe_variant\":\"" + (zoeVariant ?? "") + "\",\"zoe_root\":\"" + zroot + "\",\"zoe_device\":\"cuda\",\"zoe_max_dim\":" + zoeMaxDim + "}";
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
                                line = null;
                            }
                        }
                        System.Threading.Thread.Sleep(10);
                    }
                    swCall.Stop();
                    // Fallback: if no JSON came back, synthesize a minimal response for the known paths
                    if (string.IsNullOrEmpty(line))
                    {
                        line = "{\"req\":\"" + reqId + "\",\"out\":\"" + outJson + "\"}";
                    }
                    if (!string.IsNullOrEmpty(line) && !line.Contains("error"))
                    {
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
                Debug.LogError("[SAM Lite] Python worker did not respond; ensure StartPythonWorker succeeded.");
                yield break;
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
            // Zoe depth (always requested)
            Texture2D depthTex = null;
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
                    depthTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    depthTex.LoadImage(depthBytes, markNonReadable: false);
                    depthTex.filterMode = FilterMode.Point;
                    depthTex.wrapMode = TextureWrapMode.Clamp;
                    depthTex.Apply(false, false);
                }
                catch (Exception dex)
                {
                    Debug.LogWarning($"[SAM Lite] Failed to load depth PNG: {dex.Message}");
                    depthTex = null;
                }
            }
            else
            {
                Debug.Log($"[SAM Lite] Depth PNG not found or empty at: {depthPath}");
            }

            if (applySelection)
                yield return ApplyMaskToSelection(maskTex, depthTex);

            if (m_LastMaskTex) Destroy(m_LastMaskTex);
            m_LastMaskTex = maskTex;
            maskTex = null;
            if (depthTex)
            {
                if (m_LastDepthTex) Destroy(m_LastDepthTex);
                m_LastDepthTex = depthTex;
                depthTex = null;
                RunProbeCull(logWarnings: false);
            }
            m_LastZoeDepthCullOffset = zoeDepthCullOffset;

            try { if (System.IO.File.Exists(inPath)) System.IO.File.Delete(inPath); } catch { }
            try { if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath); } catch { }
            try { if (System.IO.File.Exists(depthPath)) System.IO.File.Delete(depthPath); } catch { }
            if (maskTex) Destroy(maskTex);
            yield break;
        }
    

    IEnumerator ApplyMaskToSelection(Texture2D maskTex, Texture2D depthTex)
    {
        if (!gs || !maskSelectCS) yield break;
        int splatCount = gs.splatCount;
        if (splatCount <= 0) yield break;

        gs.EditDeselectAll();
        var sel = gs.GpuEditSelected;
        if (sel == null)
        {
            Debug.LogWarning("[SAM Lite] Selection buffer unavailable; selection update skipped.");
            yield break;
        }

        var viewBuffer = s_ViewBufferField?.GetValue(gs) as GraphicsBuffer;
        var posBuffer = gs ? gs.GetPosBuffer() : null;
        if (viewBuffer == null || posBuffer == null)
        {
            Debug.LogWarning("[SAM Lite] Unable to access splat buffers; selection update skipped.");
            yield break;
        }

        maskSelectCS.SetBuffer(kApply, "_SplatViewData", viewBuffer);
        maskSelectCS.SetBuffer(kApply, "_SplatPos", posBuffer);
        maskSelectCS.SetTexture(kApply, "_MaskTex", maskTex);
        maskSelectCS.SetFloat("_DepthProbeActive", useProbeDepthPlaneCull ? 1f : 0f);
        bool depthValid = depthTex != null;
        maskSelectCS.SetTexture(kApply, "_DepthTex", depthValid ? depthTex : Texture2D.blackTexture);
        maskSelectCS.SetInt("_UseZoeDepthCull", depthValid ? 1 : 0);
        maskSelectCS.SetFloat("_ZoeDepthCullOffset", Mathf.Max(0f, zoeDepthCullOffset));
        maskSelectCS.SetInt("_SplatCount", splatCount);
        maskSelectCS.SetInts("_MaskSize", maskTex.width, maskTex.height);
        maskSelectCS.SetInt("_MaskPixelCount", maskTex.width * maskTex.height);
        int renderW = sourceCamera ? Mathf.Max(1, sourceCamera.pixelWidth) : Mathf.Max(1, Screen.width);
        int renderH = sourceCamera ? Mathf.Max(1, sourceCamera.pixelHeight) : Mathf.Max(1, Screen.height);
        maskSelectCS.SetFloats("_RenderViewportSize", renderW, renderH);
        maskSelectCS.SetFloat("_Threshold", Mathf.Clamp01(maskThreshold));
        maskSelectCS.SetInt("_Mode", 0);
        maskSelectCS.SetInt("_AlwaysIncludeMask", 0);
        maskSelectCS.SetInt("_CollectHistogram", 0);
        maskSelectCS.SetInt("_UseDepthGate", 0);
        maskSelectCS.SetInt("_UsePerPixelDepth", 0);
        maskSelectCS.SetInt("_ApplyPerPixelOcclusion", 0);
        maskSelectCS.SetFloat("_DepthOcclusionBias", 0f);
        maskSelectCS.SetMatrix("_ObjectToWorld", gs.transform.localToWorldMatrix);
        maskSelectCS.SetInt("_UseEllipseProbe", 0);
        maskSelectCS.SetInt("_UseCenterMask", 0);
        maskSelectCS.SetInt("_MaxRadiusPx", 0);
        maskSelectCS.SetInt("_MinRadiusPx", 0);
        maskSelectCS.SetFloat("_MaxEccentricity", 0f);
        maskSelectCS.SetFloat("_MaxAreaPx", 0f);
        maskSelectCS.SetInt("_UseProbeSphere", 0);
        maskSelectCS.SetFloats("_ProbeSphereCenter", 0f, 0f, 0f, 0f);
        maskSelectCS.SetFloat("_ProbeSphereRadius", 0f);
        maskSelectCS.SetInt("_MorphologyMode", 0);
        maskSelectCS.SetInt("_MorphRadiusPx", 0);
        maskSelectCS.SetInt("_UseBoxGate", 0);
        maskSelectCS.SetInt("_UseBoxGateBL", 0);
        maskSelectCS.SetFloats("_GateRectTLBR", 0f, 0f, 1f, 1f);
        maskSelectCS.SetFloats("_GateRectBL", 0f, 0f, 1f, 1f);
        maskSelectCS.SetInt("_UseROI", 0);
        maskSelectCS.SetFloats("_ROIMinMax", 0f, 0f, 1f, 1f);

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
            psi.EnvironmentVariables["SAM_DEVICE"] = "cuda";
            psi.EnvironmentVariables["SAM_DEVICE"] = "cuda";
            string ckpt = samCheckpoint;
            if (string.IsNullOrEmpty(ckpt))
            {
                string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string candidate = System.IO.Path.Combine(projectRoot, "sidecar", "weight", "sam_vit_h_4b8939.pth");
                if (System.IO.File.Exists(candidate)) ckpt = candidate;
            }
            if (!string.IsNullOrEmpty(ckpt)) psi.EnvironmentVariables["SAM_CHECKPOINT"] = ckpt;
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
                        if (!string.IsNullOrEmpty(line))
                            Debug.LogWarning("[SAM Lite][py-stderr] " + line);
                    }
                }
                catch { }
            });
            stderrThread.IsBackground = true;
            stderrThread.Start();

            // Warmup no longer generates temp files. Model load already happens on worker start.
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
        if (m_ProbeCullCoroutine != null)
        {
            StopCoroutine(m_ProbeCullCoroutine);
            m_ProbeCullCoroutine = null;
        }
        if (m_LastMaskTex) Destroy(m_LastMaskTex); m_LastMaskTex = null;
        if (m_LastDepthTex) { Destroy(m_LastDepthTex); m_LastDepthTex = null; }
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

    void RunProbeCull(bool logWarnings)
    {
        if (!Application.isPlaying || gs == null)
            return;
        if (!useProbeDepthPlaneCull)
            return;
        if (positivePoints == null || positivePoints.Count == 0)
        {
            if (logWarnings)
                Debug.LogWarning("[SAM Lite] Add at least one positive point before running the Zoe depth probe.");
            return;
        }
        if (m_LastDepthTex == null)
        {
            if (logWarnings)
                Debug.LogWarning("[SAM Lite] Run SAM with depth capture before probing.");
            return;
        }
        var probePt = positivePoints[0];
        if (!TryGetZoeDepthAtPoint(m_LastDepthTex, probePt, out m_ZoeProbeDepthValue))
        {
            if (logWarnings)
                Debug.LogWarning("[SAM Lite] Zoe depth probe failed: unable to sample Zoe depth at the positive point.");
            return;
        }
        if (!TryComputeZoeProbeData(probePt, m_ZoeProbeDepthValue, out float probeMetric, out float probeNdc, out Vector3 probeWorld))
        {
            if (logWarnings)
                Debug.LogWarning("[SAM Lite] Zoe depth probe failed: missing Zoe depth metadata from worker response.");
            return;
        }
        float clipTolNdc = ComputeProbeTolNdc(probeMetric, Mathf.Max(1e-4f, probeDepthTolerance));
        Debug.Log($"[SAM Lite] Zoe probe depth norm={m_ZoeProbeDepthValue:F3} metric={probeMetric:F2}m ndc={probeNdc:F3} tolNdc={clipTolNdc:F4}");

        m_ZoeProbeWorld = probeWorld;
        m_ProbeClipDepthNdc = probeNdc;
        m_ProbeClipTolNdc = clipTolNdc;
        UpdateProbePlaneDepths(m_ProbeClipDepthNdc, clipTolNdc);

        if (sourceCamera == null)
            return;

        var sel = gs.GpuEditSelected;
        var posBuffer = gs.GetPosBuffer();
        if (sel == null || posBuffer == null)
        {
            if (logWarnings)
                Debug.LogWarning("[SAM Lite] Probe cull skipped: selection or splat buffer unavailable.");
            return;
        }

        float planeTolNdc = clipTolNdc;
        if (!probeDepthPlaneUseAutoSlack)
            planeTolNdc += probeDepthPlaneSlackNdc;

        if (m_ProbeCullCoroutine != null)
        {
            StopCoroutine(m_ProbeCullCoroutine);
            m_ProbeCullCoroutine = null;
        }
        m_LastProbeCullTolNdc = planeTolNdc;
        UpdateProbePlaneDepths(m_ProbeClipDepthNdc, planeTolNdc);
        m_ProbeCullCoroutine = StartCoroutine(ApplyProbeDepthCullCPU(sel, posBuffer, gs.splatCount, sourceCamera, m_ZoeProbeWorld, m_ProbeClipDepthNdc, planeTolNdc));
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
        if (!sourceCamera)
            return false;
        float clamped = Mathf.Clamp01(zoeDepthNorm);
        metricDepth = Mathf.Lerp(0.5f, 8f, clamped);
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

    IEnumerator ApplyProbeDepthCullCPU(GraphicsBuffer selectedBits, GraphicsBuffer posBuffer, int splatCount, Camera cam, Vector3 centerWorld, float probeDepthNdc, float tolNdc)
    {
        try
        {
            if (selectedBits == null || posBuffer == null || cam == null)
                yield break;
            int wordCount = selectedBits.count;
            if (wordCount <= 0 || splatCount <= 0)
                yield break;
            int vectorCount = Mathf.Min(posBuffer.count, splatCount);
            if (vectorCount <= 0)
                yield break;
            uint[] bitData = new uint[wordCount];
            Vector3[] localPositions = new Vector3[vectorCount];
            try
            {
                selectedBits.GetData(bitData, 0, 0, wordCount);
                posBuffer.GetData(localPositions, 0, 0, vectorCount);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SAM Lite] Probe depth CPU cull failed: {ex.Message}");
                yield break;
            }
            Matrix4x4 l2w = gs ? gs.transform.localToWorldMatrix : Matrix4x4.identity;
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
                if ((i & 0xFFFF) == 0)
                    yield return null;
            }
            if (modified)
                selectedBits.SetData(bitData, 0, 0, wordCount);
            gs.UpdateEditCountsAndBounds();
            if (modified)
                Debug.Log($"[SAM Lite] Probe depth CPU cull removed splats behind ndc {probeDepthNdc:F4} tol {tolNdc:F5}.");
        }
        finally
        {
            m_ProbeCullCoroutine = null;
        }
    }

    void UpdateProbePlaneDepths(float baseNdc, float tolNdc)
    {
        TryGetForwardDepthFromNdc(baseNdc, out m_ProbeForwardDepth);
        float targetNdc = Mathf.Clamp01(baseNdc + tolNdc);
        TryGetForwardDepthFromNdc(targetNdc, out m_LastProbeCullForwardDepth);
    }

    void OnDrawGizmos()
    {
        if (!showProbeDepthPlane)
            return;
        if (!Application.isPlaying)
            return;
        if (!sourceCamera)
            return;
        if (m_ZoeProbeRay.direction == Vector3.zero)
            return;
        DrawProbePlaneGizmo(m_ProbeForwardDepth, basePlaneColor, 0.015f);
        DrawProbePlaneGizmo(m_LastProbeCullForwardDepth, clipPlaneColor, 0.02f);
    }

    void DrawProbePlaneGizmo(float forwardDepth, Color color, float borderOffset)
    {
        if (forwardDepth <= 0f)
            return;
        float distance = Mathf.Max(forwardDepth, sourceCamera.nearClipPlane + 0.001f);
        Vector3[] corners = s_GizmoCorners;
        sourceCamera.CalculateFrustumCorners(new Rect(0f, 0f, 1f, 1f), distance, Camera.MonoOrStereoscopicEye.Mono, corners);
        for (int i = 0; i < 4; ++i)
        {
            corners[i] = sourceCamera.transform.TransformPoint(corners[i]);
        }
        Color outline = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a));
#if UNITY_EDITOR
        Color fill = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.6f));
        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
        Handles.DrawSolidRectangleWithOutline(corners, fill, outline);
#else
        Gizmos.color = outline;
        Gizmos.DrawLine(corners[0], corners[1]);
        Gizmos.DrawLine(corners[1], corners[2]);
        Gizmos.DrawLine(corners[2], corners[3]);
        Gizmos.DrawLine(corners[3], corners[0]);
#endif

        // Draw probe sphere at positive point intersection if available
        if (positivePoints != null && positivePoints.Count > 0)
        {
            Vector2 vp = positivePoints[0];
            Vector3 planeHit = sourceCamera.ViewportToWorldPoint(new Vector3(vp.x, vp.y, distance));
            Gizmos.DrawWireSphere(planeHit, borderOffset);
        }
    }

    bool TryGetForwardDepthFromNdc(float ndcDepth01, out float forwardDepth)
    {
        forwardDepth = 0f;
        if (!sourceCamera)
            return false;
        Matrix4x4 proj = sourceCamera.projectionMatrix;
        Matrix4x4 invProj = proj.inverse;
        float clipZ = Mathf.Clamp(ndcDepth01 * 2f - 1f, -0.9999f, 0.9999f);
        Vector4 clip = new Vector4(0f, 0f, clipZ, 1f);
        Vector4 view = invProj * clip;
        if (Mathf.Abs(view.w) < 1e-6f)
            return false;
        view /= view.w;
        forwardDepth = Mathf.Abs(view.z);
        return forwardDepth > 0f;
    }

    static readonly Vector3[] s_GizmoCorners = new Vector3[4];


    IEnumerator ReapplyCachedMask()
    {
        if (m_LastMaskTex && m_LastDepthTex)
            yield return ApplyMaskToSelection(m_LastMaskTex, m_LastDepthTex);
        m_ReapplyPending = false;
    }
}
