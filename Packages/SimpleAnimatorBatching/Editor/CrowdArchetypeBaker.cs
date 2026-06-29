using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SimpleAnimatorBatching.Editor
{
    /// <summary>
    /// Editor-only baking logic. Takes a source prefab (SkinnedMeshRenderer + Animator) and
    /// produces everything a CrowdArchetypeAsset needs at runtime:
    ///   - a runtime prefab with the SkinnedMeshRenderer stripped and Animator forced to
    ///     AlwaysAnimate culling,
    ///   - a baked copy of the mesh with boneIndices/boneWeights packed into UV2/UV3,
    ///   - bind poses and structural bone paths in the same order.
    ///
    /// This is intentionally a single static method behind a bake button for now (plan step 1).
    /// The full guided wizard (validation messages, prefab variants, batch baking) is a later step.
    /// </summary>
    public static class CrowdArchetypeBaker
    {
        public static void Bake(CrowdArchetypeAsset archetype)
        {
            if (archetype.sourcePrefab == null)
                throw new InvalidOperationException("Source Prefab is not assigned.");

            if (archetype.sourcePrefab.GetComponentInChildren<SkinnedMeshRenderer>() == null)
                throw new InvalidOperationException(
                    $"'{archetype.sourcePrefab.name}' has no SkinnedMeshRenderer in its hierarchy.");

            var sourceAnimator = archetype.sourcePrefab.GetComponentInChildren<Animator>();
            if (sourceAnimator == null)
                throw new InvalidOperationException(
                    $"'{archetype.sourcePrefab.name}' has no Animator in its hierarchy.");

            // 1. Work on an in-scene instance so GetChild() paths and bone Transforms are stable
            //    and so we can safely strip components without touching the original asset.
            var instanceRoot = (GameObject)UnityEngine.Object.Instantiate(archetype.sourcePrefab);
            instanceRoot.name = archetype.sourcePrefab.name + "_Runtime";

            try
            {
                // v1 renders one mesh per archetype. If the model ships several SkinnedMeshRenderers
                // (e.g. X Bot has a body mesh + a low-poly joint-helper mesh), bake the highest-vertex
                // one as the body and strip them ALL. Multi-mesh characters are a v2 feature.
                var allSmrs = instanceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var instanceSmr = allSmrs[0];
                for (int s = 1; s < allSmrs.Length; s++)
                {
                    if (allSmrs[s].sharedMesh == null) continue;
                    if (instanceSmr.sharedMesh == null ||
                        allSmrs[s].sharedMesh.vertexCount > instanceSmr.sharedMesh.vertexCount)
                        instanceSmr = allSmrs[s];
                }
                if (allSmrs.Length > 1)
                    Debug.LogWarning(
                        $"[Simple Animator Batching] '{archetype.sourcePrefab.name}' has {allSmrs.Length} " +
                        $"SkinnedMeshRenderers. Baking '{instanceSmr.name}' (most vertices) and stripping the " +
                        "rest — v1 supports a single mesh per archetype.");

                var instanceAnimator = instanceRoot.GetComponentInChildren<Animator>();

                Mesh sourceMesh = instanceSmr.sharedMesh;
                if (sourceMesh == null)
                    throw new InvalidOperationException("Selected SkinnedMeshRenderer has no mesh assigned.");

                Transform[] bones = instanceSmr.bones;
                Matrix4x4[] bindPoses = sourceMesh.bindposes;

                if (bones.Length != bindPoses.Length)
                    throw new InvalidOperationException(
                        "Bone count does not match bindpose count — unexpected mesh/rig setup.");

                // 2. Bake structural bone paths relative to the instance root.
                var bonePaths = new BonePath[bones.Length];
                var pathBuffer = new List<int>(16);
                for (int i = 0; i < bones.Length; i++)
                {
                    pathBuffer.Clear();
                    if (!TryGetChildIndexPath(instanceRoot.transform, bones[i], pathBuffer))
                        throw new InvalidOperationException(
                            $"Bone '{bones[i].name}' could not be located inside the instantiated hierarchy.");
                    bonePaths[i] = new BonePath { childIndices = pathBuffer.ToArray() };
                }

                int hierarchyHash = CrowdArchetypeAsset.ComputeHierarchyHash(instanceRoot.transform);

                // 3. Bake a working copy of the mesh with boneIndices/boneWeights in UV2/UV3.
                Mesh bakedMesh = UnityEngine.Object.Instantiate(sourceMesh);
                bakedMesh.name = sourceMesh.name + "_Baked";
                BakeBoneWeightsToMesh(bakedMesh);

                // 4. Strip the SkinnedMeshRenderer — rendering happens through the batched
                //    pipeline instead — and force the Animator to keep evaluating even though
                //    nothing is rendered through this GameObject anymore.
                foreach (var smr in allSmrs)
                    UnityEngine.Object.DestroyImmediate(smr);
                instanceAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // 5. Persist baked mesh + runtime prefab as assets next to the archetype, at stable
                //    paths. Re-baking OVERWRITES the previous outputs rather than spawning
                //    "_Mesh 1", "_Mesh 2", ... orphans on every bake.
                string archetypePath = AssetDatabase.GetAssetPath(archetype);
                string folder = Path.GetDirectoryName(archetypePath);
                string baseName = Path.GetFileNameWithoutExtension(archetypePath);

                string meshPath = Path.Combine(folder, baseName + "_Mesh.asset").Replace('\\', '/');
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                    AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(bakedMesh, meshPath);

                string prefabPath = Path.Combine(folder, baseName + "_RuntimePrefab.prefab").Replace('\\', '/');
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                    AssetDatabase.DeleteAsset(prefabPath);
                PrefabUtility.SaveAsPrefabAsset(instanceRoot, prefabPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // 6. Write everything into the archetype asset.
                archetype.runtimePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                archetype.bakedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                archetype.bindPoses = bindPoses;
                archetype.bonePaths = bonePaths;
                archetype.boneCount = bones.Length;
                archetype.hierarchyStructureHash = hierarchyHash;

                EditorUtility.SetDirty(archetype);
                AssetDatabase.SaveAssets();

                Debug.Log(
                    $"[Simple Animator Batching] Baked '{archetype.sourcePrefab.name}': " +
                    $"{bones.Length} bones. Mesh saved to '{meshPath}', runtime prefab saved to '{prefabPath}'.\n" +
                    "Note: the runtime prefab has no Renderer, so dragging it into a scene won't show " +
                    "anything visually yet — that's expected until the GPU-skinned renderer (later plan steps) " +
                    "is wired up. To sanity-check the bake, select a bone inside an instance of the runtime " +
                    "prefab in Play mode and confirm its rotation changes while the Animator plays.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instanceRoot);
            }
        }

        static bool TryGetChildIndexPath(Transform root, Transform target, List<int> pathOut)
        {
            if (root == target) return true;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                pathOut.Add(i);
                if (TryGetChildIndexPath(child, target, pathOut)) return true;
                pathOut.RemoveAt(pathOut.Count - 1);
            }
            return false;
        }

        static void BakeBoneWeightsToMesh(Mesh mesh)
        {
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();

            int vertexCount = mesh.vertexCount;
            var boneIndices = new Vector4[vertexCount];
            var boneWeightsOut = new Vector4[vertexCount];

            int cursor = 0;
            int maxObservedInfluences = 0;

            for (int v = 0; v < vertexCount; v++)
            {
                int count = bonesPerVertex[v];
                if (count > maxObservedInfluences) maxObservedInfluences = count;

                int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
                float w0 = 0f, w1 = 0f, w2 = 0f, w3 = 0f;

                int clamped = Mathf.Min(count, 4);
                for (int k = 0; k < clamped; k++)
                {
                    var bw = weights[cursor + k];
                    switch (k)
                    {
                        case 0: i0 = bw.boneIndex; w0 = bw.weight; break;
                        case 1: i1 = bw.boneIndex; w1 = bw.weight; break;
                        case 2: i2 = bw.boneIndex; w2 = bw.weight; break;
                        case 3: i3 = bw.boneIndex; w3 = bw.weight; break;
                    }
                }
                cursor += count;

                boneIndices[v] = new Vector4(i0, i1, i2, i3);
                boneWeightsOut[v] = new Vector4(w0, w1, w2, w3);
            }

            if (maxObservedInfluences > 4)
            {
                Debug.LogWarning(
                    $"[Simple Animator Batching] Mesh '{mesh.name}' has vertices with more than 4 bone " +
                    "influences. Only the 4 highest-weighted influences per vertex are kept; this can " +
                    "visibly distort skinning on the affected vertices (commonly face/cloth rigs).");
            }

            mesh.SetUVs(2, boneIndices);
            mesh.SetUVs(3, boneWeightsOut);
        }
    }
}
