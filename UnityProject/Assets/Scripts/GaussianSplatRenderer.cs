using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Renders many 3D Gaussian splats stored in a buffer.
public class GaussianSplatRenderer : MonoBehaviour
{
    [Header("Basics")]
    public int  MaxSplats = 10000;
    public bool ShowIDMask = true;

    [Header("LOD")]
    [Tooltip("Skip splats whose projected footprint is below this pixel size.")]
    [Range(0f, 4f)] public float MinPixels = 1f;

    [Header("Auto Size (screen-space)")]
    [Tooltip("Keep splats readable without manually tweaking sigma per scene.")]
    public bool AutoSize = true;
    [Range(0.5f, 6f)] public float TargetPixelRadius = 2f;   // desired half-width in pixels
    [Range(0.25f, 4f)] public float GlobalSizeScale = 1f;    // manual multiplier if AutoSize is off

    [Header("Shaders")]
    public ComputeShader projectCS;          // Assets/Shaders/SplatProject.compute
    public Shader         billboardShader;   // Custom/SplatBillboard* (color pass)
    public Shader         idShader;          // Custom/SplatBillboardID (ID pass) — optional

    [Header("Debug")]
    public bool debugHugeBounds   = true;    // draw with gigantic bounds (skip occlusion)
    public bool forceRedIDs       = false;   // tell ID shader to output solid red (prove write)
    public bool overlayIDToScreen = false;   // draw ID pass on the main camera target (visual proof)

    [Header("Debug / Bypass")]
    public bool bypassCulling = true;        // turn ON to ignore compute culling
    uint[] _seqIndices;                      // 0..N-1 for direct instancing

    // smooth global scale (for Auto Size)
    float _scaleSmoothed = -1f;

    // Buffers/materials
    ComputeBuffer splatBuffer;               // Splat data
    ComputeBuffer visibleBuffer;             // Indices of visible splats
    ComputeBuffer argsBuffer;                // [vertsPerInstance, instanceCount, startVtx, startInst]
    Material      billboardMat;
    Material      idMat;

    public Material material => billboardMat;
    public bool generateDemoCloudOnEnable = false;   // leave OFF to use baked data only
    Camera cam;
Shader _lastBillboardShader;
Shader _lastIDShader;
    // Hotkeys
    #if ENABLE_INPUT_SYSTEM
    InputActionMap _hotkeyMap;
    InputAction _bakeAction;     // F9
    InputAction _debugLogAction; // F8
    #endif

    [Header("Debug Anchors")]
    public bool debugDrawAnchors = true;
    [Range(1,128)] public int debugAnchorCount = 32;
    public float debugAnchorSize = 0.02f;
    List<Vector3> _debugAnchors = new List<Vector3>();

    struct Splat { public Vector3 pos; public Vector4 col; public Vector3 cov0, cov1, cov2; }


// [ADD] Rebuild materials if the assigned shader changed (or if mats are null)
void EnsureMaterials()
{
    // Billboard mat
    if (billboardMat == null || billboardShader != _lastBillboardShader)
    {
        if (billboardMat) DestroyImmediate(billboardMat);
        if (billboardShader != null)
        {
            billboardMat = new Material(billboardShader) { hideFlags = HideFlags.HideAndDontSave };
            if (splatBuffer != null) billboardMat.SetBuffer("_SplatData", splatBuffer);
            if (visibleBuffer != null) billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        }
        _lastBillboardShader = billboardShader;
    }

    // ID mat (optional)
    if (idMat == null || idShader != _lastIDShader)
    {
        if (idMat) DestroyImmediate(idMat);
        if (idShader != null)
        {
            idMat = new Material(idShader) { hideFlags = HideFlags.HideAndDontSave };
            if (splatBuffer != null) idMat.SetBuffer("_SplatData", splatBuffer);
            if (visibleBuffer != null) idMat.SetBuffer("_VisibleIndices", visibleBuffer);
        }
        _lastIDShader = idShader;
    }
}

    void OnEnable()
    {
        cam = Camera.main;

        // create visibility & args buffers
        int cap = Mathf.Max(1, MaxSplats);
        visibleBuffer = new ComputeBuffer(cap, sizeof(uint), ComputeBufferType.Append);
        argsBuffer    = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(new uint[5] { 6, 0, 0, 0, 0 }); // 6 verts / instance
        // [ADD]
        EnsureMaterials();

        // optional demo cloud
        if (splatBuffer == null && generateDemoCloudOnEnable)
        {
            var rnd = new System.Random(0);
            var data = new Splat[cap];
            for (int i = 0; i < cap; i++)
            {
                data[i].pos = new Vector3(
                    (float)rnd.NextDouble() * 2f - 1f,
                    (float)rnd.NextDouble() * 2f - 1f,
                    (float)rnd.NextDouble() * 2f - 1f) * 2f;

                data[i].col = new Color(
                    (float)rnd.NextDouble(),
                    (float)rnd.NextDouble(),
                    (float)rnd.NextDouble(), 1f);

                float s = 0.03f + 0.02f * (float)rnd.NextDouble();
                data[i].cov0 = new Vector3(s, 0, 0);
                data[i].cov1 = new Vector3(0, s, 0);
                data[i].cov2 = new Vector3(0, 0, s);
            }
            splatBuffer = new ComputeBuffer(cap, 64);
            splatBuffer.SetData(data);
        }

        if (billboardMat == null && billboardShader != null)
            billboardMat = new Material(billboardShader) { hideFlags = HideFlags.HideAndDontSave };

        if (billboardMat != null)
        {
            if (splatBuffer != null) billboardMat.SetBuffer("_SplatData", splatBuffer);
            billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
            billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);
            billboardMat.SetFloat("_GlobalScale", 1f);
        }

        // Build hotkeys (once)
        #if ENABLE_INPUT_SYSTEM
        if (_hotkeyMap == null)
        {
            _hotkeyMap      = new InputActionMap("GSRendererHotkeys");
            _bakeAction     = _hotkeyMap.AddAction("Bake",       binding: "<Keyboard>/f9");
            _debugLogAction = _hotkeyMap.AddAction("LogAnchors", binding: "<Keyboard>/f8");
            _bakeAction.performed     += _ => DoHotkeyBake();
            _debugLogAction.performed += _ => DebugLogAnchors();
            _hotkeyMap.Enable();
        }
        #endif
    }
// [ADD] anywhere in the class
void OnValidate()
{
    // Recreate materials when the shader fields change in the inspector
    EnsureMaterials();
}

    void OnDisable()
    {
        splatBuffer?.Release();
        visibleBuffer?.Release();
        argsBuffer?.Release();
        if (billboardMat) DestroyImmediate(billboardMat);
        if (idMat)        DestroyImmediate(idMat);

        #if ENABLE_INPUT_SYSTEM
        if (_hotkeyMap != null)
        {
            _hotkeyMap.Disable();
            if (_bakeAction != null)     _bakeAction.performed     -= _ => DoHotkeyBake();
            if (_debugLogAction != null) _debugLogAction.performed -= _ => DebugLogAnchors();
            _bakeAction?.Dispose();
            _debugLogAction?.Dispose();
            _hotkeyMap?.Dispose();
            _bakeAction = null;
            _debugLogAction = null;
            _hotkeyMap = null;
        }
        #endif
    }

    public void LoadSplatsFromList(List<MeshToGaussianSplats.Splat> list)
    {
        generateDemoCloudOnEnable = false;
        MaxSplats = Mathf.Max(1, list?.Count ?? 0);

        splatBuffer?.Release();
        splatBuffer = new ComputeBuffer(MaxSplats, 64);
        if (list != null && list.Count > 0) splatBuffer.SetData(list);

        EnsureVisibleBufferCapacity(MaxSplats);

        if (billboardMat != null)
            billboardMat.SetBuffer("_SplatData", splatBuffer);

        // capture anchors
        _debugAnchors.Clear();
        if (list != null && list.Count > 0)
        {
            int step = Mathf.Max(1, list.Count / Mathf.Max(1, debugAnchorCount));
            for (int i = 0; i < list.Count && _debugAnchors.Count < debugAnchorCount; i += step)
                _debugAnchors.Add(list[i].pos);
        }

        // 0..N-1 list for bypass
        if (MaxSplats > 0)
        {
            if (_seqIndices == null || _seqIndices.Length != MaxSplats)
            {
                _seqIndices = new uint[MaxSplats];
                for (uint i = 0; i < _seqIndices.Length; i++) _seqIndices[i] = i;
            }
        }
    }

    void EnsureVisibleBufferCapacity(int needed)
    {
        if (needed < 1) needed = 1;
        if (visibleBuffer == null || visibleBuffer.count < needed)
        {
            visibleBuffer?.Release();
            visibleBuffer = new ComputeBuffer(needed, sizeof(uint), ComputeBufferType.Append);
            if (billboardMat != null) billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        }
    }

    // ----- AutoSize helpers -----
    float MetersPerPixel(Camera c, float depthMeters)
    {
        float tanHalfFov = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float frustumHeightAtZ = 2f * depthMeters * tanHalfFov;
        return frustumHeightAtZ / Mathf.Max(1, Screen.height);
    }

    float ComputeAutoScale(Camera c)
    {
        float depth = Vector3.Distance(c.transform.position, transform.position);
        depth = Mathf.Max(0.05f, depth);
        const float referenceSigmaMeters = 0.01f; // matches baker's σ_tangent hint
        float mpp = MetersPerPixel(c, depth);
        float desiredMeters = TargetPixelRadius * mpp;
        float scale = desiredMeters / referenceSigmaMeters;
        return Mathf.Clamp(scale, 0.1f, 10f);
    }
    // ----------------------------

    // ===== Visibility/update (bypass or compute) =====
    void UpdateVisibility(Camera cam)
    {
        // [ADD]
EnsureMaterials();

        if (splatBuffer == null || billboardMat == null)
        {
            argsBuffer.SetData(new uint[5] { 6, 0, 0, 0, 0 });
            return;
        }

        // GPU-correct VP (backbuffer path)
        var gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
        var VP = gpuProj * cam.worldToCameraMatrix;

        EnsureVisibleBufferCapacity(MaxSplats);

        // Common material params
        billboardMat.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
        billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        float target = AutoSize ? ComputeAutoScale(cam) : Mathf.Max(0.01f, GlobalSizeScale);
        if (_scaleSmoothed < 0f) _scaleSmoothed = target;
        _scaleSmoothed = Mathf.Lerp(_scaleSmoothed, target, 0.15f); // 0.1–0.2 feels good
        billboardMat.SetFloat("_GlobalScale", _scaleSmoothed);

        // --- BYPASS: draw all splats (skip compute) ---
        if (bypassCulling)
        {
            if (_seqIndices == null || _seqIndices.Length != MaxSplats)
            {
                _seqIndices = new uint[MaxSplats];
                for (uint i = 0; i < _seqIndices.Length; i++) _seqIndices[i] = i;
            }
            visibleBuffer.SetData(_seqIndices);
            argsBuffer.SetData(new uint[5] { 6u, (uint)MaxSplats, 0u, 0u, 0u });
            return;
        }

        // --- Compute culling path ---
        if (projectCS == null)
        {
            // safe fallback: draw all
            if (_seqIndices == null || _seqIndices.Length != MaxSplats)
            {
                _seqIndices = new uint[MaxSplats];
                for (uint i = 0; i < _seqIndices.Length; i++) _seqIndices[i] = i;
            }
            visibleBuffer.SetData(_seqIndices);
            argsBuffer.SetData(new uint[5] { 6u, (uint)MaxSplats, 0u, 0u, 0u });
            return;
        }

        visibleBuffer.SetCounterValue(0);
        projectCS.SetInt("_Count", MaxSplats);
        projectCS.SetMatrix("_VP", VP);
        projectCS.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
        projectCS.SetFloat("_MinPixels", Mathf.Max(0f, MinPixels));
        projectCS.SetFloat("_GlobalScale", _scaleSmoothed);
        projectCS.SetBuffer(0, "SplatData", splatBuffer);
        projectCS.SetBuffer(0, "VisibleIndices", visibleBuffer);

        int groups = Mathf.CeilToInt(MaxSplats / 64f);
        projectCS.Dispatch(0, groups, 1, 1);

        ComputeBuffer.CopyCount(visibleBuffer, argsBuffer, sizeof(uint));
    }

    // ===== Rendering =====
    public void DrawColor(Camera cam)
    {
        UpdateVisibility(cam);
        if (splatBuffer == null) return;

        billboardMat.DisableKeyword("_IDPASS");
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);

        var bounds = new Bounds(Vector3.zero, Vector3.one * (debugHugeBounds ? 1_000_000f : 10_000f));
        Graphics.DrawProceduralIndirect(billboardMat, bounds, MeshTopology.Triangles, argsBuffer);
    }

    public void DrawID(Camera cam, RenderTexture idRT)
    {
        if (splatBuffer == null) return;

        UpdateVisibility(cam);

        var old = RenderTexture.active;
        RenderTexture.active = idRT;
        GL.Clear(true, true, Color.black);

        billboardMat.EnableKeyword("_IDPASS");
        var bounds = new Bounds(Vector3.zero, Vector3.one * (debugHugeBounds ? 1_000_000f : 10_000f));
        Graphics.DrawProceduralIndirect(billboardMat, bounds, MeshTopology.Triangles, argsBuffer);
        billboardMat.DisableKeyword("_IDPASS");

        RenderTexture.active = old;
    }

    public void DrawIDOverlay(Camera cam)
    {
        if (cam == null || idMat == null) return;
        var bounds = new Bounds(Vector3.zero, Vector3.one * (debugHugeBounds ? 1_000_000f : 10_000f));
        Graphics.DrawProceduralIndirect(idMat, bounds, MeshTopology.Triangles, argsBuffer);
    }

    public void SetIDMask(
        ComputeBuffer buf, int count,
        Color? tint = null, float alphaBoost = 1.75f, float sizeScale = 1.4f,
        float dimOthers = 1.0f
    )
    {
        if (billboardMat == null) return;

        billboardMat.SetBuffer("_IDMask", buf);
        billboardMat.SetInt("_IDMaskCount", count);
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);

        var t = tint ?? new Color(1f, 0.95f, 0.15f, 1f);
        billboardMat.SetColor("_SelectTint",  t);
        billboardMat.SetFloat("_SelectBoost", alphaBoost);
        billboardMat.SetFloat("_SelectScale", sizeScale);
        billboardMat.SetFloat("_DimOthers",   Mathf.Clamp01(dimOthers));
    }

    // Simple auto-render for Main Camera (optional; URP feature can call Render())
    void Update()
    {
        if (!generateDemoCloudOnEnable && splatBuffer == null) return;
        var camNow = Camera.main;
        if (camNow == null) return;
        DrawColor(camNow);
    }

    public void Render(Camera cam) => DrawColor(cam);

    public uint GetVisibleCount()
    {
        var tmp = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        ComputeBuffer.CopyCount(visibleBuffer, tmp, 0);
        var arr = new uint[1]; tmp.GetData(arr); tmp.Dispose();
        return arr[0];
    }

    #if ENABLE_INPUT_SYSTEM
    void DoHotkeyBake()
    {
        var bakers = FindObjectsByType<MeshToGaussianSplats>(FindObjectsSortMode.None);
        int count = 0;
        foreach (var b in bakers)
        {
            if (b != null && b.targetRenderer == this)
            {
                b.BakeToRenderer(this);
                count++;
            }
        }
        Debug.Log($"[GaussianSplatRenderer] F9 bake: {count} baker(s) updated for '{name}'.");
    }
    #endif

    void DebugLogAnchors()
    {
        if (_debugAnchors == null || _debugAnchors.Count == 0)
        {
            Debug.Log("[GS] No anchors captured yet. Bake (F9) first.");
            return;
        }
        int n = Mathf.Min(_debugAnchors.Count, 8);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("[GS] Anchor positions (world): ");
        for (int i = 0; i < n; i++)
        {
            var p = _debugAnchors[i];
            sb.Append($"[{i}]({p.x:F3},{p.y:F3},{p.z:F3}) ");
        }
        Debug.Log(sb.ToString());
    }

    void OnDrawGizmos()
    {
        if (!debugDrawAnchors || _debugAnchors == null) return;
        Gizmos.color = Color.cyan;
        foreach (var p in _debugAnchors)
            Gizmos.DrawSphere(p, debugAnchorSize);
    }
}
