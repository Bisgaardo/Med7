using System.Collections.Generic;
using UnityEngine;

// Simple demo that selects splats by ID using a second render pass.
public class SplatSegmentationDemo : MonoBehaviour
{
    public GaussianSplatRenderer renderer;
    public Color highlight = Color.yellow;

    RenderTexture idRT;
    Texture2D readTex;
    HashSet<int> selected = new();
    ComputeBuffer idMask;
    Vector2 dragStart;

    void Start()
    {
        int size = 256; // small pick buffer
        idRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        readTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        idMask = new ComputeBuffer(1024, sizeof(uint));
        renderer.material.SetBuffer("_IDMask", idMask);
    }

    void OnDestroy()
    {
        idRT.Release();
        Destroy(readTex);
        idMask.Release();
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0)) dragStart = Input.mousePosition;
        if (Input.GetMouseButtonUp(0))
        {
            // Render ID pass then read a small rect where user dragged
            renderer.RenderID(idRT);
            var end = Input.mousePosition;
            Rect r = Rect.MinMaxRect(Mathf.Min(dragStart.x, end.x), Mathf.Min(dragStart.y, end.y),
                                     Mathf.Max(dragStart.x, end.x), Mathf.Max(dragStart.y, end.y));
            RenderTexture.active = idRT;
            readTex.ReadPixels(r, 0, 0);
            readTex.Apply();
            var px = readTex.GetPixels32();
            foreach (var c in px)
            {
                int id = c.r | (c.g << 8) | (c.b << 16);
                if (id != 0) selected.Add(id);
            }
            // Upload mask
            int count = Mathf.Min(selected.Count, 1024);
            var arr = new uint[count];
            int i = 0; foreach (var v in selected) { if (i >= count) break; arr[i++] = (uint)v; }
            idMask.SetData(arr);
            renderer.SetIDMask(idMask, count);
        }
    }
}
