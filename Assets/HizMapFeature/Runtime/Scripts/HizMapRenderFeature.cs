using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GPUDrivenTerrainLearn
{
    public class HizMapRenderFeature : ScriptableRendererFeature
    {
        [SerializeField] private ComputeShader _computeShader;

        private HizMapPass _pass;

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            if (cameraData.isSceneViewCamera || cameraData.isPreviewCamera)
            {
                return;
            }

            if (cameraData.camera.name == "Preview Camera")
            {
                return;
            }

            if (_pass != null)
            {
                renderer.EnqueuePass(_pass);
            }
        }

        public override void Create()
        {
            if (_pass == null)
            {
                if (!_computeShader)
                {
                    Debug.LogError("missing Hiz compute shader");
                    return;
                }

                _pass = new HizMapPass(this._computeShader);
            }
        }
    }


    public class HizMapPass : ScriptableRenderPass
    {
        private readonly HizMap _hizMap;

        public HizMapPass(ComputeShader computeShader)
        {
            this.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            _hizMap = new HizMap(computeShader);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            _hizMap.Update(context, renderingData.cameraData);
        }
    }
}