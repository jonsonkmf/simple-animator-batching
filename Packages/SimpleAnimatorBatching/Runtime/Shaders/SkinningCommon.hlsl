#ifndef SIMPLE_ANIMATOR_BATCHING_SKINNING_INCLUDED
#define SIMPLE_ANIMATOR_BATCHING_SKINNING_INCLUDED

// Shared GPU-skinning helpers. Included by EVERY pass (forward, shadow, depth) so the skinned
// pose is byte-for-byte identical across passes — this is what makes self-shadowing line up.
//
// Data contract:
//   _SkinMatrices : StructuredBuffer of world-space matrices (boneLocalToWorld * bindPose), laid
//                   out as [instance * _BoneCount + boneIndex]. Filled each frame on the CPU by
//                   BoneMatrixCollectJob and uploaded by the renderer.
//   boneOffset    : instance * _BoneCount, supplied per-instance via DOTS instancing (_BoneOffset).
//   boneIndices   : mesh UV2 (4 bone indices, padded with weight 0).
//   boneWeights   : mesh UV3 (4 weights matching boneIndices).
//
// Because the matrices already include the world transform, these functions output WORLD space.
// The batched renderer's per-instance unity_ObjectToWorld is identity, so there is no further
// object-to-world step in the passes.

StructuredBuffer<float4x4> _SkinMatrices;

float4x4 SAB_BlendBoneMatrices(float4 boneIndices, float4 boneWeights, uint boneOffset)
{
    float4x4 m =
        _SkinMatrices[boneOffset + (uint)boneIndices.x] * boneWeights.x +
        _SkinMatrices[boneOffset + (uint)boneIndices.y] * boneWeights.y +
        _SkinMatrices[boneOffset + (uint)boneIndices.z] * boneWeights.z +
        _SkinMatrices[boneOffset + (uint)boneIndices.w] * boneWeights.w;
    return m;
}

// Skins a rest-pose object-space position to world space.
float3 SAB_SkinPositionWS(float3 positionOS, float4 boneIndices, float4 boneWeights, uint boneOffset)
{
    float4x4 m = SAB_BlendBoneMatrices(boneIndices, boneWeights, boneOffset);
    return mul(m, float4(positionOS, 1.0)).xyz;
}

// Skins a rest-pose object-space normal to world space (rotation/scale part only).
float3 SAB_SkinNormalWS(float3 normalOS, float4 boneIndices, float4 boneWeights, uint boneOffset)
{
    float4x4 m = SAB_BlendBoneMatrices(boneIndices, boneWeights, boneOffset);
    return normalize(mul((float3x3)m, normalOS));
}

// Convenience: skins position, normal and tangent together.
void SAB_Skin(float3 positionOS, float3 normalOS, float4 tangentOS,
              float4 boneIndices, float4 boneWeights, uint boneOffset,
              out float3 positionWS, out float3 normalWS, out float3 tangentWS)
{
    float4x4 m = SAB_BlendBoneMatrices(boneIndices, boneWeights, boneOffset);
    positionWS = mul(m, float4(positionOS, 1.0)).xyz;
    float3x3 m3 = (float3x3)m;
    normalWS = normalize(mul(m3, normalOS));
    tangentWS = normalize(mul(m3, tangentOS.xyz));
}

#endif // SIMPLE_ANIMATOR_BATCHING_SKINNING_INCLUDED
