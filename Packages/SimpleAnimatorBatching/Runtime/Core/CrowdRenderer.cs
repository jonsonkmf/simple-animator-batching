using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleAnimatorBatching
{
    /// <summary>
    /// Draws every active pool instance of one archetype in a single BatchRendererGroup batch — one
    /// BatchDrawCommand, hence one draw call per render pass (camera + each shadow cascade).
    ///
    /// Per-instance data (identity unity_ObjectToWorld / unity_WorldToObject, plus the _BoneOffset
    /// into the skin-matrix buffer) lives in a raw GraphicsBuffer addressed via DOTS instancing
    /// metadata. The actual skinning matrices live in a separate StructuredBuffer (_SkinMatrices),
    /// refreshed each frame from <see cref="BoneMatrixCollectJob"/>.
    ///
    /// v1 culling: OnPerformCulling emits every active instance for every pass (no frustum test yet),
    /// which is correct but unoptimised — real per-pass frustum culling is a later step.
    /// </summary>
    internal sealed unsafe class CrowdRenderer : IDisposable
    {
        const int kSizeOfMatrix = sizeof(float) * 16;        // 64, float4x4 in the skin buffer
        const int kSizeOfPackedMatrix = sizeof(float) * 12;  // 48, float3x4 (last row dropped)
        const int kSizeOfFloat4 = sizeof(float) * 4;         // 16

        static readonly int s_SkinMatricesID = Shader.PropertyToID("_SkinMatrices");

        readonly int capacity;
        readonly int boneCount;
        readonly bool[] activeMask;             // shared with the spawner; read in OnPerformCulling
        readonly NativeArray<float3> instanceCenters; // shared; world-space cull sphere centers
        readonly float boundingRadius;          // cull sphere radius per instance

        BatchRendererGroup brg;
        GraphicsBuffer instanceBuffer; // raw: per-instance identity matrices + bone offsets
        GraphicsBuffer skinBuffer;     // structured float4x4[capacity * boneCount]
        BatchID batchID;
        BatchMeshID meshID;
        BatchMaterialID materialID;
        bool batchAdded;

        public CrowdRenderer(Mesh mesh, Material material, int capacity, int boneCount,
            bool[] activeMask, NativeArray<float3> instanceCenters, float boundingRadius)
        {
            this.capacity = capacity;
            this.boneCount = boneCount;
            this.activeMask = activeMask;
            this.instanceCenters = instanceCenters;
            this.boundingRadius = boundingRadius;

            brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
            // Generous global bounds so the group itself is never culled; per-instance culling (later)
            // happens inside OnPerformCulling.
            brg.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 100000f));

            meshID = brg.RegisterMesh(mesh);
            materialID = brg.RegisterMaterial(material);

            skinBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * boneCount, kSizeOfMatrix);
            material.SetBuffer(s_SkinMatricesID, skinBuffer);

            AllocateAndFillInstanceBuffer();
        }

        void AllocateAndFillInstanceBuffer()
        {
            int bytesPerInstance = (kSizeOfPackedMatrix * 2) + kSizeOfFloat4;
            int extraBytes = kSizeOfMatrix * 2; // leading pad, mirrors Unity's BRG sample
            int totalBytes = bytesPerInstance * capacity + extraBytes;

            uint byteAddressObjectToWorld = (uint)(kSizeOfPackedMatrix * 2);
            uint byteAddressWorldToObject = byteAddressObjectToWorld + (uint)(kSizeOfPackedMatrix * capacity);
            uint byteAddressBoneOffset = byteAddressWorldToObject + (uint)(kSizeOfPackedMatrix * capacity);

            instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalBytes / 4, 4);

            var data = new float[totalBytes / 4];

            int owFloat = (int)(byteAddressObjectToWorld / 4);
            int woFloat = (int)(byteAddressWorldToObject / 4);
            int boFloat = (int)(byteAddressBoneOffset / 4);

            for (int i = 0; i < capacity; i++)
            {
                WritePackedIdentity(data, owFloat + i * 12);
                WritePackedIdentity(data, woFloat + i * 12);
                data[boFloat + i * 4] = i * boneCount; // _BoneOffset.x for this instance
            }

            instanceBuffer.SetData(data);

            var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | byteAddressObjectToWorld };
            metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | byteAddressWorldToObject };
            metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BoneOffset"), Value = 0x80000000 | byteAddressBoneOffset };

            batchID = brg.AddBatch(metadata, instanceBuffer.bufferHandle);
            batchAdded = true;
            metadata.Dispose();
        }

        // float3x4 packed identity: columns c0,c1,c2,c3 each xyz (translation row dropped).
        static void WritePackedIdentity(float[] data, int f)
        {
            data[f + 0] = 1f; data[f + 1] = 0f; data[f + 2] = 0f; // c0
            data[f + 3] = 0f; data[f + 4] = 1f; data[f + 5] = 0f; // c1
            data[f + 6] = 0f; data[f + 7] = 0f; data[f + 8] = 1f; // c2
            data[f + 9] = 0f; data[f + 10] = 0f; data[f + 11] = 0f; // c3 (translation)
        }

        /// <summary>Uploads this frame's skinning matrices to the GPU. Call after the collect job completes.</summary>
        public void UploadSkinMatrices(NativeArray<float4x4> skinMatrices)
        {
            if (skinBuffer != null && skinMatrices.IsCreated)
                skinBuffer.SetData(skinMatrices);
        }

        JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            int activeCount = 0;
            for (int s = 0; s < capacity; s++)
                if (activeMask[s]) activeCount++;

            if (activeCount == 0)
                return new JobHandle();

            var planes = cullingContext.cullingPlanes;   // inward-facing frustum planes (all splits)
            var splits = cullingContext.cullingSplits;   // camera: 1 split; shadows: 1 per cascade
            int splitCount = splits.Length;
            if (splitCount == 0)
                return new JobHandle();

            int alignment = UnsafeUtility.AlignOf<long>();

            // Worst case: every active instance visible in every split. One draw command per split.
            int maxVisible = activeCount * splitCount;
            int* visible = (int*)UnsafeUtility.Malloc(maxVisible * sizeof(int), alignment, Allocator.TempJob);
            int* splitOffset = stackalloc int[splitCount];
            int* splitCounts = stackalloc int[splitCount];

            int cursor = 0;
            for (int si = 0; si < splitCount; si++)
            {
                splitOffset[si] = cursor;
                var split = splits[si];
                int planeBegin = split.cullingPlaneOffset;
                int planeEnd = planeBegin + split.cullingPlaneCount;

                for (int s = 0; s < capacity; s++)
                {
                    if (!activeMask[s]) continue;
                    if (SphereInsidePlanes(instanceCenters[s], boundingRadius, planes, planeBegin, planeEnd))
                        visible[cursor++] = s;
                }
                splitCounts[si] = cursor - splitOffset[si];
            }

            int totalVisible = cursor;
            if (totalVisible == 0)
            {
                UnsafeUtility.Free(visible, Allocator.TempJob);
                return new JobHandle();
            }

            int cmdCount = 0;
            for (int si = 0; si < splitCount; si++)
                if (splitCounts[si] > 0) cmdCount++;

            var drawCommands = new BatchCullingOutputDrawCommands
            {
                visibleInstances = visible,
                visibleInstanceCount = totalVisible,
                drawCommandCount = cmdCount,
                drawRangeCount = 1,
                instanceSortingPositions = null,
                instanceSortingPositionFloatCount = 0,
            };
            drawCommands.drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawCommand>() * cmdCount, alignment, Allocator.TempJob);
            drawCommands.drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawRange>(), alignment, Allocator.TempJob);

            int ci = 0;
            for (int si = 0; si < splitCount; si++)
            {
                if (splitCounts[si] == 0) continue;
                drawCommands.drawCommands[ci++] = new BatchDrawCommand
                {
                    visibleOffset = (uint)splitOffset[si],
                    visibleCount = (uint)splitCounts[si],
                    batchID = batchID,
                    materialID = materialID,
                    meshID = meshID,
                    submeshIndex = 0,
                    splitVisibilityMask = (ushort)(1u << si),
                    flags = BatchDrawCommandFlags.None,
                    sortingPosition = 0,
                };
            }

            drawCommands.drawRanges[0] = new BatchDrawRange
            {
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)cmdCount,
                filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = 0xffffffff,
                    layer = 0,
                    motionMode = MotionVectorGenerationMode.ForceNoMotion,
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                    staticShadowCaster = false,
                    allDepthSorted = false,
                },
            };

            cullingOutput.drawCommands[0] = drawCommands;
            return new JobHandle();
        }

        // Sphere vs. a contiguous range of inward-facing planes: outside if it lies fully behind any plane.
        static bool SphereInsidePlanes(float3 center, float radius, NativeArray<Plane> planes, int begin, int end)
        {
            for (int i = begin; i < end; i++)
            {
                Plane p = planes[i];
                float d = p.normal.x * center.x + p.normal.y * center.y + p.normal.z * center.z + p.distance;
                if (d < -radius)
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (brg != null)
            {
                if (batchAdded)
                {
                    brg.RemoveBatch(batchID);
                    batchAdded = false;
                }
                brg.Dispose();
                brg = null;
            }

            instanceBuffer?.Dispose();
            instanceBuffer = null;
            skinBuffer?.Dispose();
            skinBuffer = null;
        }
    }
}
