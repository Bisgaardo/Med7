using UnityEngine;
using UnityEngine.Rendering;
// Renders many 3D Gaussian splats stored in a buffer (simple diagonal covariance).
public class GaussianSplatRenderer : MonoBehaviour
{
    [Header("Basics")]
    public int  MaxSplats = 10000;
    public bool ShowIDMask = true;

    [Header("LOD")]
    [Tooltip("Skip splats whose projected footprint is below this pixel size.")]
    [Range(0f, 4f)] public float MinPixels = 1f;

    [Header("Shaders")]
    public ComputeShader projectCS;          // Assets/Shaders/SplatProject.compute
    public Shader         billboardShader;   // Custom/SplatBillboard (color pass)
    public Shader         idShader;          // Custom/SplatBillboardID (ID pass)

    [Header("Debug")]
    public bool debugHugeBounds   = true;    // draw with gigantic bounds (skip occlusion)
    public bool forceRedIDs       = false;   // tell ID shader to output solid red (prove write)
    public bool overlayIDToScreen = false;   // draw ID pass on the main camera target (visual proof)

    // Buffers/materials
    ComputeBuffer splatBuffer;               // Splat data
    ComputeBuffer visibleBuffer;             // Append indices of visible splats
    ComputeBuffer argsBuffer;                // [vertsPerInstance, instanceCount, startVtx, startInst]
    Material      billboardMat;
    Material      idMat;

    public Material material => billboardMat;

    struct Splat { public Vector3 pos; public Vector4 col; public Vector3 cov0,cov1,cov2; }

void OnEnable()
{
    // ---- Validate shader assignments so you get loud errors in Console
    if (billboardShader == null)
        Debug.LogError("[GaussianSplatRenderer] billboardShader is NOT assigned in the inspector.");
    if (idShader == null)
        Debug.LogError("[GaussianSplatRenderer] idShader is NOT assigned in the inspector (ID pass will NOT render).");

    // Allocate core buffers
    splatBuffer   = new ComputeBuffer(MaxSplats, 64);
    visibleBuffer = new ComputeBuffer(MaxSplats, sizeof(uint), ComputeBufferType.Append);

    // Indirect draw args: 6 verts per instance (two triangles), instanceCount written later by CopyCount
    argsBuffer    = new ComputeBuffer(1, sizeof(uint) * 4, ComputeBufferType.IndirectArguments);
    var args = new uint[4] { 6u, 0u, 0u, 0u };
    argsBuffer.SetData(args);

    // Demo points
    var data = new Splat[MaxSplats];
    var rnd  = new System.Random(0);
    for (int i = 0; i < data.Length; i++)
    {
        data[i].pos = new Vector3(
            (float)rnd.NextDouble() * 10f - 5f,
            (float)rnd.NextDouble() *  6f - 3f,
            (float)rnd.NextDouble() * 10f - 5f);
        data[i].col = new Color(
            (float)rnd.NextDouble(),
            (float)rnd.NextDouble(),
            (float)rnd.NextDouble(), 1f);
        float s = 0.10f;
        data[i].cov0 = new Vector3(s, 0, 0);
        data[i].cov1 = new Vector3(0, s, 0);
        data[i].cov2 = new Vector3(0, 0, s);
    }
    splatBuffer.SetData(data);

    // Materials
    billboardMat = billboardShader ? new Material(billboardShader) : null;
    if (billboardMat != null)
    {
        billboardMat.hideFlags = HideFlags.HideAndDontSave;
        billboardMat.SetBuffer("_SplatData", splatBuffer);
        billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);
    }

    idMat = idShader ? new Material(idShader) : null;
    if (idMat != null)
    {
        idMat.hideFlags = HideFlags.HideAndDontSave;
        idMat.SetBuffer("_SplatData", splatBuffer);
        idMat.SetBuffer("_VisibleIndices", visibleBuffer);
        idMat.SetInt("_DebugForceRed", 0);
    }

    if (idMat == null)
        Debug.LogError("[GaussianSplatRenderer] idMat could not be created. Assign Custom/SplatBillboardID to ID Shader.");
}


    void OnDisable()
    {
        splatBuffer?.Release();
        visibleBuffer?.Release();
        argsBuffer?.Release();
        if (billboardMat) DestroyImmediate(billboardMat);
        if (idMat)        DestroyImmediate(idMat);
    }

    // 1) Compute visibility + fill indirect args for this camera
public void UpdateVisibility(Camera cam)
{
    if (!projectCS || cam == null) return;

    visibleBuffer.SetCounterValue(0);

    int count = MaxSplats;
    Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;

    projectCS.SetInt("_Count", count);
    projectCS.SetMatrix("_VP", vp);
    projectCS.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
    projectCS.SetFloat("_MinPixels", MinPixels);
    projectCS.SetBuffer(0, "SplatData",      splatBuffer);
    projectCS.SetBuffer(0, "VisibleIndices", visibleBuffer);

    int groups = Mathf.CeilToInt(count / 64f);
    projectCS.Dispatch(0, groups, 1, 1);

    ComputeBuffer.CopyCount(visibleBuffer, argsBuffer, sizeof(uint));

    if (billboardMat != null)
    {
        billboardMat.SetMatrix("_VP", vp);
        billboardMat.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
        billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);
    }
if (idMat != null)
{
    idMat.SetMatrix("_VP", vp);
    idMat.SetInt("_DebugForceRed", forceRedIDs ? 1 : 0);
    idMat.SetBuffer("_SplatData",      splatBuffer);     // <-- ensure bound
    idMat.SetBuffer("_VisibleIndices", visibleBuffer);   // <-- ensure bound
}

    
}


    public void DrawColor(Camera cam)
    {
        if (cam == null || billboardMat == null) return;
        var bounds = new Bounds(Vector3.zero, Vector3.one * (debugHugeBounds ? 1_000_000f : 10_000f));
        Graphics.DrawProceduralIndirect(billboardMat, bounds, MeshTopology.Triangles, argsBuffer);
    }

public void DrawID(Camera cam, RenderTexture target)
{
    if (target == null || cam == null || idMat == null) return;

    UpdateVisibility(cam);
    if (!target.IsCreated()) target.Create();

    var cb = new CommandBuffer { name = "GaussianSplats: ID Pass" };
    cb.SetRenderTarget(target);
    cb.ClearRenderTarget(true, true, Color.black);

    // **Make viewport = full RT** so clip space maps correctly to the target
    cb.SetViewport(new Rect(0, 0, target.width, target.height));

    cb.DrawProceduralIndirect(
        Matrix4x4.identity, idMat, 0, MeshTopology.Triangles, argsBuffer);

    Graphics.ExecuteCommandBuffer(cb);
    cb.Release();
}




    // Visual proof: draw ID material over the main camera target
    public void DrawIDOverlay(Camera cam)
    {
        if (cam == null || idMat == null) return;
        var bounds = new Bounds(Vector3.zero, Vector3.one * (debugHugeBounds ? 1_000_000f : 10_000f));
        Graphics.DrawProceduralIndirect(idMat, bounds, MeshTopology.Triangles, argsBuffer);
    }

public void SetIDMask(
    ComputeBuffer buf, int count,
    Color? tint = null, float alphaBoost = 1.75f, float sizeScale = 1.4f,
    float dimOthers = 1.0f // 1 = no dim, <1 dims non-selected
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





    void Update()
    {
        var cam = Camera.main;
        UpdateVisibility(cam);
        DrawColor(cam);

        // Optional: draw ID pass over the scene to visually confirm per-splat colors
        if (overlayIDToScreen) DrawIDOverlay(cam);

        // Loud reminder if forcing RED
        if (forceRedIDs)
            Debug.LogWarning("[GaussianSplatRenderer] ForceRedIDs is ON — ID RT will be solid red dots.");
    }

    // Handy for logging
    public uint GetVisibleCount()
    {
        var tmp = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        ComputeBuffer.CopyCount(visibleBuffer, tmp, 0);
        var arr = new uint[1]; tmp.GetData(arr); tmp.Dispose();
        return arr[0];
    }
}
