using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace SimpleAnimatorBatching
{
    /// <summary>
    /// Collects per-bone skinning matrices for every pooled instance in parallel.
    ///
    /// The transforms are stored in one flat <see cref="TransformAccessArray"/> laid out as
    /// [slot0 bone0, slot0 bone1, ..., slot1 bone0, ...]; the bone within a slot is recovered with
    /// <c>index % boneCount</c>. Unity skips transforms whose GameObject is inactive, so despawned
    /// pool slots cost nothing here and simply keep their previous (unused) matrices.
    ///
    /// The matrix written is <c>boneLocalToWorld * bindPose</c>: it maps a rest-pose vertex (in mesh
    /// space) straight to world space. The batched renderer's per-instance ObjectToWorld is identity,
    /// so the shader can output this world position directly.
    /// </summary>
    [BurstCompile]
    internal struct BoneMatrixCollectJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<float4x4> bindPoses; // length == boneCount
        public int boneCount;

        // Each index is written exactly once (1:1 with the transform index), so the parallel-for
        // single-index write restriction is safe to disable.
        [NativeDisableParallelForRestriction] public NativeArray<float4x4> skinMatrices;

        public void Execute(int index, TransformAccess transform)
        {
            int bone = index % boneCount;

            Matrix4x4 ltw = transform.localToWorldMatrix;
            float4x4 world = new float4x4(ltw.GetColumn(0), ltw.GetColumn(1), ltw.GetColumn(2), ltw.GetColumn(3));

            skinMatrices[index] = math.mul(world, bindPoses[bone]);
        }
    }
}
