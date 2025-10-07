using System;
using UnityEngine;

public class ClickableObject : MonoBehaviour
{
    [Header("Identity")]
    public string objectID = "";      // unique id (set in inspector or by spawner)
    public string shapeType = "";     // "Circle", "Square", "Capsule"

    [Header("Appearance")]
    public Color highlightColor = Color.yellow;
    private Color defaultColor = Color.white;

    [HideInInspector] public bool isActive = false;

    private Renderer rend;

    // Event fired when this object has been clicked and metrics computed
    public static event Action<ClickableObject, ClickMetrics> OnClickedStatic;

    void Awake()
    {
        rend = GetComponent<Renderer>();
        if (rend == null)
            Debug.LogError($"ClickableObject on '{name}' needs a Renderer component.");

        // Create a per-instance material so we can change color per object safely.
        if (rend != null && rend.sharedMaterial != null)
        {
            rend.material = new Material(rend.sharedMaterial);
            // read default color (URP uses _BaseColor)
            if (rend.material.HasProperty("_BaseColor"))
                defaultColor = rend.material.GetColor("_BaseColor");
            else
                defaultColor = rend.material.color;
        }
    }

    /// <summary>
    /// Called by manager to highlight and start timing.
    /// </summary>
    public void Activate()
    {
        isActive = true;
        SetColor(highlightColor);
    }

    /// <summary>
    /// Called by manager to remove highlight.
    /// </summary>
    public void Deactivate()
    {
        isActive = false;
        SetColor(defaultColor);
    }

    private void SetColor(Color c)
    {
        if (rend == null) return;
        if (rend.material == null) return;

        if (rend.material.HasProperty("_BaseColor"))
            rend.material.SetColor("_BaseColor", c);
        else
            rend.material.color = c;
    }

    /// <summary>
    /// Called by manager when a click is detected on this object.
    /// Manager passes the screen click position and the time since activation (seconds).
    /// This method computes offsets and other metrics, then raises an event.
    /// </summary>
    public void RegisterClick(Vector2 screenClickPos, float timeSinceActivationSeconds, Camera cam)
    {
        ClickMetrics m = ComputeMetrics(screenClickPos, timeSinceActivationSeconds, cam);
        // raise static event so the manager/logger can subscribe
        OnClickedStatic?.Invoke(this, m);
    }

    private ClickMetrics ComputeMetrics(Vector2 screenClickPos, float timeSinceActivationSeconds, Camera cam)
    {
        var metrics = new ClickMetrics();

        metrics.objectID = string.IsNullOrEmpty(objectID) ? name : objectID;
        metrics.shapeType = shapeType;
        metrics.timeMs = timeSinceActivationSeconds * 1000f;
        metrics.screenClick = screenClickPos;

        if (cam == null)
            cam = Camera.main;

        // world center position (approx)
        Vector3 worldCenter = transform.position;
        metrics.worldCenter = worldCenter;

        // distance camera -> object center
        metrics.distanceToCamera = cam != null ? Vector3.Distance(cam.transform.position, worldCenter) : 0f;

        // compute center in screen space
        Vector3 centerScreen = cam.WorldToScreenPoint(worldCenter);
        Vector2 centerScreen2 = new Vector2(centerScreen.x, centerScreen.y);

        // offset in pixels
        metrics.offsetPx = Vector2.Distance(centerScreen2, screenClickPos);

        // approximate target "radius" in pixels using renderer bounds extents
        float radiusPx = 0f;
        if (rend != null)
        {
            Bounds b = rend.bounds;
            // pick the largest horizontal extent as radius (world units)
            float worldRadius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
            Vector3 worldEdge = worldCenter + transform.right * worldRadius;
            Vector3 screenEdge = cam.WorldToScreenPoint(worldEdge);
            radiusPx = Vector2.Distance(centerScreen2, new Vector2(screenEdge.x, screenEdge.y));
        }
        metrics.targetRadiusPx = Mathf.Max(1f, radiusPx); // avoid zero

        metrics.offsetNorm = metrics.offsetPx / metrics.targetRadiusPx;

        // success defined as clicking within the visible boundary (offsetNorm <= 1)
        metrics.success = metrics.offsetNorm <= 1f;

        return metrics;
    }
}

/// <summary>
/// Container for click metrics computed by ClickableObject.
/// </summary>
public class ClickMetrics
{
    public string objectID;
    public string shapeType;
    public float timeMs;
    public Vector2 screenClick;
    public Vector3 worldCenter;
    public float offsetPx;
    public float offsetNorm;
    public float targetRadiusPx;
    public float distanceToCamera;
    public bool success;
}
