using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace GPUDrivenTerrainLearn
{
    /// <summary>
    /// 地形构建器，用于在GPU上构建和渲染四叉树地形
    /// 实现了LOD管理、视锥体裁剪、遮挡剔除等功能
    /// </summary>
    public class TerrainBuilder : System.IDisposable
    {
        private ComputeShader _computeShader; // 用于地形构建的计算着色器

        // 各种ComputeBuffer用于GPU和CPU之间的数据传输
        private ComputeBuffer _maxLODNodeList;    // 存储最大LOD级别的所有节点
        private ComputeBuffer _nodeListA;         // 节点列表A，用于四叉树遍历过程中的节点交换
        private ComputeBuffer _nodeListB;         // 节点列表B，用于四叉树遍历过程中的节点交换
        private ComputeBuffer _finalNodeListBuffer; // 存储最终筛选后的节点列表
        private ComputeBuffer _nodeDescriptors;    // 节点描述符，存储节点的额外信息

        private ComputeBuffer _culledPatchBuffer;  // 存储经过剔除后的地形片段

        // 间接绘制相关缓冲区
        private ComputeBuffer _patchIndirectArgs;        // 地形片段的间接绘制参数
        private ComputeBuffer _patchBoundsBuffer;        // 存储地形片段的包围盒数据
        private ComputeBuffer _patchBoundsIndirectArgs;  // 地形包围盒的间接绘制参数
        private ComputeBuffer _indirectArgsBuffer;       // 通用间接参数缓冲区
        private RenderTexture _lodMap;                  // LOD映射贴图，存储每个区域的LOD级别

        private const int PatchStripSize = 9 * 4;  // 每个地形片段条带的大小

        // 节点评估参数，用于计算节点的LOD级别
        private Vector4 _nodeEvaluationC = new(1, 0, 0, 0);
        private bool _isNodeEvaluationCDirty = true;

        private float _hizDepthBias = 1;  // 用于深度遮挡剔除的偏移值

        private TerrainAsset _asset;  // 地形资产，包含地形的尺寸、纹理等信息

        private CommandBuffer _commandBuffer = new();  // 用于批量提交绘制命令
        private Plane[] _cameraFrustumPlanes = new Plane[6];         // 摄像机视锥体的6个平面
        private Vector4[] _cameraFrustumPlanesV4 = new Vector4[6];   // 以Vector4格式存储的视锥体平面

        // 计算着色器的核心函数索引
        private int _kernelOfTraverseQuadTree;  // 遍历四叉树的核心函数
        private int _kernelOfBuildLodMap;       // 构建LOD映射的核心函数
        private int _kernelOfBuildPatches;      // 构建地形片段的核心函数

        /// <summary>
        /// Buffer的大小需要根据预估的最大分割情况进行分配
        /// </summary>
        private const int MaxNodeBufferSize = 200; // 节点缓冲区的最大大小
        private const int TempNodeBufferSize = 50; // 临时节点缓冲区的大小

        /// <summary>
        /// 构造函数，初始化地形构建器
        /// </summary>
        /// <param name="asset">地形资产引用</param>
        public TerrainBuilder(TerrainAsset asset)
        {
            _asset = asset;
            _computeShader = asset.computeShader;
            _commandBuffer.name = "TerrainBuild";
            
            // 初始化各种ComputeBuffer
            _culledPatchBuffer = new ComputeBuffer(MaxNodeBufferSize * 64, PatchStripSize, ComputeBufferType.Append);

            // 初始化间接绘制参数
            _patchIndirectArgs = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
            _patchIndirectArgs.SetData(new uint[] { TerrainAsset.patchMesh.GetIndexCount(0), 0, 0, 0, 0 });

            _patchBoundsIndirectArgs = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments);
            _patchBoundsIndirectArgs.SetData(new uint[] { TerrainAsset.unitCubeMesh.GetIndexCount(0), 0, 0, 0, 0 });

            // 初始化最大LOD节点列表
            _maxLODNodeList = new ComputeBuffer(TerrainAsset.MAX_LOD_NODE_COUNT * TerrainAsset.MAX_LOD_NODE_COUNT, 8, ComputeBufferType.Append);
            InitMaxLODNodeListDatas();

            // 初始化节点列表和其他缓冲区
            _nodeListA = new ComputeBuffer(TempNodeBufferSize, 8, ComputeBufferType.Append);
            _nodeListB = new ComputeBuffer(TempNodeBufferSize, 8, ComputeBufferType.Append);
            _indirectArgsBuffer = new ComputeBuffer(3, 4, ComputeBufferType.IndirectArguments);
            _indirectArgsBuffer.SetData(new uint[] { 1, 1, 1 });
            _finalNodeListBuffer = new ComputeBuffer(MaxNodeBufferSize, 12, ComputeBufferType.Append);
            _nodeDescriptors = new ComputeBuffer((int)(TerrainAsset.MAX_NODE_ID + 1), 4);

            _patchBoundsBuffer = new ComputeBuffer(MaxNodeBufferSize * 64, 4 * 10, ComputeBufferType.Append);

            // 创建LOD贴图
            _lodMap = TextureUtility.CreateLODMap(160);

            // 检查并设置深度缓冲区的方向
            if (SystemInfo.usesReversedZBuffer)
            {
                _computeShader.EnableKeyword("_REVERSE_Z");
            }
            else
            {
                _computeShader.DisableKeyword("_REVERSE_Z");
            }

            InitKernels();     // 初始化计算着色器核心
            InitWorldParams(); // 初始化世界参数

            boundsHeightRedundance = 5; // 设置包围盒高度冗余
            hizDepthBias = 1;           // 设置深度偏差
        }
        
        public void Start()
        {
            _camera = Camera.main; // 获取主摄像机
        }

        /// <summary>
        /// 初始化最大LOD节点列表数据
        /// 创建一个网格，其中包含最大LOD级别的所有节点
        /// </summary>
        private void InitMaxLODNodeListDatas()
        {
            var maxLODNodeCount = TerrainAsset.MAX_LOD_NODE_COUNT;
            uint2[] datas = new uint2[maxLODNodeCount * maxLODNodeCount];
            var index = 0;
            for (uint i = 0; i < maxLODNodeCount; i++)
            {
                for (uint j = 0; j < maxLODNodeCount; j++)
                {
                    datas[index] = new uint2(i, j); // 创建二维索引
                    index++;
                }
            }

            _maxLODNodeList.SetData(datas);
        }

        /// <summary>
        /// 初始化计算着色器的核心函数
        /// </summary>
        private void InitKernels()
        {
            // 找到各个核心函数的索引
            _kernelOfTraverseQuadTree = _computeShader.FindKernel("TraverseQuadTree");
            _kernelOfBuildLodMap = _computeShader.FindKernel("BuildLodMap");
            _kernelOfBuildPatches = _computeShader.FindKernel("BuildPatches");
            
            // 绑定计算着色器的资源
            BindComputeShader(_kernelOfTraverseQuadTree);
            BindComputeShader(_kernelOfBuildLodMap);
            BindComputeShader(_kernelOfBuildPatches);
        }

        /// <summary>
        /// 为指定的核心函数绑定计算着色器资源
        /// </summary>
        /// <param name="kernelIndex">核心函数索引</param>
        private void BindComputeShader(int kernelIndex)
        {
            // 所有核心都需要四叉树纹理
            _computeShader.SetTexture(kernelIndex, "QuadTreeTexture", _asset.quadTreeMap);
            
            if (kernelIndex == _kernelOfTraverseQuadTree)
            {
                // 四叉树遍历核心需要的资源
                _computeShader.SetBuffer(kernelIndex, ShaderConstants.AppendFinalNodeList, _finalNodeListBuffer);
                _computeShader.SetTexture(kernelIndex, "MinMaxHeightTexture", _asset.minMaxHeightMap);
                _computeShader.SetBuffer(kernelIndex, ShaderConstants.NodeDescriptors, _nodeDescriptors);
            }
            else if (kernelIndex == _kernelOfBuildLodMap)
            {
                // 构建LOD映射核心需要的资源
                _computeShader.SetTexture(kernelIndex, ShaderConstants.LodMap, _lodMap);
                _computeShader.SetBuffer(kernelIndex, ShaderConstants.NodeDescriptors, _nodeDescriptors);
            }
            else if (kernelIndex == _kernelOfBuildPatches)
            {
                // 构建地形片段核心需要的资源
                _computeShader.SetTexture(kernelIndex, ShaderConstants.LodMap, _lodMap);
                _computeShader.SetTexture(kernelIndex, "MinMaxHeightTexture", _asset.minMaxHeightMap);
                _computeShader.SetBuffer(kernelIndex, ShaderConstants.FinalNodeList, _finalNodeListBuffer);
                _computeShader.SetBuffer(kernelIndex, "CulledPatchList", _culledPatchBuffer);
                _computeShader.SetBuffer(kernelIndex, "PatchBoundsList", _patchBoundsBuffer);
            }
        }

        /// <summary>
        /// 初始化世界参数
        /// 计算不同LOD级别的节点大小、片段范围等参数
        /// </summary>
        private void InitWorldParams()
        {
            float wSize = _asset.worldSize.x;
            int nodeCount = TerrainAsset.MAX_LOD_NODE_COUNT;
            Vector4[] worldLODParams = new Vector4[TerrainAsset.MAX_LOD + 1];
            for (var lod = TerrainAsset.MAX_LOD; lod >= 0; lod--)
            {
                var nodeSize = wSize / nodeCount;       // 节点大小
                var patchExtent = nodeSize / 16;        // 片段范围
                var sectorCountPerNode = (int)Mathf.Pow(2, lod); // 每个节点的扇区数
                worldLODParams[lod] = new Vector4(nodeSize, patchExtent, nodeCount, sectorCountPerNode);
                nodeCount *= 2;  // 下一个LOD级别的节点数量翻倍
            }

            _computeShader.SetVectorArray(ShaderConstants.WorldLodParams, worldLODParams);

            // 计算各LOD级别节点ID的偏移量
            int[] nodeIDOffsetLOD = new int[(TerrainAsset.MAX_LOD + 1) * 4];
            int nodeIdOffset = 0;
            for (int lod = TerrainAsset.MAX_LOD; lod >= 0; lod--)
            {
                nodeIDOffsetLOD[lod * 4] = nodeIdOffset;
                nodeIdOffset += (int)(worldLODParams[lod].z * worldLODParams[lod].z);
            }

            _computeShader.SetInts("NodeIDOffsetOfLOD", nodeIDOffsetLOD);
        }

        /// <summary>
        /// 设置包围盒高度冗余
        /// 用于增加地形包围盒高度，防止边缘裁剪问题
        /// </summary>
        public int boundsHeightRedundance
        {
            set => _computeShader.SetInt("_BoundsHeightRedundance", value);
        }

        /// <summary>
        /// 设置节点评估距离
        /// 用于调整LOD级别计算的距离参数
        /// </summary>
        public float nodeEvalDistance
        {
            set
            {
                _nodeEvaluationC.x = value;
                _isNodeEvaluationCDirty = true;
            }
        }

        /// <summary>
        /// 启用/禁用接缝调试
        /// 用于可视化地形接缝问题
        /// </summary>
        public bool enableSeamDebug
        {
            set
            {
                if (value)
                {
                    _computeShader.EnableKeyword("ENABLE_SEAM");
                }
                else
                {
                    _computeShader.DisableKeyword("ENABLE_SEAM");
                }
            }
        }

        /// <summary>
        /// 设置HiZ深度偏差
        /// 用于调整层次Z缓冲的深度计算，防止错误的遮挡剔除
        /// </summary>
        public float hizDepthBias
        {
            set
            {
                _hizDepthBias = value;
                _computeShader.SetFloat("_HizDepthBias", Mathf.Clamp(value, 0.01f, 1000f));
            }
            get => _hizDepthBias;
        }

        /// <summary>
        /// 释放所有ComputeBuffer资源
        /// </summary>
        public void Dispose()
        {
            _culledPatchBuffer.Dispose();
            _patchIndirectArgs.Dispose();
            _finalNodeListBuffer.Dispose();
            _maxLODNodeList.Dispose();
            _nodeListA.Dispose();
            _nodeListB.Dispose();
            _indirectArgsBuffer.Dispose();
            _patchBoundsBuffer.Dispose();
            _patchBoundsIndirectArgs.Dispose();
            _nodeDescriptors.Dispose();
        }

        /// <summary>
        /// 启用/禁用视锥体裁剪
        /// </summary>
        public bool isFrustumCullEnabled
        {
            set
            {
                if (value)
                {
                    _computeShader.EnableKeyword("ENABLE_FRUS_CULL");
                }
                else
                {
                    _computeShader.DisableKeyword("ENABLE_FRUS_CULL");
                }
            }
        }
        
        /// <summary>
        /// 启用/禁用batch二分
        /// </summary>
        public bool isDoubleCut
        {
            set
            {
                if (value)
                {
                    _computeShader.EnableKeyword("ENABLE_DOUBLE_CUT");
                }
                else
                {
                    _computeShader.DisableKeyword("ENABLE_DOUBLE_CUT");
                }
            }
        }

        /// <summary>
        /// 启用/禁用HiZ遮挡剔除
        /// 利用层次化深度缓冲剔除被遮挡的地形部分
        /// </summary>
        public bool isHizOcclusionCullingEnabled
        {
            set
            {
                if (value)
                {
                    _computeShader.EnableKeyword("ENABLE_HIZ_CULL");
                }
                else
                {
                    _computeShader.DisableKeyword("ENABLE_HIZ_CULL");
                }
            }
        }

        private bool _isBoundsBufferOn;
        private Camera _camera;

        /// <summary>
        /// 启用/禁用包围盒调试
        /// 用于可视化地形片段的包围盒
        /// </summary>
        public bool isBoundsBufferOn
        {
            set
            {
                if (value)
                {
                    _computeShader.EnableKeyword("BOUNDS_DEBUG");
                }
                else
                {
                    _computeShader.DisableKeyword("BOUNDS_DEBUG");
                }

                _isBoundsBufferOn = value;
            }
            get => _isBoundsBufferOn;
        }

        /// <summary>
        /// 输出片段间接绘制参数到日志
        /// 用于调试
        /// </summary>
        private void LogPatchArgs()
        {
            var data = new uint[5];
            _patchIndirectArgs.GetData(data);
            Debug.Log(data[1]);
        }

        /// <summary>
        /// 清除所有Buffer的计数器
        /// 准备下一帧的处理
        /// </summary>
        private void ClearBufferCounter()
        {
            _commandBuffer.SetBufferCounterValue(_maxLODNodeList, (uint)_maxLODNodeList.count);
            _commandBuffer.SetBufferCounterValue(_nodeListA, 0);
            _commandBuffer.SetBufferCounterValue(_nodeListB, 0);
            _commandBuffer.SetBufferCounterValue(_finalNodeListBuffer, 0);
            _commandBuffer.SetBufferCounterValue(_culledPatchBuffer, 0);
            _commandBuffer.SetBufferCounterValue(_patchBoundsBuffer, 0);
        }

        /// <summary>
        /// 更新摄像机视锥体平面
        /// 用于视锥体裁剪
        /// </summary>
        /// <param name="camera">当前摄像机</param>
        private void UpdateCameraFrustumPlanes(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, _cameraFrustumPlanes);
            for (var i = 0; i < _cameraFrustumPlanes.Length; i++)
            {
                Vector4 v4 = _cameraFrustumPlanes[i].normal;
                v4.w = _cameraFrustumPlanes[i].distance;
                _cameraFrustumPlanesV4[i] = v4;
            }

            _computeShader.SetVectorArray(ShaderConstants.CameraFrustumPlanes, _cameraFrustumPlanesV4);
        }

        /// <summary>
        /// 执行地形构建和渲染
        /// 主要流程包括四叉树遍历、LOD映射构建和地形片段生成
        /// </summary>
        public void Dispatch()
        {
            //清除上一帧的命令和计数器
            _commandBuffer.Clear();
            ClearBufferCounter();

            //计算视锥体各平面
            UpdateCameraFrustumPlanes(_camera);

            //更新节点评估参数(如果已变脏)
            if (_isNodeEvaluationCDirty)
            {
                _isNodeEvaluationCDirty = false;
                _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.NodeEvaluationC, _nodeEvaluationC);
            }

            //设置相机位置和世界大小
            _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.CameraPositionWS, _camera.transform.position);
            _commandBuffer.SetComputeVectorParam(_computeShader, ShaderConstants.WorldSize, _asset.worldSize);

            //四叉树分割计算得到初步的Patch列表
            _commandBuffer.CopyCounterValue(_maxLODNodeList, _indirectArgsBuffer, 0);
            ComputeBuffer consumeNodeList = _nodeListA;
            ComputeBuffer appendNodeList = _nodeListB;
            
            // 从高到低遍历所有LOD级别
            for (var lod = TerrainAsset.MAX_LOD; lod >= 0; lod--)
            {
                _commandBuffer.SetComputeIntParam(_computeShader, ShaderConstants.PassLOD, lod);
                
                // 为第一次(最大LOD)遍历使用maxLODNodeList，之后使用交替的nodeList
                if (lod == TerrainAsset.MAX_LOD)
                {
                    _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree, ShaderConstants.ConsumeNodeList, _maxLODNodeList);
                }
                else
                {
                    _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree, ShaderConstants.ConsumeNodeList, consumeNodeList);
                }

                _commandBuffer.SetComputeBufferParam(_computeShader, _kernelOfTraverseQuadTree, ShaderConstants.AppendNodeList, appendNodeList);
                
                // 调用计算着色器执行四叉树遍历
                _commandBuffer.DispatchCompute(_computeShader, _kernelOfTraverseQuadTree, _indirectArgsBuffer, 0);
                
                // 准备下一次遍历
                _commandBuffer.CopyCounterValue(appendNodeList, _indirectArgsBuffer, 0);
                (consumeNodeList, appendNodeList) = (appendNodeList, consumeNodeList); // 交换两个缓冲区
            }

            //生成LodMap，用于确定各区域的LOD级别
            _commandBuffer.DispatchCompute(_computeShader, _kernelOfBuildLodMap, 20, 20, 1);

            //生成最终的地形片段
            _commandBuffer.CopyCounterValue(_finalNodeListBuffer, _indirectArgsBuffer, 0);
            _commandBuffer.DispatchCompute(_computeShader, _kernelOfBuildPatches, _indirectArgsBuffer, 0);
            
            // 复制片段数量到间接绘制参数中
            _commandBuffer.CopyCounterValue(_culledPatchBuffer, _patchIndirectArgs, 4);
            
            // 如果启用了包围盒调试，复制包围盒数量
            if (isBoundsBufferOn)
            {
                _commandBuffer.CopyCounterValue(_patchBoundsBuffer, _patchBoundsIndirectArgs, 4);
            }

            // 执行所有命令
            Graphics.ExecuteCommandBuffer(_commandBuffer);

            // 调试用：输出片段参数
            // this.LogPatchArgs();
        }

        // 以下是各种公开的属性，用于获取对应的缓冲区

        /// <summary>
        /// 获取地形片段的间接绘制参数
        /// </summary>
        public ComputeBuffer patchIndirectArgs => _patchIndirectArgs;

        /// <summary>
        /// 获取经过剔除的地形片段缓冲区
        /// </summary>
        public ComputeBuffer culledPatchBuffer => _culledPatchBuffer;

        /// <summary>
        /// 获取节点ID列表
        /// </summary>
        public ComputeBuffer nodeIDList => _finalNodeListBuffer;

        /// <summary>
        /// 获取地形片段包围盒缓冲区
        /// </summary>
        public ComputeBuffer patchBoundsBuffer => _patchBoundsBuffer;

        /// <summary>
        /// 获取包围盒间接绘制参数
        /// </summary>
        public ComputeBuffer boundsIndirectArgs => _patchBoundsIndirectArgs;

        /// <summary>
        /// 着色器常量ID的定义
        /// 用于获取或设置计算着色器中的参数
        /// </summary>
        private class ShaderConstants
        {
            public static readonly int WorldSize = Shader.PropertyToID("_WorldSize");
            public static readonly int CameraPositionWS = Shader.PropertyToID("_CameraPositionWS");
            public static readonly int CameraFrustumPlanes = Shader.PropertyToID("_CameraFrustumPlanes");
            public static readonly int PassLOD = Shader.PropertyToID("PassLOD");
            public static readonly int AppendFinalNodeList = Shader.PropertyToID("AppendFinalNodeList");
            public static readonly int FinalNodeList = Shader.PropertyToID("FinalNodeList");

            public static readonly int AppendNodeList = Shader.PropertyToID("AppendNodeList");
            public static readonly int ConsumeNodeList = Shader.PropertyToID("ConsumeNodeList");
            public static readonly int NodeEvaluationC = Shader.PropertyToID("_NodeEvaluationC");
            public static readonly int WorldLodParams = Shader.PropertyToID("WorldLodParams");

            public static readonly int NodeDescriptors = Shader.PropertyToID("NodeDescriptors");

            public static readonly int LodMap = Shader.PropertyToID("_LodMap");
        }
    }
}