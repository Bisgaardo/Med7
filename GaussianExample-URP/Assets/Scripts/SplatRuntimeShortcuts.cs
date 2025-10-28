using UnityEngine;
using GaussianSplatting.Runtime;

public class SplatRuntimeShortcuts : MonoBehaviour
{
    [Header("References (auto-assigned if null)")]
    public GaussianSplatRenderer gs;

    void Awake()
    {
        if (gs == null)
        {
#if UNITY_2023_1_OR_NEWER
            gs = FindFirstObjectByType<GaussianSplatRenderer>();
#else
            gs = FindObjectOfType<GaussianSplatRenderer>();
#endif
            if (gs != null)
                Debug.Log($"[Shortcuts] Auto-found GaussianSplatRenderer: {gs.name}");
            else
                Debug.LogWarning("[Shortcuts] No GaussianSplatRenderer found in scene!");
        }

        // Segmentation manager removed in SAM+YOLO+ZoeDepth pipeline
    }

    void Update()
    {
        if (!gs || !gs.HasValidAsset || !gs.HasValidRenderSetup) return;

        // --- Selection helpers ---
        if (Input.GetKeyDown(KeyCode.R)) { gs.EditDeselectAll();  gs.UpdateEditCountsAndBounds(); Debug.Log("[Shortcut] Deselected all splats"); }
        if (Input.GetKeyDown(KeyCode.I)) { gs.EditInvertSelection(); gs.UpdateEditCountsAndBounds(); Debug.Log("[Shortcut] Inverted selection"); }
        if (Input.GetKeyDown(KeyCode.T)) { gs.EditSelectAll();     gs.UpdateEditCountsAndBounds(); Debug.Log("[Shortcut] Selected all splats"); }
        if (Input.GetKeyDown(KeyCode.Delete)) { gs.EditDeleteSelected(); gs.UpdateEditCountsAndBounds(); Debug.Log("[Shortcut] Deleted selected splats"); }

        // --- Legacy K-Means hotkey removed ---
        if (Input.GetKeyDown(KeyCode.K))
        {
            Debug.Log("[Segmentation] K-Means shortcut is deprecated and disabled.");
        }

        // --- Toggle display ---
        if (Input.GetKeyDown(KeyCode.L))
        {
            gs.m_RenderMode = (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.ColorByLabel)
                ? GaussianSplatRenderer.RenderMode.Splats
                : GaussianSplatRenderer.RenderMode.ColorByLabel;

            Debug.Log(gs.m_RenderMode == GaussianSplatRenderer.RenderMode.ColorByLabel
                ? "[Render] Switched to ColorByLabel view."
                : "[Render] Switched to normal Splats view.");
        }
    }
}
