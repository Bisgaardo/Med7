using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using GaussianSplatting.Runtime;

public class SplatRuntimeSelector : MonoBehaviour
{
    public GaussianSplatRenderer gs;
    public Camera cam;

    bool dragging;
    Vector2 dragStart, dragNow;

    void Awake()
    {
        if (!cam) cam = Camera.main;

        if (gs != null && gs.HasValidAsset && gs.HasValidRenderSetup)
        {
            // Ensure edit buffers exist
            gs.EditStoreSelectionMouseDown();
            gs.EditUpdateSelection(Vector2.zero, Vector2.zero, cam, false);
            gs.UpdateEditCountsAndBounds();
        }
    }

    void Update()
    {
if (Input.GetMouseButtonDown(0)) {
    dragging = true;
    dragStart = Input.mousePosition;
    gs.EditStoreSelectionMouseDown();
}

if (dragging) {
    dragNow = Input.mousePosition;
    var min = Vector2.Min(dragStart, dragNow);
    var max = Vector2.Max(dragStart, dragNow);
    bool subtract = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    gs.EditUpdateSelection(min, max, cam, subtract);
    gs.UpdateEditCountsAndBounds();
}

if (Input.GetMouseButtonUp(0)) {
    dragging = false;
}

    }

    IEnumerator DebugReadSelectedOnce()
    {
        var buf = gs.GpuEditSelected;
        if (buf == null) yield break;

        var req = AsyncGPUReadback.Request(buf);
        yield return new WaitUntil(() => req.done);
        if (req.hasError) yield break;

        // Count bits (optional debug)
        /*
        var data = req.GetData<uint>();
        int totalSet = 0;
        foreach (var word in data)
        {
            uint w = word;
            while (w != 0) { w &= (w - 1); totalSet++; }
        }
        Debug.Log($"Selected splats: {totalSet}");
        */
    }

    void OnGUI()
    {
        if (dragging)
        {
            var rect = GetScreenRect(dragStart, dragNow);
            DrawScreenRect(rect, new Color(0.2f, 0.5f, 1f, 0.25f));
            DrawScreenRectBorder(rect, 2, new Color(0.2f, 0.5f, 1f));
        }
    }

    Rect GetScreenRect(Vector2 start, Vector2 end)
    {
        start.y = Screen.height - start.y;
        end.y = Screen.height - end.y;
        Vector2 topLeft = Vector2.Min(start, end);
        Vector2 bottomRight = Vector2.Max(start, end);
        return Rect.MinMaxRect(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y);
    }

    void DrawScreenRect(Rect rect, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void DrawScreenRectBorder(Rect rect, float thickness, Color color)
    {
        DrawScreenRect(new Rect(rect.xMin, rect.yMin, rect.width, thickness), color);                 // top
        DrawScreenRect(new Rect(rect.xMin, rect.yMin, thickness, rect.height), color);                // left
        DrawScreenRect(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), color);   // right
        DrawScreenRect(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), color);    // bottom
    }
}
