using System;
using System.Collections.Generic;
using UnityEngine;

/// Attach to a MeshFilter (or give it a Mesh) and call BakeToRenderer()
/// Works in play mode or via the ContextMenu (editor).
[ExecuteAlways]
public class MeshToGaussianSplats : MonoBehaviour
{
    [Header("Source")]
    public MeshFilter meshFilter;                // if null, tries GetComponent<MeshFilter>()
    public SkinnedMeshRenderer skinned;          // optional: for skinned meshes (uses BakeMesh)

    [Header("Sampling")]
    [Tooltip("Total splats to generate (or leave 0 and use Splats Per Square Meter).")]
    public int targetCount = 10000;

    [Tooltip("If targetCount == 0, use this density instead.")]
    public float splatsPerSquareMeter = 2000f;

    [Header("Covariance / Footprint")]
    [Tooltip("Standard deviation in surface directions (meters).")]
    public float sigmaTangent = 0.01f;

    [Tooltip("Standard deviation along the surface normal (meters).")]
    public float sigmaNormal = 0.003f;

    [Header("Color Sampling")]
    [Tooltip("Prefer vertex colors if present.")]
    public bool preferVertexColor = true;

    [Tooltip("Sample albedo from material texture (requires readable texture or temporary copy).")]
    public bool sampleTexture = true;

    [Tooltip("Fallback color if neither vertex color nor texture is available.")]
    public Color fallbackColor = Color.white;

    [Header("Output")]
    public GaussianSplatRenderer targetRenderer;

    // --- internal temp ---
    Texture2D sampledAlbedo;

    [ContextMenu("Bake To Target Renderer")]
    public void BakeToRendererContextMenu()
    {
        if (!targetRenderer)
        {
            Debug.LogError("MeshToGaussianSplats: No targetRenderer assigned.");
            return;
        }
        BakeToRenderer(targetRenderer);
    }

    public void BakeToRenderer(GaussianSplatRenderer renderer)
    {
        Mesh mesh = null;
        Matrix4x4 localToWorld = transform.localToWorldMatrix;

        if (skinned)
        {
            mesh = new Mesh();
            skinned.BakeMesh(mesh, true);
            localToWorld = Matrix4x4.identity; // baked mesh already in world space
        }
        else
        {
            if (!meshFilter) meshFilter = GetComponent<MeshFilter>();
            if (!meshFilter || !meshFilter.sharedMesh)
            {
                Debug.LogError("MeshToGaussianSplats: No mesh found.");
                return;
            }
            mesh = meshFilter.sharedMesh;
        }

        // Topology
        var verts = mesh.vertices;
        var norms = mesh.normals;
        var tans  = mesh.tangents; // x,y,z,w (w = handedness)
        var cols  = mesh.colors;
        var uvs   = mesh.uv;
        var tris  = mesh.triangles;

        if (norms == null || norms.Length != verts.Length)
        {
            mesh.RecalculateNormals();
            norms = mesh.normals;
        }
        if (tans == null || tans.Length != verts.Length)
        {
            mesh.RecalculateTangents();
            tans = mesh.tangents;
        }

        // Triangle-area CDF
        int triCount = tris.Length / 3;
        var cumulative = new float[triCount];
        float totalArea = 0f;

        for (int t = 0; t < triCount; t++)
        {
            int i0 = tris[3 * t + 0];
            int i1 = tris[3 * t + 1];
            int i2 = tris[3 * t + 2];
            Vector3 p0 = verts[i0];
            Vector3 p1 = verts[i1];
            Vector3 p2 = verts[i2];
            float area = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;

            totalArea += area;
            cumulative[t] = totalArea;
        }

        int wanted = targetCount;
        if (wanted <= 0)
            wanted = Mathf.Max(100, Mathf.RoundToInt(totalArea * splatsPerSquareMeter));

        // Prepare texture sampling
        sampledAlbedo = null;
        Texture2D albedoReadable = TryGetReadableMainTex(out float texW, out float texH);

        // Build splat array (64 bytes each: pos(12) + col(16) + cov0(12) + cov1(12) + cov2(12))
        var splats = new List<Splat>(wanted);
        var rand = new System.Random(12345);

        for (int s = 0; s < wanted; s++)
        {
            // Pick triangle by area
            float r = (float)rand.NextDouble() * totalArea;
            int lo = 0, hi = triCount - 1, mid = 0;
            while (lo < hi)
            {
                mid = (lo + hi) >> 1;
                if (r <= cumulative[mid]) hi = mid; else lo = mid + 1;
            }
            int triIdx = lo;

            int i0 = tris[3 * triIdx + 0];
            int i1 = tris[3 * triIdx + 1];
            int i2 = tris[3 * triIdx + 2];

            // Random barycentric (uniform)
            float u = (float)rand.NextDouble();
            float v = (float)rand.NextDouble();
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            float w = 1f - u - v;

            // Interpolate attributes
            Vector3 P = w * verts[i0] + u * verts[i1] + v * verts[i2];
            Vector3 N = (w * norms[i0] + u * norms[i1] + v * norms[i2]).normalized;

            // Tangent frame
            Vector4 T4 = w * tans[i0] + u * tans[i1] + v * tans[i2];
            Vector3 T = ((Vector3)T4).normalized;
            Vector3 B = Vector3.Cross(N, T) * (T4.w < 0f ? -1f : 1f);

            // World space (unless skinned baked to world)
            Vector3 Pw = (skinned ? P : localToWorld.MultiplyPoint3x4(P));
            Vector3 Nw = (skinned ? N : localToWorld.MultiplyVector(N)).normalized;
            Vector3 Tw = (skinned ? T : localToWorld.MultiplyVector(T)).normalized;
            Vector3 Bw = Vector3.Cross(Nw, Tw) * (T4.w < 0f ? -1f : 1f);

            // === NEW PACKING FOR SURFACE-ALIGNED SPLATS ===
            // Store in-plane axes already scaled by sigmaTangent,
            // and the normal scaled by sigmaNormal. This keeps the 64-byte layout.
            Vector3 covRow0 = Tw * sigmaTangent; // axis-X in world
            Vector3 covRow1 = Bw * sigmaTangent; // axis-Y in world
            Vector3 covRow2 = Nw * sigmaNormal;  // thickness direction (used for look or effects later)

            // Color: vertex > texture > fallback
            Color c = fallbackColor;
            bool gotColor = false;

            if (preferVertexColor && cols != null && cols.Length == verts.Length)
            {
                c = w * cols[i0] + u * cols[i1] + v * cols[i2];
                gotColor = true;
            }
            if (!gotColor && sampleTexture && albedoReadable && uvs != null && uvs.Length == verts.Length)
            {
                Vector2 uv = w * uvs[i0] + u * uvs[i1] + v * uvs[i2];
                int tx = Mathf.Clamp(Mathf.RoundToInt(uv.x * texW), 0, (int)texW - 1);
                int ty = Mathf.Clamp(Mathf.RoundToInt(uv.y * texH), 0, (int)texH - 1);
                c = sampledAlbedo.GetPixel(tx, ty);
                gotColor = true;
            }

            var sp = new Splat
            {
                pos  = Pw,
                col  = new Vector4(c.r, c.g, c.b, 1f),
                cov0 = covRow0,   // Tw * sigmaT
                cov1 = covRow1,   // Bw * sigmaT
                cov2 = covRow2,   // Nw * sigmaN
            };
            splats.Add(sp);
        }

        renderer.LoadSplatsFromList(splats);
        Debug.Log($"Baked {splats.Count} surface-aligned splats from mesh '{mesh.name}' (area ~ {totalArea:F2} m²).");
    }

    Texture2D TryGetReadableMainTex(out float w, out float h)
    {
        w = h = 0f;
        var mr = GetComponent<Renderer>();
        if (!mr || mr.sharedMaterial == null) return null;

        var tex = mr.sharedMaterial.mainTexture as Texture2D;
        if (!tex) return null;

        // If texture is already readable, use it directly
        try
        {
            tex.GetPixel(0, 0);
            sampledAlbedo = tex;
            w = tex.width; h = tex.height;
            return sampledAlbedo;
        }
        catch { /* not readable */ }

        // Make a temporary readable copy via RT
        var prev = RenderTexture.active;
        var rt = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
        RenderTexture.active = rt;
        copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        copy.Apply(false, false);
        RenderTexture.active = prev;
        rt.Release();

        sampledAlbedo = copy;
        w = copy.width; h = copy.height;
        return sampledAlbedo;
    }

    // ==== Must match the renderer's 64-byte Splat layout ====
    public struct Splat
    {
        public Vector3 pos;   // 12
        public Vector4 col;   // +16 = 28
        public Vector3 cov0;  // +12 = 40  (Tw * sigmaT)
        public Vector3 cov1;  // +12 = 52  (Bw * sigmaT)
        public Vector3 cov2;  // +12 = 64  (Nw * sigmaN)
    }
}
