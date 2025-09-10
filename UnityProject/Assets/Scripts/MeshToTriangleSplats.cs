using System.Collections.Generic;
using UnityEngine;   // <-- Needed for MonoBehaviour, ContextMenu, etc.

public class MeshToTriangleSplats : MonoBehaviour
{
    public GaussianSplatRenderer targetRenderer;

    [ContextMenu("Bake Triangles To Renderer")]
    public void BakeToRenderer()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh)
        {
            Debug.LogError("MeshToTriangleSplats: No MeshFilter/Mesh found.");
            return;
        }

        Mesh mesh = mf.sharedMesh;
        var tris = mesh.triangles;
        var verts = mesh.vertices;
        var colors = mesh.colors;

        var list = new List<TriSplat>(tris.Length / 3);
        for (int i = 0; i < tris.Length; i += 3)
        {
            TriSplat s = new TriSplat();
            s.v0 = transform.TransformPoint(verts[tris[i]]);
            s.v1 = transform.TransformPoint(verts[tris[i + 1]]);
            s.v2 = transform.TransformPoint(verts[tris[i + 2]]);

            if (colors != null && colors.Length == verts.Length)
            {
                s.col0 = colors[tris[i]];
                s.col1 = colors[tris[i + 1]];
                s.col2 = colors[tris[i + 2]];
            }
            else
            {
                s.col0 = s.col1 = s.col2 = new Vector4(1, 1, 1, 1);
            }

            list.Add(s);
        }

        targetRenderer.LoadTriSplatsFromList(list);
        Debug.Log($"[GS] Baked {list.Count} triangle splats.");
    }


public struct TriSplat
{
    public Vector3 v0, v1, v2;
    public Vector4 col0, col1, col2;
}


}
