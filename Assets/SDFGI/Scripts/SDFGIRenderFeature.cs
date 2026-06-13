// SDFGIRenderFeature.cs
// Unity 6 (RenderGraph) URP renderer feature. Adds a fullscreen additive pass
// after the skybox that reconstructs world position + normal from the depth
// and normals textures, samples the SDFGI irradiance field and adds
// irradiance * albedo on top of the lit scene.
//
// Albedo source:
//   - Deferred rendering path: GBuffer0 (recommended — correct albedo).
//   - Forward/Forward+:        a constant fallback albedo from SDFGIVolume.

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class SDFGIRenderFeature : ScriptableRendererFeature
{
    class SDFGIPass : ScriptableRenderPass
    {
        static readonly int GBuffer0Id = Shader.PropertyToID("_SDFGIGBuffer0");
        Material _material;

        public SDFGIPass(Material material)
        {
            _material = material;
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        class PassData
        {
            public Material material;
            public TextureHandle gBuffer0;
            public bool useGBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;
            var volume = SDFGIVolume.Active;
            if (volume == null || !volume.IsReady) return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType == CameraType.Preview ||
                cameraData.cameraType == CameraType.Reflection)
                return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "SDFGI Composite", out var passData))
            {
                passData.material = _material;

                bool useGBuffer =
                    resourceData.gBuffer != null &&
                    resourceData.gBuffer.Length > 0 &&
                    resourceData.gBuffer[0].IsValid();
                passData.useGBuffer = useGBuffer;
                if (useGBuffer)
                {
                    passData.gBuffer0 = resourceData.gBuffer[0];
                    builder.UseTexture(passData.gBuffer0);
                }

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    if (data.useGBuffer)
                    {
                        data.material.EnableKeyword("_SDFGI_GBUFFER");
                        data.material.SetTexture(GBuffer0Id, data.gBuffer0);
                    }
                    else
                    {
                        data.material.DisableKeyword("_SDFGI_GBUFFER");
                    }

                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                        data.material, 0);
                });
            }
        }
    }

    [SerializeField] Shader compositeShader;
    Material _material;
    SDFGIPass _pass;

    public override void Create()
    {
        if (compositeShader == null)
            compositeShader = Shader.Find("Hidden/SDFGI/Composite");
        if (compositeShader != null)
            _material = CoreUtils.CreateEngineMaterial(compositeShader);
        _pass = new SDFGIPass(_material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null) return;
        if (renderingData.cameraData.cameraType != CameraType.Game &&
            renderingData.cameraData.cameraType != CameraType.SceneView)
            return;
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
    }
}
