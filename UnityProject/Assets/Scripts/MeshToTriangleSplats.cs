using System.Collections.Generic;
using UnityEngine;   

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
        var uvs = new System.Collections.Generic.List<Vector2>(); 
        mesh.GetUVs(0, uvs);

        var list = new List<TriSplat>(tris.Length / 3);
        for (int i = 0; i < tris.Length; i += 3)
        {
            TriSplat s = new TriSplat();
            s.v0 = transform.TransformPoint(verts[tris[i]]);
            s.v1 = transform.TransformPoint(verts[tris[i + 1]]);
            s.v2 = transform.TransformPoint(verts[tris[i + 2]]);

            if (uvs != null && uvs.Count == verts.Length)
            {
                s.uv0 = uvs[tris[i]];
                s.uv1 = uvs[tris[i + 1]];
                s.uv2 = uvs[tris[i + 2]];
            }
else
{
    s.uv0 = s.uv1 = s.uv2 = Vector2.zero;
}

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

var mr = GetComponent<MeshRenderer>();
if (mr && mr.sharedMaterial)
{
    Texture leafTex = null;

    // Try URP's BaseMap first, then legacy mainTexture
    if (mr.sharedMaterial.HasProperty("_BaseMap"))
        leafTex = mr.sharedMaterial.GetTexture("_BaseMap");
    if (leafTex == null)
        leafTex = mr.sharedMaterial.mainTexture;

    if (leafTex != null)
        targetRenderer.SetTriangleTexture(leafTex, 0.3f); // tweak cutoff later if needed
}

        targetRenderer.LoadTriSplatsFromList(list);
        Debug.Log($"[GS] Baked {list.Count} triangle splats.");
    }



}
