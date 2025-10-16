using UnityEngine;

public class ClickableObject : MonoBehaviour
{
    public int objectId;
    private Color originalColor;

    void Start()
    {
        originalColor = GetComponent<Renderer>().material.color;
    }
    void Update()
    {

    }

    public float GetDirectionalWidth(Camera cam, Vector2 cursorPos)
    {
        Renderer r = GetComponent<Renderer>();
        if (r == null) return 1f;

        Bounds b = r.bounds;
        Vector3 c = b.center;
        Vector3 e = b.extents;

        // Screen-space target center
        Vector2 targetScreen = cam.WorldToScreenPoint(c);

        // Direction of approach (normalized 2D vector)
        Vector2 dir = (targetScreen - cursorPos).normalized;
        if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right;

        float minProj = float.PositiveInfinity;
        float maxProj = float.NegativeInfinity;

        // Loop through 8 corners of the world bounding box
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                    Vector3 screenPos3 = cam.WorldToScreenPoint(corner);
                    if (screenPos3.z < 0f) continue;

                    Vector2 screenPos = new Vector2(screenPos3.x, screenPos3.y);
                    float proj = Vector2.Dot(screenPos, dir);
                    if (proj < minProj) minProj = proj;
                    if (proj > maxProj) maxProj = proj;
                }

        // Directional width in pixels along the movement axis
        return Mathf.Max(1f, maxProj - minProj);
    }


    public void HighlightObject()
    {
        GetComponent<Renderer>().material.color = Color.yellow;
    }

    public Vector3 GetScreenPosition(Camera camera)
    {
        return camera.WorldToScreenPoint(transform.position);
    }

    public void ResetColor()
    {
        GetComponent<Renderer>().material.color = originalColor;
    }

}
