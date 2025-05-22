using UnityEngine;

namespace GPUDrivenTerrainLearn
{
    public class GPUTerrain : MonoBehaviour
    {
        public TerrainAsset terrainAsset;
        public Material mat;
        public bool isFrustumCullEnabled = true;
        public bool isDoubleCut = true;
        public bool isHizOcclusionCullingEnabled = true;

        [Range(0.01f, 1000)] public float hizDepthBias = 1;
        [Range(0, 100)] public int boundsHeightRedundance = 5;
        [Range(0.1f, 1.9f)] public float distanceEvaluation = 1.2f;


        /// <summary>
        /// 是否处理LOD之间的接缝问题
        /// </summary>
        public bool seamLess = true;

        /// <summary>
        /// 在渲染的时候，Patch之间留出一定缝隙供Debug
        /// </summary>
        public bool patchDebug = false;
        public bool nodeDebug = false;
        public bool mipDebug = false;
        public bool patchBoundsDebug = false;
        private bool _isTerrainMaterialDirty = false;
        private TerrainBuilder _traverse;
        private Material _terrainMaterial;

        private void Start()
        {
            _traverse = new TerrainBuilder(terrainAsset);
            _traverse.Start();
            terrainAsset.boundsDebugMaterial.SetBuffer("BoundsList", _traverse.patchBoundsBuffer);
            this.ApplySettings();
        }

        private Material EnsureTerrainMaterial()
        {
            if (!_terrainMaterial)
            {
                // var material = new Material(Shader.Find("GPUTerrainLearn/Terrain"));
                // material.SetTexture("_HeightMap", terrainAsset.heightMap);
                // material.SetTexture("_NormalMap", terrainAsset.normalMap);
                // material.SetTexture("_MainTex", terrainAsset.albedoMap);
                // material.SetBuffer("PatchList", _traverse.culledPatchBuffer);

                float shadowMaxDistance = 50;
                var urpAsset = UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                if (urpAsset)
                {
                    shadowMaxDistance = urpAsset.shadowDistance;
                }
                
                _terrainMaterial = mat;
                _terrainMaterial.SetBuffer("PatchList", _traverse.culledPatchBuffer);
                _terrainMaterial.SetFloat("_ShadowMaxDistance", shadowMaxDistance);
                this.UpdateTerrainMaterialProeprties();
            }

            return _terrainMaterial;
        }


        private void UpdateTerrainMaterialProeprties()
        {
            _isTerrainMaterialDirty = false;
            if (_terrainMaterial)
            {
                if (seamLess)
                {
                    _terrainMaterial.EnableKeyword("ENABLE_LOD_SEAMLESS");
                }
                else
                {
                    _terrainMaterial.DisableKeyword("ENABLE_LOD_SEAMLESS");
                }

                if (mipDebug)
                {
                    _terrainMaterial.EnableKeyword("ENABLE_MIP_DEBUG");
                }
                else
                {
                    _terrainMaterial.DisableKeyword("ENABLE_MIP_DEBUG");
                }

                if (this.patchDebug)
                {
                    _terrainMaterial.EnableKeyword("ENABLE_PATCH_DEBUG");
                }
                else
                {
                    _terrainMaterial.DisableKeyword("ENABLE_PATCH_DEBUG");
                }

                if (this.nodeDebug)
                {
                    _terrainMaterial.EnableKeyword("ENABLE_NODE_DEBUG");
                }
                else
                {
                    _terrainMaterial.DisableKeyword("ENABLE_NODE_DEBUG");
                }

                _terrainMaterial.SetVector("_WorldSize", terrainAsset.worldSize);
                _terrainMaterial.SetMatrix("_WorldToNormalMapMatrix", Matrix4x4.Scale(this.terrainAsset.worldSize).inverse);
            }
        }


        private void OnValidate()
        {
            this.ApplySettings();
        }

        private void ApplySettings()
        {
            if (_traverse != null)
            {
                _traverse.isFrustumCullEnabled = this.isFrustumCullEnabled;
                _traverse.isBoundsBufferOn = this.patchBoundsDebug;
                _traverse.isDoubleCut = this.isDoubleCut;
                _traverse.isHizOcclusionCullingEnabled = this.isHizOcclusionCullingEnabled;
                _traverse.boundsHeightRedundance = this.boundsHeightRedundance;
                _traverse.enableSeamDebug = this.patchDebug;
                _traverse.nodeEvalDistance = this.distanceEvaluation;
                _traverse.hizDepthBias = this.hizDepthBias;
            }

            _isTerrainMaterialDirty = true;
        }

        private void OnDestroy()
        {
            _traverse.Dispose();
        }

        //void Update() update会在相机高速旋转or移动时，在画面边缘产生空隙
        private void LateUpdate()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                _traverse.Dispatch();
            }

            _traverse.Dispatch();
            var terrainMaterial = this.EnsureTerrainMaterial();
            if (_isTerrainMaterialDirty)
            {
                this.UpdateTerrainMaterialProeprties();
            }

            Graphics.DrawMeshInstancedIndirect(TerrainAsset.patchMesh, 0, terrainMaterial, new Bounds(Vector3.zero, Vector3.one * 10240), _traverse.patchIndirectArgs);
            if (patchBoundsDebug)
            {
                Graphics.DrawMeshInstancedIndirect(TerrainAsset.unitCubeMesh, 0, terrainAsset.boundsDebugMaterial, new Bounds(Vector3.zero, Vector3.one * 10240), _traverse.boundsIndirectArgs);
            }
        }
    }
}