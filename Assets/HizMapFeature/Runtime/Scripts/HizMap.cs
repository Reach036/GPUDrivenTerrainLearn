using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GPUDrivenTerrainLearn
{
    public class HizMap
    {
        private const int KERNEL_BLIT = 0;
        private const int KERNEL_BUILDHIZ = 1;
        private float cutValue = 16;    //每次Dispatch做四次mip，完成后宽度缩小16倍
        private RenderTexture _hizMap;
        private readonly CommandBuffer _commandBuffer;
        private readonly ComputeShader _computeShader;
        private ComputeBuffer _mDispatchArgsBuffer;
        private uint[] _buildHizMapArgs = new uint[3];
        //public RenderTexture HizTexture => _hizmap;
        
        public HizMap(ComputeShader computeShader)
        {
            _commandBuffer = new CommandBuffer();
            _commandBuffer.name = "HizMap";
            _computeShader = computeShader;
            if (SystemInfo.usesReversedZBuffer)
            {
                computeShader.EnableKeyword("_REVERSE_Z");
            }
            
            if (SystemInfo.supportsIndirectArgumentsBuffer)
            {
                _mDispatchArgsBuffer = new ComputeBuffer(3, 4, ComputeBufferType.IndirectArguments);
            }
        }

        private void GetTempHizMapTexture(int nameId, int size, CommandBuffer commandBuffer)
        {
            var desc = new RenderTextureDescriptor(size, size, RenderTextureFormat.RFloat, 0, 1)
            {
                autoGenerateMips = false,
                useMipMap = false,
                enableRandomWrite = true
            };
            commandBuffer.GetTemporaryRT(nameId, desc, FilterMode.Point);
        }

        private RenderTexture GetTempHizMapTexture(int size, int mipCount)
        {
            var desc = new RenderTextureDescriptor(size, size, RenderTextureFormat.RFloat, 0, mipCount);
            var rt = RenderTexture.GetTemporary(desc);
            rt.autoGenerateMips = false;
            rt.useMipMap = mipCount > 1;
            rt.filterMode = FilterMode.Point;
            rt.enableRandomWrite = true;
            rt.Create();
            return rt;
        }

        private RenderTexture EnsureHizMap(Camera camera)
        {
            var preferMapSize = GetHiZMapSize(camera);
            if (_hizMap && _hizMap.width == preferMapSize && _hizMap.height == preferMapSize)
            {
                return _hizMap;
            }

            if (_hizMap)
            {
                RenderTexture.ReleaseTemporary(_hizMap);
            }

            var mipCount = (int)Mathf.Log(preferMapSize, 2) + 1;
            _hizMap = GetTempHizMapTexture(preferMapSize, mipCount);
            return _hizMap;
        }

        /// <summary>
        /// 生成HizMap
        /// </summary>
        public void Update(ScriptableRenderContext context, CameraData cameraData)
        {
            var camera = cameraData.camera;
            var hizMap = EnsureHizMap(camera);
            
            _commandBuffer.Clear();
            var dstWidth = hizMap.width * 2;
            var dstHeight = hizMap.height * 2;
            _computeShader.GetKernelThreadGroupSizes(KERNEL_BLIT, out var threadX, out var threadY, out _);
            
            var screenSize = new Vector2(camera.pixelWidth, camera.pixelHeight);
            _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.InTex, cameraData.renderer.cameraDepthTargetHandle.nameID);
            _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.SrcTexSize, screenSize);
            _computeShader.GetKernelThreadGroupSizes(KERNEL_BUILDHIZ, out threadX, out threadY, out _);

            //mip begin 优化版hiz生成，参考了https://zhuanlan.zhihu.com/p/335325149的思路
            int pingTex = ShaderConstants.PingTex;
            int pongTex = ShaderConstants.PongTex;
            int groupCount = Mathf.CeilToInt(hizMap.mipmapCount / 4f);
            for (var i = 0; i < groupCount; i++)
            {
                if (i > 0)  //第一个循环中，直接用相机深度贴图，先不改SrcTexSize
                {
                    _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.SrcTexSize, new Vector2(dstWidth, dstHeight));
                }

                var targetWidth = dstWidth / 2f;
                var targetHeight = dstHeight / 2f;
                _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.DstTexSize, new Vector2(targetWidth, targetHeight));
                _commandBuffer.SetComputeIntParam(_computeShader, ShaderConstants.HizMipCount,  (i + 1) * 4 - hizMap.mipmapCount);  //计算需要mip的数量，大于0说明不需要连续mip4次了
                
                if (i * 4 < hizMap.mipmapCount)
                {
                    _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.HIZ_MAP_Mip0_P, hizMap, i * 4);
                }
                if (i * 4 + 1 < hizMap.mipmapCount)
                {
                    _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.HIZ_MAP_Mip1_P, hizMap, i * 4 + 1);
                }
                if (i * 4 + 2 < hizMap.mipmapCount)
                {
                    _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.HIZ_MAP_Mip2_P, hizMap, i * 4 + 2);
                }
                if (i * 4 + 3 < hizMap.mipmapCount)
                {
                    _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.HIZ_MAP_Mip3_P, hizMap, i * 4 + 3);
                }

                var groupX = Mathf.CeilToInt(targetWidth * 1.0f / threadX);
                var groupY = Mathf.CeilToInt(targetHeight * 1.0f / threadY);
                dstWidth = dstWidth > cutValue ? Mathf.CeilToInt(dstWidth / cutValue) : 1;
                dstHeight = dstHeight > cutValue ? Mathf.CeilToInt(dstHeight / cutValue) : 1;

                GetTempHizMapTexture(pongTex, dstWidth, _commandBuffer);
                _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.MipCopyTex, pongTex, 0);

                if (SystemInfo.supportsIndirectArgumentsBuffer)
                {
                    _buildHizMapArgs[0] = (uint)groupX;
                    _buildHizMapArgs[1] = (uint)groupY;
                    _buildHizMapArgs[2] = 1;
                    _commandBuffer.SetBufferData(_mDispatchArgsBuffer, _buildHizMapArgs);
                    _commandBuffer.DispatchCompute(_computeShader, KERNEL_BUILDHIZ, _mDispatchArgsBuffer, 0);
                }
                else
                {
                    _commandBuffer.DispatchCompute(_computeShader, KERNEL_BUILDHIZ, groupX, groupY, 1);
                }
                
                //释放ping
                _commandBuffer.ReleaseTemporaryRT(pingTex);
                //将pong设置为输入
                _commandBuffer.SetComputeTextureParam(_computeShader, KERNEL_BUILDHIZ, ShaderConstants.InTex, pongTex);
                //交换PingPong
                (pingTex, pongTex) = (pongTex, pingTex);
            }
            //mip end
            _commandBuffer.ReleaseTemporaryRT(pingTex);
            _commandBuffer.SetGlobalTexture(ShaderConstants.HizMap, hizMap);
            var matrixVp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
            _commandBuffer.SetGlobalMatrix(ShaderConstants.HizCameraMatrixVP, matrixVp);
            _commandBuffer.SetGlobalVector(ShaderConstants.HizMapSize, new Vector4(hizMap.width, hizMap.height, hizMap.mipmapCount));
            _commandBuffer.SetGlobalVector(ShaderConstants.HizCameraPosition, camera.transform.position);
            context.ExecuteCommandBuffer(_commandBuffer);
        }

        private class ShaderConstants
        {
            public static readonly int HizCameraMatrixVP = Shader.PropertyToID("_HizCameraMatrixVP");
            public static readonly int HizCameraPosition = Shader.PropertyToID("_HizCameraPositionWS");
            //public static readonly RenderTargetIdentifier CameraDepthTexture = "_CameraDepthTexture";   2022后这个参数不对了
            public static readonly int InTex = Shader.PropertyToID("InTex");
            public static readonly int MipCopyTex = Shader.PropertyToID("MipCopyTex");
            public static readonly int PingTex = Shader.PropertyToID("PingTex");
            public static readonly int PongTex = Shader.PropertyToID("PongTex");

            public static readonly int SrcTexSize = Shader.PropertyToID("_SrcTexSize");
            public static readonly int DstTexSize = Shader.PropertyToID("_DstTexSize");
            public static readonly int HizMap = Shader.PropertyToID("_HizMap");
            public static readonly int HizMipCount = Shader.PropertyToID("_HizMipCount");
            public static readonly int HizMapSize = Shader.PropertyToID("_HizMapSize");
            
            public static readonly int HIZ_MAP_Mip0_P = Shader.PropertyToID("HIZ_MAP_Mip0");
            public static readonly int HIZ_MAP_Mip1_P = Shader.PropertyToID("HIZ_MAP_Mip1");
            public static readonly int HIZ_MAP_Mip2_P = Shader.PropertyToID("HIZ_MAP_Mip2");
            public static readonly int HIZ_MAP_Mip3_P = Shader.PropertyToID("HIZ_MAP_Mip3");
        }

        private static int GetHiZMapSize(Camera camera)
        {
            var screenSize = Mathf.Max(camera.pixelWidth, camera.pixelHeight);
            var textureSize = Mathf.NextPowerOfTwo(screenSize);
            return textureSize;
        }
    }
}