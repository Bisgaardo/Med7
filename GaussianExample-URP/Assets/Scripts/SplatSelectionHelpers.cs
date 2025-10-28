using UnityEngine;                       // for Camera, Bounds, Vector3, etc.
using GaussianSplatting.Runtime;         // for GaussianSplatRendere

public static class SplatSelectionHelpers
{
    public static void SelectByBounds(GaussianSplatRenderer gs, Camera cam, Bounds worldBounds, bool subtract = false)
    {
        // START = snapshot
        gs.EditStoreSelectionMouseDown();

        // project 8 corners to screen
        var c = worldBounds.center; var e = worldBounds.extents;
        Vector3[] w = {
            c + new Vector3(-e.x,-e.y,-e.z), c + new Vector3(+e.x,-e.y,-e.z),
            c + new Vector3(-e.x,+e.y,-e.z), c + new Vector3(+e.x,+e.y,-e.z),
            c + new Vector3(-e.x,-e.y,+e.z), c + new Vector3(+e.x,-e.y,+e.z),
            c + new Vector3(-e.x,+e.y,+e.z), c + new Vector3(+e.x,+e.y,+e.z)
        };
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var s = (Vector2)cam.WorldToScreenPoint(w[i]);
            min = Vector2.Min(min, s);
            max = Vector2.Max(max, s);
        }

        gs.EditUpdateSelection(min, max, cam, subtract);
        gs.UpdateEditCountsAndBounds();
    }
}
