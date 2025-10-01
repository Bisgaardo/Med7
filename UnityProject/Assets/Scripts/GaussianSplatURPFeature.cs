using UnityEngine;
#if UNITY_RENDER_PIPELINE_UNIVERSAL
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tiny renderer feature that invokes GaussianSplatRenderer after transparents
public class GaussianSplatURPFeature : ScriptableRendererFeature
{
    class Pass : ScriptableRenderPass
    {
        public GaussianSplatRenderer renderer;
        public override void Execute(ScriptableRenderContext ctx, ref RenderingData data)
        {
            if (renderer) renderer.Render(data.cameraData.camera);
        }
    }

    public GaussianSplatRenderer renderer;
    Pass pass;

    public override void Create()
    {
        pass = new Pass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents, renderer = renderer };
    }

    public override void AddRenderPasses(ScriptableRenderer r, ref RenderingData data)
    {
        if (renderer != null) r.EnqueuePass(pass);
    }
}
#endif
