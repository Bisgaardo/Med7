using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class SplatSegmentationDemo : MonoBehaviour
{
    public GaussianSplatRenderer splatRenderer;   // rename from 'renderer'
    [Range(128, 1024)] public int pickRTSize = 512;

    [Header("Selection")]
    public bool accumulateSelection = false;   // if false, every drag clears selection

    [Header("Debug")]
    public bool showIDPreview = true;
    public bool debugFillIdRT = false;

    RenderTexture idRT;
    Texture2D readTex;
    HashSet<uint> selected = new();
    ComputeBuffer idMask;


    [SerializeField] bool focusLock = false;        // press F to keep dim on until toggled


    Vector2 dragStart, dragEnd;
    bool    dragging;

void Start()
{
    // --- ID RT: force LINEAR (no sRGB), unorm8 ---
    var desc = new RenderTextureDescriptor(pickRTSize, pickRTSize)
    {
        graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
        depthBufferBits = 24,
        sRGB = false,
        msaaSamples = 1,
        mipCount = 1,
        useMipMap = false
    };
    idRT = new RenderTexture(desc);
    idRT.name = "SplatID_RT";
    idRT.Create();

    // CPU readback texture (linear)
    readTex = new Texture2D(pickRTSize, pickRTSize, TextureFormat.RGBA32, false, true);

    // Mask buffer
    idMask = new ComputeBuffer(4096, sizeof(uint));

    // Hook up mask (count=0 initially)
    splatRenderer.SetIDMask(idMask, 0);
}



    void OnDestroy()
    {
        if (idRT) idRT.Release();
        if (readTex) Destroy(readTex);
        idMask?.Dispose();
    }

// Applies current focus/dim immediately, without waiting for another drag
void ApplyFocusDim(Camera cam)
{
    float dimOthers = focusLock ? 0.35f : 1.0f;

    int count = Mathf.Min(selected.Count, idMask.count);
    splatRenderer.SetIDMask(idMask, count, null, 1.75f, 1.40f, dimOthers);
    splatRenderer.DrawColor(cam);
}



void Update()
{
    Camera cam = Camera.main;
    if (!cam) return;

    // ---------- Hotkeys ----------
#if ENABLE_INPUT_SYSTEM
    var kb = Keyboard.current;
    if (kb != null)
    {
        if (kb.escapeKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame)
        {
            selected.Clear();
            splatRenderer.SetIDMask(idMask, 0, null, 1.75f, 1.40f, 1f);
            splatRenderer.DrawColor(cam);
            Debug.Log("Selection cleared.");
        }
        if (kb.eKey.wasPressedThisFrame) ExportSelectedIdsCSV(selected);
        if (kb.fKey.wasPressedThisFrame) { focusLock = !focusLock; ApplyFocusDim(cam); }
    }

    var mouse = Mouse.current;
    if (mouse == null) return;
    bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
    bool ctrl  = kb != null && (kb.leftCtrlKey.isPressed  || kb.rightCtrlKey.isPressed
                             || kb.leftMetaKey.isPressed  || kb.rightMetaKey.isPressed);
    bool addMode    = accumulateSelection || shift;
    bool removeMode = ctrl;

    if (mouse.leftButton.wasPressedThisFrame)
    {
        if (!addMode && !removeMode) selected.Clear();
        dragStart = mouse.position.ReadValue();
        dragging  = true;
    }
    if (dragging) dragEnd = mouse.position.ReadValue();

    if (mouse.leftButton.wasReleasedThisFrame && dragging)
    {
        dragging = false;
        Vector2 end = dragEnd;
#else
    if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.C))
    {
        selected.Clear();
        splatRenderer.SetIDMask(idMask, 0, null, 1.75f, 1.40f, 1f);
        splatRenderer.DrawColor(cam);
        Debug.Log("Selection cleared.");
    }
    if (Input.GetKeyDown(KeyCode.E)) ExportSelectedIdsCSV(selected);
    if (Input.GetKeyDown(KeyCode.F)) { focusLock = !focusLock; ApplyFocusDim(cam); }

    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                 Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
    bool addMode    = accumulateSelection || shift;
    bool removeMode = ctrl;

    if (Input.GetMouseButtonDown(0))
    {
        if (!addMode && !removeMode) selected.Clear();
        dragStart = Input.mousePosition;
        dragging  = true;
    }
    if (dragging) dragEnd = Input.mousePosition;

    if (Input.GetMouseButtonUp(0) && dragging)
    {
        dragging = false;
        Vector2 end = dragEnd;
#endif
        // Click vs drag handling …

        // 1) ID pass
        if (debugFillIdRT)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = idRT; GL.Clear(true, true, Color.white); RenderTexture.active = prev;
        }
        else
        {
            splatRenderer.DrawID(cam, idRT);
        }

        // (rest of your method unchanged; after collecting IDs:)
        int count = Mathf.Min(selected.Count, idMask.count);
        var arr = new uint[count];
        int i = 0; foreach (var v in selected) { if (i >= count) break; arr[i++] = v; }
        idMask.SetData(arr);

        ApplyFocusDim(cam);
    }
}




    // On-screen selection box + optional ID RT preview
    void OnGUI()
    {
        if (showIDPreview && idRT != null)
        {
            const int size = 200;
            GUI.Box(new Rect(10, 10, size + 4, size + 24), "ID Preview");
            GUI.DrawTexture(new Rect(12, 32, size, size), idRT, ScaleMode.StretchToFill, false);
        }

        if (!dragging) return;

        float xMin = Mathf.Min(dragStart.x, dragEnd.x);
        float xMax = Mathf.Max(dragStart.x, dragEnd.x);
        float yMin = Mathf.Min(dragStart.y, dragEnd.y);
        float yMax = Mathf.Max(dragStart.y, dragEnd.y);
        Rect rGUI = new Rect(xMin, Screen.height - yMax, xMax - xMin, yMax - yMin);

        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 0f, 0.15f);
        GUI.Box(rGUI, GUIContent.none);
        GUI.color = new Color(1f, 1f, 0f, 0.9f);
        GUI.DrawTexture(new Rect(rGUI.xMin, rGUI.yMin, rGUI.width, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rGUI.xMin, rGUI.yMax - 2, rGUI.width, 2), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rGUI.xMin, rGUI.yMin, 2, rGUI.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rGUI.xMax - 2, rGUI.yMin, 2, rGUI.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    // CSV export to Assets/Generated/selected_ids.csv (Editor-friendly)
    void ExportSelectedIdsCSV(HashSet<uint> set)
    {
        try
        {
            string dir = Path.Combine(Application.dataPath, "Generated");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "selected_ids.csv");

            using (var w = new StreamWriter(path, false))
            {
                w.WriteLine("id");
                foreach (var id in set) w.WriteLine(id.ToString());
            }
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
            Debug.Log($"Exported {set.Count} IDs → {path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Export failed: " + ex.Message);
        }
    }
}

