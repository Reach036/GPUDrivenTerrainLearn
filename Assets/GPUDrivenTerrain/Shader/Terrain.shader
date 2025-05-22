Shader "GPUTerrainLearn/Terrain"
{
    Properties
    {
        _MainTex ("_MainTex", 2D) = "white" {}
        _HeightMap ("_HeightMap", 2D) = "white" {}
        _NormalMap ("_NormalMap", 2D) = "white" {}
        _ShadowStrength("Shadow Strength", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "LightMode" = "UniversalForward"}
        LOD 100

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // Shadow keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #pragma shader_feature ENABLE_MIP_DEBUG
            #pragma shader_feature ENABLE_PATCH_DEBUG
            #pragma shader_feature ENABLE_LOD_SEAMLESS
            #pragma shader_feature ENABLE_NODE_DEBUG
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "./CommonInput.hlsl"

            StructuredBuffer<RenderPatch> PatchList;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                half3 color: TEXCOORD1;
                float3 positionWS : TEXCOORD2; // 世界空间位置，用于阴影采样
                float4 shadowCoord : TEXCOORD3; // 阴影坐标
                float shadowFade : TEXCOORD4;   // 新增：阴影淡出因子
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _HeightMap;
            sampler2D _NormalMap;
            uniform float3 _WorldSize;
            float4x4 _WorldToNormalMapMatrix;
            float _ShadowStrength;
            float _ShadowMaxDistance;

            static half3 debugColorForMip[6] = {
                half3(0, 1, 0),
                half3(0, 0, 1),
                half3(1, 0, 0),
                half3(1, 1, 0),
                half3(0, 1, 1),
                half3(1, 0, 1),
            };

            float3 TransformNormalToWorldSpace(float3 normal)
            {
                return SafeNormalize(mul(normal, (float3x3)_WorldToNormalMapMatrix));
            }

            float3 SampleNormal(float2 uv)
            {
                float3 normal;
                normal.xz = tex2Dlod(_NormalMap, float4(uv, 0, 0)).xy * 2 - 1;
                normal.y = sqrt(max(0, 1 - dot(normal.xz, normal.xz)));
                normal = TransformNormalToWorldSpace(normal);
                return normal;
            }

            //修复接缝
            void FixLODConnectSeam(inout float4 vertex, inout float2 uv, RenderPatch patch)
            {
                //但是这样采样出来 还是会有接缝.因为 uv相同但lod不同 采样的高度图会得到不同的高度.就出现接缝.这里需要把处于边线的点,取max(A块lod,B块lod)
                uint4 lodTrans = patch.lodTrans;
                uint2 vertexIndex = floor((vertex.xz + PATCH_MESH_SIZE * 0.5 + 0.01) / PATCH_MESH_GRID_SIZE);
                float uvGridStrip = 1.0 / PATCH_MESH_GRID_COUNT; //一个网格在UV坐标上的宽度

                uint lodDelta = lodTrans.x;
                if (lodDelta > 0 && vertexIndex.x == 0)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.y % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.z -= PATCH_MESH_GRID_SIZE * modIndex;
                        uv.y -= uvGridStrip * modIndex;
                        return;
                    }
                }

                lodDelta = lodTrans.y;
                if (lodDelta > 0 && vertexIndex.y == 0)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.x % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.x -= PATCH_MESH_GRID_SIZE * modIndex;
                        uv.x -= uvGridStrip * modIndex;
                        return;
                    }
                }

                lodDelta = lodTrans.z;
                if (lodDelta > 0 && vertexIndex.x == PATCH_MESH_GRID_COUNT)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.y % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.z += PATCH_MESH_GRID_SIZE * (gridStripCount - modIndex);
                        uv.y += uvGridStrip * (gridStripCount - modIndex);
                        return;
                    }
                }

                lodDelta = lodTrans.w;
                if (lodDelta > 0 && vertexIndex.y == PATCH_MESH_GRID_COUNT)
                {
                    uint gridStripCount = pow(2, lodDelta);
                    uint modIndex = vertexIndex.x % gridStripCount;
                    if (modIndex != 0)
                    {
                        vertex.x += PATCH_MESH_GRID_SIZE * (gridStripCount - modIndex);
                        uv.x += uvGridStrip * (gridStripCount - modIndex);
                        return;
                    }
                }
            }

            //在Node之间留出缝隙供Debug
            float3 ApplyNodeDebug(RenderPatch patch, float3 vertex)
            {
                uint nodeCount = (uint)(5 * pow(2, 5 - patch.lod));
                float nodeSize = _WorldSize.x / nodeCount;
                uint2 nodeLoc = floor((patch.position + _WorldSize.xz * 0.5) / nodeSize);
                float2 nodeCenterPosition = -_WorldSize.xz * 0.5 + (nodeLoc + 0.5) * nodeSize;
                vertex.xz = nodeCenterPosition + (vertex.xz - nodeCenterPosition) * 0.95;
                return vertex;
            }

            uniform float4x4 _HizCameraMatrixVP;

            float3 TransformWorldToUVD(float3 positionWS)
            {
                float4 positionHS = mul(_HizCameraMatrixVP, float4(positionWS, 1.0));
                float3 uvd = positionHS.xyz / positionHS.w;
                uvd.xy = (uvd.xy + 1) * 0.5;
                return uvd;
            }

            v2f vert(appdata v)
            {
                v2f o;

                float4 inVertex = v.vertex;
                float2 uv = v.uv;

                RenderPatch patch = PatchList[v.instanceID];
                #if ENABLE_LOD_SEAMLESS
                FixLODConnectSeam(inVertex,uv,patch);
                #endif
                uint lod = patch.lod;
                float scale = pow(2, lod);

                uint4 lodTrans = patch.lodTrans;

                inVertex.xz *= scale;
                #if ENABLE_PATCH_DEBUG
                inVertex.xz *= 0.9;
                #endif
                inVertex.xz += patch.position;

                #if ENABLE_NODE_DEBUG
                inVertex.xyz = ApplyNodeDebug(patch,inVertex.xyz);
                #endif

                float2 heightUV = (inVertex.xz + (_WorldSize.xz * 0.5) + 0.5) / (_WorldSize.xz + 1);
                float height = tex2Dlod(_HeightMap, float4(heightUV, 0, 0)).r;
                inVertex.y = height * _WorldSize.y;

                float3 normal = SampleNormal(heightUV);
                Light light = GetMainLight();
                o.color = max(0.05, dot(light.direction, normal));

                // 保存世界空间位置用于阴影采样
                o.positionWS = TransformObjectToWorld(inVertex.xyz);
                float4 vertex = TransformObjectToHClip(inVertex.xyz);
                o.vertex = vertex;
                o.uv = uv * scale * 8;

                float3 cameraPosWS = _WorldSpaceCameraPos;
                float distanceToCamera = distance(o.positionWS, cameraPosWS);

                // 获取 URP 中设置的阴影距离参数
                float shadowDistance = _ShadowMaxDistance; // URP 的阴影最大距离
                float shadowFadeRange = shadowDistance * 0.1; // 淡出范围（默认10%）

                // 计算淡出因子 [0,1]（0=完全阴影，1=无阴影）
                o.shadowFade = saturate((distanceToCamera - (shadowDistance - shadowFadeRange)) / shadowFadeRange);
                // 计算阴影坐标
                if (o.shadowFade < 1.0)
                {
                    o.shadowCoord = TransformWorldToShadowCoord(o.positionWS);
                }
                else
                {
                    o.shadowCoord = float4(0, 0, 0, 0); // 超出距离时清空阴影坐标
                }

                #if ENABLE_MIP_DEBUG
                    uint4 lodColorIndex = lod + lodTrans;
                    o.color *= (debugColorForMip[lodColorIndex.x] + 
                    debugColorForMip[lodColorIndex.y] +
                    debugColorForMip[lodColorIndex.z] +
                    debugColorForMip[lodColorIndex.w]) * 0.25;
                #endif

                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // 采样基础纹理
                half4 col = tex2D(_MainTex, i.uv);

                // 应用基础光照
                col.rgb *= i.color;
                
                // 计算阴影
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                if (i.shadowFade < 1.0) 
                {
                    Light mainLight = GetMainLight(i.shadowCoord);
                    float shadowAtten = mainLight.shadowAttenuation;
                    
                    // 应用阴影淡出（与 URP 内置逻辑一致）
                    shadowAtten = lerp(shadowAtten, 1.0, i.shadowFade);
                    // 应用阴影强度
                    shadowAtten = lerp(1.0, shadowAtten, _ShadowStrength);
                    // 应用阴影到基础颜色
                    col.rgb *= shadowAtten;
                }
                #endif

                //TODO 额外光源的阴影（如果需要）
                #ifdef _ADDITIONAL_LIGHT_SHADOWS
                
                #endif

                return col;
            }
            ENDHLSL
        }
    }
}