#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Kagefumi
{
    /// <summary>
    /// Works around a URP 17.3 WebGL bug that hides every Lit mesh in Chrome on macOS.
    /// When a light has nothing to shadow (lamps off by day, sun off at night) URP still enables the shadow
    /// keyword and binds the RenderGraph <c>DefaultShadowTexture</c>, which is not a comparison depth texture
    /// in WebGL. ANGLE on Metal rejects the draw ("Mismatch between texture format and sampler type");
    /// ANGLE on D3D11 (Windows) lets it through. Right after the shadow passes this swaps such a 1×1 map
    /// for one allocated like URP's real shadowmaps (<see cref="ShadowUtils.AllocShadowRT"/>).
    /// </summary>
    static class WebGLShadowmapFix
    {
        static readonly int MainShadowmapId = Shader.PropertyToID("_MainLightShadowmapTexture");
        static readonly int AdditionalShadowmapId = Shader.PropertyToID("_AdditionalLightsShadowmapTexture");

        static RTHandle emptyShadowmap;
        static FixPass pass;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            emptyShadowmap = ShadowUtils.AllocShadowRT(1, 1, 16, 1, 0f, "KagefumiEmptyShadowmap");
            var cmd = new CommandBuffer { name = "Clear empty shadowmap" };
            cmd.SetRenderTarget(emptyShadowmap);
            cmd.ClearRenderTarget(RTClearFlags.All, Color.white); // far plane: everything lit
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            pass = new FixPass { renderPassEvent = RenderPassEvent.AfterRenderingShadows };
            RenderPipelineManager.beginCameraRendering += (_, camera) =>
            {
                if (camera.TryGetComponent(out UniversalAdditionalCameraData data) && data.scriptableRenderer != null)
                    data.scriptableRenderer.EnqueuePass(pass);
            };
        }

        class FixPass : ScriptableRenderPass
        {
            class PassData { }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                bool main = IsEmpty(renderGraph, resources.mainShadowsTexture);
                bool additional = IsEmpty(renderGraph, resources.additionalShadowsTexture);
                if (!main && !additional) return;

                var texture = renderGraph.ImportTexture(emptyShadowmap);
                using var builder = renderGraph.AddUnsafePass<PassData>("Kagefumi WebGL empty shadowmap", out _);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                if (main) builder.SetGlobalTextureAfterPass(texture, MainShadowmapId);
                if (additional) builder.SetGlobalTextureAfterPass(texture, AdditionalShadowmapId);
                builder.SetRenderFunc((PassData _, UnsafeGraphContext _) => { });
            }

            /// <summary>URP's real shadow atlases are at least 256 px; the default one it falls back to is 1×1.</summary>
            static bool IsEmpty(RenderGraph renderGraph, TextureHandle handle) =>
                handle.IsValid() && handle.GetDescriptor(renderGraph).width <= 1;
        }
    }
}
#endif
