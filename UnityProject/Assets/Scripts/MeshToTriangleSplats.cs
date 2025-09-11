using System.Collections.Generic;
using UnityEngine;

public class MeshToTriangleSplats : MonoBehaviour
{
    public GaussianSplatRenderer targetRenderer;

    [ContextMenu("Bake Triangles To Renderer")]
    public void BakeToRenderer()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        MeshRenderer mr = GetComponent<MeshRenderer>();

        if (!mf || !mf.sharedMesh || !mr)
        {
            Debug.LogError("MeshToTriangleSplats: Missing MeshFilter or MeshRenderer.");
            return;
        }

        Mesh mesh = mf.sharedMesh;
        var mats = mr.sharedMaterials;
        int[] tris = mesh.triangles;
        Vector3[] verts = mesh.vertices;
        Color[] colors = mesh.colors;
        Vector2[] uvs = mesh.uv;

        var list = new List<TriSplat>(tris.Length / 3);

        // Loop through all triangles
        for (int i = 0; i < tris.Length; i += 3)
        {
            int subMeshIndex = FindSubmeshForTriangle(mesh, i); // helper below

            // Detect if this triangle belongs to a leaf material
            bool isLeaf = false;
            Texture2D tex = null;

            if (mats != null && subMeshIndex < mats.Length)
            {
                var mat = mats[subMeshIndex];
                if (mat != null)
                {
                    if (mat.mainTexture is Texture2D t) tex = t;
                    if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.name.ToLower().Contains("leaf"))
                        isLeaf = true;
                }
            }
for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
{
    var indices = mesh.GetTriangles(submesh);
    for (int t = 0; t < indices.Length; t += 3)   // 🔑 use t, not i
    {
        TriSplat s = new TriSplat();
        s.v0 = transform.TransformPoint(verts[indices[t]]);
        s.v1 = transform.TransformPoint(verts[indices[t + 1]]);
        s.v2 = transform.TransformPoint(verts[indices[t + 2]]);

        s.uv0 = uvs[indices[t]];
        s.uv1 = uvs[indices[t + 1]];
        s.uv2 = uvs[indices[t + 2]];

        s.col0 = s.col1 = s.col2 = new Vector4(1, 1, 1, 1);

        s.matID = submesh; // 🔑 material index

        list.Add(s);
    }
}



            // For leaves: assign texture once
            if (isLeaf && tex != null && targetRenderer != null && targetRenderer.triSplatMat != null)
            {
                targetRenderer.triSplatMat.SetTexture("_BaseMap", tex);
                targetRenderer.triSplatMat.SetInt("_HasTex", 1);
            }
        }

        // If no leaf textures found, fallback to color-only mode
        if (targetRenderer != null && targetRenderer.triSplatMat != null)
        {
            if (!targetRenderer.triSplatMat.HasProperty("_HasTex"))
            {
                targetRenderer.triSplatMat.SetInt("_HasTex", 0);
            }
        }

        targetRenderer.LoadTriSplatsFromList(list);
        Debug.Log($"[GS] Baked {list.Count} triangle splats.");
    }

    // Helper to figure out which submesh a triangle belongs to
    int FindSubmeshForTriangle(Mesh mesh, int triIndex)
    {
        int t = triIndex;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            int[] subTris = mesh.GetTriangles(sub);
            for (int i = 0; i < subTris.Length; i += 3)
            {
                if (subTris[i] == mesh.triangles[t] &&
                    subTris[i + 1] == mesh.triangles[t + 1] &&
                    subTris[i + 2] == mesh.triangles[t + 2])
                {
                    return sub;
                }
            }
        }
        return 0; // default to first if not found
    }
}
