using System;
using UnityEngine;

namespace SimpleAnimatorBatching
{
    /// <summary>
    /// Structural path to a bone Transform, expressed as a sequence of GetChild(i) indices
    /// from an instance's root. Indices are used instead of Transform references or names
    /// because the same archetype must resolve bones inside N independently instantiated
    /// copies of the runtime prefab.
    /// </summary>
    [Serializable]
    public struct BonePath
    {
        public int[] childIndices;
    }

    /// <summary>
    /// Baked data describing one skeleton+mesh archetype: bind poses, structural bone paths,
    /// the runtime prefab (Animator only, SkinnedMeshRenderer stripped), and the baked mesh
    /// (with boneIndices/boneWeights packed into UV2/UV3 for GPU skinning).
    ///
    /// Baking itself happens in the Editor assembly (see CrowdArchetypeBaker). This class only
    /// holds the resulting data plus the runtime-safe lookups that the spawner/pool need every
    /// time a pooled instance is warmed up.
    /// </summary>
    [CreateAssetMenu(fileName = "CrowdArchetype", menuName = "Simple Animator Batching/Crowd Archetype")]
    public class CrowdArchetypeAsset : ScriptableObject
    {
        [Header("Source (editor-only, used for re-baking)")]
        [SerializeField] internal GameObject sourcePrefab;

        [Header("Baked runtime data")]
        [SerializeField] internal GameObject runtimePrefab;
        [SerializeField] internal Mesh bakedMesh;
        [SerializeField] internal Material material;

        [SerializeField] internal Matrix4x4[] bindPoses;
        [SerializeField] internal BonePath[] bonePaths;
        [SerializeField] internal int boneCount;
        [SerializeField] internal int hierarchyStructureHash;

        [Header("Pool")]
        [Tooltip("Fixed pool capacity. Spawn() fails once this many instances are simultaneously active. " +
                 "Size this for the real peak of concurrently alive instances (e.g. the biggest wave).")]
        [SerializeField] internal int maxInstances = 64;

        public GameObject RuntimePrefab => runtimePrefab;
        public Mesh BakedMesh => bakedMesh;
        public Material Material => material;
        public int BoneCount => boneCount;
        public int MaxInstances => maxInstances;
        public Matrix4x4[] BindPoses => bindPoses;

        public bool IsBaked =>
            runtimePrefab != null &&
            bakedMesh != null &&
            bonePaths != null &&
            bonePaths.Length > 0 &&
            bindPoses != null &&
            bonePaths.Length == bindPoses.Length;

        /// <summary>
        /// Resolves the Transform for a given bone index inside a specific pooled instance's
        /// hierarchy. Intended to be called once per instance during pool warm-up (to build a
        /// flat TransformAccessArray), not every frame.
        /// </summary>
        public Transform ResolveBone(Transform instanceRoot, int boneIndex)
        {
            if (bonePaths == null || boneIndex < 0 || boneIndex >= bonePaths.Length)
                throw new ArgumentOutOfRangeException(nameof(boneIndex));

            Transform t = instanceRoot;
            int[] path = bonePaths[boneIndex].childIndices;
            for (int i = 0; i < path.Length; i++)
            {
                int childIndex = path[i];
                if (childIndex < 0 || childIndex >= t.childCount)
                {
                    throw new InvalidOperationException(
                        $"CrowdArchetypeAsset '{name}': bone path for bone {boneIndex} no longer matches " +
                        $"the hierarchy of '{instanceRoot.name}'. The runtime prefab was likely edited after " +
                        "baking — re-bake the archetype from its source prefab.");
                }
                t = t.GetChild(childIndex);
            }
            return t;
        }

        /// <summary>
        /// Recomputes the structural hierarchy hash for a given instance root and compares it
        /// against the hash captured at bake time. Call once per instance during pool warm-up,
        /// before relying on ResolveBone for that instance.
        /// </summary>
        public void ValidateHierarchyOrThrow(Transform instanceRoot)
        {
            int currentHash = ComputeHierarchyHash(instanceRoot);
            if (currentHash != hierarchyStructureHash)
            {
                throw new InvalidOperationException(
                    $"CrowdArchetypeAsset '{name}': hierarchy hash mismatch for '{instanceRoot.name}' " +
                    $"(expected {hierarchyStructureHash}, got {currentHash}). The runtime prefab was likely " +
                    "edited after baking — re-bake the archetype from its source prefab.");
            }
        }

        /// <summary>
        /// Structural hash of a Transform hierarchy based on child counts only. Used to detect
        /// whether the runtime prefab's hierarchy changed since the archetype was baked.
        /// Internal, but shared with the Editor-side baker so both sides compute it identically.
        /// </summary>
        internal static int ComputeHierarchyHash(Transform root)
        {
            int hash = 17;

            void Walk(Transform t)
            {
                hash = hash * 31 + t.childCount;
                for (int i = 0; i < t.childCount; i++)
                    Walk(t.GetChild(i));
            }

            Walk(root);
            return hash;
        }
    }
}
