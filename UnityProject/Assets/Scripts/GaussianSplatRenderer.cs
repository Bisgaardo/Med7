using System.Collections.Generic;
using UnityEngine;

// Renders many 3D Gaussian splats stored in a buffer.
// Keep math simple: diagonal covariance only, scale by distance.
public class GaussianSplatRenderer : MonoBehaviour
{
    // Public toggles exposed in inspector
    public bool FrontToBackApprox = false; // TODO: sort by depth for translucency
    public int MaxSplats = 10000;          // Maximum splats processed
    public bool ShowIDMask = true;         // Tint selected IDs

    public ComputeShader projectCS;        // SplatProject.compute
    public Shader billboardShader;         // SplatBillboard.shader

    ComputeBuffer splatBuffer;             // float3 pos, float4 color, float3x3 covariance
    ComputeBuffer visibleBuffer;           // uint indices after cull (Append buffer)
    ComputeBuffer argsBuffer;              // indirect draw args {4, count,0,0,0}

    Material billboardMat;
    Camera cam;

    public Material material => billboardMat;


    struct Splat
    {
        public Vector3 pos;   // world position
        public Vector4 col;   // premultiplied color
        public Vector3 cov0;  // covariance matrix rows
        public Vector3 cov1;
        public Vector3 cov2;
    }

    void OnEnable()
    {
        cam = Camera.main;
        // Allocate buffers
        splatBuffer   = new ComputeBuffer(MaxSplats, 64);
        visibleBuffer = new ComputeBuffer(MaxSplats, sizeof(uint), ComputeBufferType.Append);
        argsBuffer    = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);

        // Init draw args: 4 verts per quad, instance count written later
        var args = new uint[5] {4, 0, 0, 0, 0};
        argsBuffer.SetData(args);

        // Generate small random cloud
        var data = new Splat[MaxSplats];
        var rnd = new System.Random(0);
        for (int i = 0; i < data.Length; i++)
        {
            // random position in unit cube
            data[i].pos = new Vector3(
                (float)rnd.NextDouble() * 2f - 1f,
                (float)rnd.NextDouble() * 2f - 1f,
                (float)rnd.NextDouble() * 2f - 1f) * 5f;
            data[i].col = new Color(
                (float)rnd.NextDouble(),
                (float)rnd.NextDouble(),
                (float)rnd.NextDouble(), 1f);
            // covariance ~ small sphere
            float s = 0.05f + 0.05f * (float)rnd.NextDouble();
            data[i].cov0 = new Vector3(s, 0, 0);
            data[i].cov1 = new Vector3(0, s, 0);
            data[i].cov2 = new Vector3(0, 0, s);
        }
        splatBuffer.SetData(data);

        billboardMat = new Material(billboardShader);
        billboardMat.hideFlags = HideFlags.HideAndDontSave;
        billboardMat.SetBuffer("_SplatData", splatBuffer);
        billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);
    }

    void OnDisable()
    {
        splatBuffer?.Release();
        visibleBuffer?.Release();
        argsBuffer?.Release();
        if (billboardMat) DestroyImmediate(billboardMat);
    }

    // Called from Update and URP pass
    public void Render(Camera camera)
    {
        if (!projectCS || !billboardMat) return;
        cam = camera;

        // Reset visibility buffer & args
        visibleBuffer.SetCounterValue(0);

        // Setup compute constants
        int count = MaxSplats;
        projectCS.SetInt("_Count", count);
        projectCS.SetMatrix("_VP", cam.projectionMatrix * cam.worldToCameraMatrix);
        projectCS.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
        projectCS.SetBuffer(0, "SplatData", splatBuffer);
        projectCS.SetBuffer(0, "VisibleIndices", visibleBuffer);

        // Dispatch culling kernel (64 threads)
        int groups = Mathf.CeilToInt(count / 64f);
        projectCS.Dispatch(0, groups, 1, 1);

        // Write visible count into args[1]
        ComputeBuffer.CopyCount(visibleBuffer, argsBuffer, sizeof(uint));
        billboardMat.SetMatrix("_VP", cam.projectionMatrix * cam.worldToCameraMatrix);
        billboardMat.SetVector("_Screen", new Vector2(Screen.width, Screen.height));
        billboardMat.SetBuffer("_VisibleIndices", visibleBuffer);
        billboardMat.SetInt("_ShowIDMask", ShowIDMask ? 1 : 0);

        // Draw instanced quads
        var bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        Graphics.DrawProceduralIndirect(billboardMat, bounds, MeshTopology.TriangleStrip, argsBuffer);
    }

    void Update()
    {
        Render(Camera.main);
    }

    // Render ID mask to an offscreen target
    public void RenderID(RenderTexture target)
    {
        var old = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(false, true, Color.clear);
        billboardMat.EnableKeyword("_IDPASS");
        Graphics.DrawProceduralIndirect(billboardMat, new Bounds(Vector3.zero, Vector3.one * 1000f),
            MeshTopology.TriangleStrip, argsBuffer);
        billboardMat.DisableKeyword("_IDPASS");
        RenderTexture.active = old;
    }

    // Provide mask buffer with selected IDs
    public void SetIDMask(ComputeBuffer buf, int count)
    {
        billboardMat.SetBuffer("_IDMask", buf);
        billboardMat.SetInt("_IDMaskCount", count);
    }
}
