using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.Experimental.Rendering;
using GaussianSplatting.Runtime;

[RequireComponent(typeof(GaussianSplatRenderer))]
public class SplatSegmentationManager : MonoBehaviour
{
    public Camera mainCamera;
    public Shader pickShader;

    public int activeSegment = 0;
    public int brushRadius = 12;
    public float pickInflate = 1.5f;

    RenderTexture pickTexture;
    RenderTexture debugBlitTex; // ARGB32 for OnGUI preview
    Material pickMaterial;
    ComputeBuffer segmentBuffer;
    int[] segmentCPU;
    GaussianSplatRenderer splatRenderer;
    int rtW, rtH;

    void Start()
    {
        splatRenderer = GetComponent<GaussianSplatRenderer>();
        if (mainCamera == null) mainCamera = Camera.main;

        CreateOrResizePickRT(Screen.width, Screen.height);
        pickMaterial = new Material(pickShader);

        int splatCount = splatRenderer.splatCount;
        segmentCPU = new int[splatCount];
        for (int i = 0; i < splatCount; i++) segmentCPU[i] = -1;

        segmentBuffer = new ComputeBuffer(splatCount, sizeof(int));
        segmentBuffer.SetData(segmentCPU);

        Shader.SetGlobalBuffer("_SplatSegments", segmentBuffer);
        Shader.SetGlobalInt("_SegmentsBound", 1);
    }

    void CreateOrResizePickRT(int width, int height)
    {
        width  = Mathf.Max(8, width);
        height = Mathf.Max(8, height);

        if (pickTexture != null && (rtW == width && rtH == height)) return;

        if (pickTexture != null) pickTexture.Release();

        var desc = new RenderTextureDescriptor(width, height)
        {
            graphicsFormat   = GraphicsFormat.R32_UInt,
            depthBufferBits  = 0,
            msaaSamples      = 1,
            mipCount         = 1,
            useMipMap        = false,
            autoGenerateMips = false
        };
        pickTexture = new RenderTexture(desc)
        {
            name       = "PickIDsRT",
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp
        };
        pickTexture.Create();

        if (debugBlitTex != null) debugBlitTex.Release();
        debugBlitTex = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm);

        rtW = width; rtH = height;
    }

void Update()
{
    CreateOrResizePickRT(Screen.width, Screen.height);
    if (Mouse.current == null) return;
    if (!Mouse.current.leftButton.isPressed) return;

    int label = activeSegment;
    Vector2 mousePos = Mouse.current.position.ReadValue();
    mousePos.y = Screen.height - mousePos.y;

    GaussianSplatRenderSystem.instance.RenderPickIDs(mainCamera, pickTexture, pickMaterial);
    ReadIdsFromPickTexture(mousePos, brushRadius, label);
}


    void ReadIdsFromPickTexture(Vector2 center, int radius, int labelToWrite)
    {
        AsyncGPUReadback.Request(pickTexture, 0, (req) =>
        {
            if (req.hasError) return;
            var data = req.GetData<uint>();
            int w = pickTexture.width;
            int h = pickTexture.height;

            int x0 = Mathf.Clamp((int)center.x - radius, 0, w - 1);
            int x1 = Mathf.Clamp((int)center.x + radius, 0, w - 1);
            int y0 = Mathf.Clamp((int)center.y - radius, 0, h - 1);
            int y1 = Mathf.Clamp((int)center.y + radius, 0, h - 1);

            int maxId = splatRenderer.splatCount;
            int painted = 0;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    uint v = data[row + x];
                    if (v == 0u) continue;
                    uint id = v - 1u;
                    if (id >= (uint)maxId) continue;
                    if (segmentCPU[id] != labelToWrite)
                    {
                        segmentCPU[id] = labelToWrite;
                        painted++;
                    }
                }
            }

            if (painted > 0) segmentBuffer.SetData(segmentCPU);
            if (painted > 0) Debug.Log($"[PICK] painted {painted} splats at {center}.");
        });
    }

    void OnGUI()
    {
        if (debugBlitTex != null)
            GUI.DrawTexture(new Rect(10, 10, 256, 256), debugBlitTex, ScaleMode.ScaleToFit, false);
    }

    void OnDestroy()
    {
        if (segmentBuffer != null) { segmentBuffer.Release(); segmentBuffer = null; }
        if (pickTexture != null) { pickTexture.Release(); DestroyImmediate(pickTexture); }
        if (debugBlitTex != null) { debugBlitTex.Release(); DestroyImmediate(debugBlitTex); }
        if (pickMaterial != null) { DestroyImmediate(pickMaterial); }
        Shader.SetGlobalInt("_SegmentsBound", 0);
    }
}
