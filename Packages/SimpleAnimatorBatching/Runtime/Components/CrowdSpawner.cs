using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace SimpleAnimatorBatching
{
    /// <summary>
    /// Owns a fixed-capacity pool of pooled crowd instances for a single archetype and exposes a
    /// dynamic Spawn/Despawn API (designed for "waves of enemies": instances come and go, but the
    /// underlying GameObjects, bone Transforms and — later — GPU buffers are allocated exactly once).
    ///
    /// v1 scope of THIS component: the instance pool, generation-checked handles, and anti-teleport
    /// on spawn. The GPU-skinned batched renderer (BoneMatrixCollectJob + BatchRendererGroup) is
    /// layered on in later steps; until then pooled instances animate but are not drawn (their
    /// SkinnedMeshRenderer was stripped at bake time).
    /// </summary>
    [AddComponentMenu("Simple Animator Batching/Crowd Spawner")]
    public class CrowdSpawner : MonoBehaviour
    {
        [Tooltip("Baked archetype to spawn instances of. Must be baked (see its inspector's Bake button).")]
        [SerializeField] CrowdArchetypeAsset archetype;

        [Tooltip("Animator state played from the start on every Spawn(), on the layer below. " +
                 "Leave empty to only Rebind() without forcing a specific entry state. " +
                 "For the X Bot demo this is \"Taunt\".")]
        [SerializeField] string defaultStateName = "";

        [Tooltip("Animator layer the default state lives on.")]
        [SerializeField] int defaultStateLayer = 0;

        [Tooltip("Warm up the pool automatically in Awake. Disable to call WarmUp() yourself.")]
        [SerializeField] bool warmUpOnAwake = true;

        [Tooltip("Radius of the per-instance bounding sphere used for frustum culling, anchored at the " +
                 "instance's root position. Make it large enough to cover the character in any pose " +
                 "(arms raised, jumping). Too small causes instances to pop out at screen edges.")]
        [SerializeField] float cullingBoundingRadius = 2f;

        // --- Pool state (allocated once in WarmUp) ---
        GameObject[] instances;
        Transform[] instanceRoots;
        Animator[] animators;
        bool[] activeMask;
        int[] generation;
        Stack<int> freeSlots;
        Transform poolContainer;

        int defaultStateHash;
        bool warmedUp;

        // --- GPU-skinning data (allocated once in WarmUp, disposed in OnDestroy) ---
        // Flat layout: index = slot * boneCount + bone.
        TransformAccessArray boneTransforms;
        NativeArray<float4x4> skinMatrices;
        NativeArray<float4x4> bindPosesNative;
        NativeArray<float3> instanceCenters; // world-space cull-sphere centers, one per slot
        JobHandle collectHandle;
        bool collectScheduled;
        int boneCount;

        CrowdRenderer crowdRenderer;

        /// <summary>
        /// World-space skinning matrices for every (slot, bone), laid out as slot*BoneCount + bone.
        /// Refreshed every LateUpdate from the live Animator poses. The batched renderer reads this to
        /// fill its GraphicsBuffer. Only slots that are currently active hold meaningful values.
        /// </summary>
        internal NativeArray<float4x4> SkinMatrices => skinMatrices;
        internal int BoneCount => boneCount;

        public CrowdArchetypeAsset Archetype => archetype;
        public bool IsWarmedUp => warmedUp;
        public int Capacity => warmedUp ? instances.Length : (archetype != null ? archetype.MaxInstances : 0);
        public int ActiveCount => warmedUp ? instances.Length - freeSlots.Count : 0;

        void Awake()
        {
            if (warmUpOnAwake)
                WarmUp();
        }

        /// <summary>
        /// Allocates the whole pool: instantiates MaxInstances copies of the runtime prefab once,
        /// validates their skeleton against the baked archetype, and parks them inactive. Safe to
        /// call once; subsequent calls are ignored.
        /// </summary>
        public void WarmUp()
        {
            if (warmedUp)
                return;

            if (archetype == null)
            {
                Debug.LogError("[Simple Animator Batching] CrowdSpawner has no archetype assigned.", this);
                return;
            }
            if (!archetype.IsBaked || archetype.RuntimePrefab == null)
            {
                Debug.LogError(
                    $"[Simple Animator Batching] Archetype '{archetype.name}' is not baked. " +
                    "Open it and press Bake before spawning.", this);
                return;
            }

            int capacity = Mathf.Max(1, archetype.MaxInstances);
            boneCount = archetype.BoneCount;

            instances = new GameObject[capacity];
            instanceRoots = new Transform[capacity];
            animators = new Animator[capacity];
            activeMask = new bool[capacity];
            generation = new int[capacity];
            freeSlots = new Stack<int>(capacity);

            defaultStateHash = string.IsNullOrEmpty(defaultStateName) ? 0 : Animator.StringToHash(defaultStateName);

            // One flat TransformAccessArray over every (slot, bone), and the matching matrix buffers.
            boneTransforms = new TransformAccessArray(capacity * boneCount);
            skinMatrices = new NativeArray<float4x4>(capacity * boneCount, Allocator.Persistent);
            bindPosesNative = new NativeArray<float4x4>(boneCount, Allocator.Persistent);
            instanceCenters = new NativeArray<float3>(capacity, Allocator.Persistent);
            Matrix4x4[] bindPoses = archetype.BindPoses;
            for (int b = 0; b < boneCount; b++)
                bindPosesNative[b] = bindPoses[b];

            var container = new GameObject($"{archetype.name} Pool");
            poolContainer = container.transform;
            poolContainer.SetParent(transform, worldPositionStays: false);

            for (int slot = 0; slot < capacity; slot++)
            {
                var go = Instantiate(archetype.RuntimePrefab, poolContainer);
                go.name = $"{archetype.RuntimePrefab.name} [{slot}]";

                var root = go.transform;
                var animator = go.GetComponentInChildren<Animator>(includeInactive: true);
                if (animator == null)
                {
                    Debug.LogError(
                        $"[Simple Animator Batching] Runtime prefab instance '{go.name}' has no Animator.", this);
                }

                // Validate skeleton topology against the bake once per instance (cheap, one-time).
                // Throws with a clear message if the runtime prefab was edited after baking.
                archetype.ValidateHierarchyOrThrow(root);

                // Append this instance's bones in baked order, so global index == slot*boneCount + bone.
                for (int b = 0; b < boneCount; b++)
                    boneTransforms.Add(archetype.ResolveBone(root, b));

                go.SetActive(false);

                instances[slot] = go;
                instanceRoots[slot] = root;
                animators[slot] = animator;
                activeMask[slot] = false;
                generation[slot] = 0;
                freeSlots.Push(slot);
            }

            warmedUp = true;

            // Stand up the batched renderer. If the archetype has no material yet (e.g. the shader
            // step hasn't been done), the pool still animates — it just isn't drawn.
            if (archetype.Material != null && archetype.BakedMesh != null)
            {
                crowdRenderer = new CrowdRenderer(
                    archetype.BakedMesh, archetype.Material, capacity, boneCount, activeMask,
                    instanceCenters, cullingBoundingRadius);
            }
            else
            {
                Debug.LogWarning(
                    $"[Simple Animator Batching] Archetype '{archetype.name}' has no material assigned; " +
                    "instances will animate but not render. Assign a CrowdLit/Crowd Unlit material to the archetype.",
                    this);
            }
        }

        /// <summary>
        /// Activates one pooled instance at the given pose. Returns <see cref="CrowdInstanceHandle.Invalid"/>
        /// if the pool is exhausted (this is logged, not thrown — size MaxInstances for your peak wave).
        /// </summary>
        public CrowdInstanceHandle Spawn(Vector3 position, Quaternion rotation)
        {
            if (!warmedUp)
                WarmUp();
            if (!warmedUp)
                return CrowdInstanceHandle.Invalid;

            if (freeSlots.Count == 0)
            {
                Debug.LogError(
                    $"[Simple Animator Batching] Pool for '{archetype.name}' is full ({instances.Length} " +
                    "instances). Spawn ignored. Increase MaxInstances to cover your peak concurrent count.", this);
                return CrowdInstanceHandle.Invalid;
            }

            int slot = freeSlots.Pop();

            Transform root = instanceRoots[slot];
            root.SetPositionAndRotation(position, rotation);

            GameObject go = instances[slot];
            go.SetActive(true);

            // Anti-teleport, requirement 1 (state correctness): never inherit the pose/state of the
            // slot's previous occupant. Rebind resets the Animator to its defaults, then optionally
            // force the configured entry state so the new instance starts from a known animation.
            Animator animator = animators[slot];
            if (animator != null)
            {
                animator.Rebind();
                if (defaultStateHash != 0)
                    animator.Play(defaultStateHash, defaultStateLayer, 0f);

                // Anti-teleport, requirement 2 (no visual snap): synchronously evaluate the entry
                // pose now, before the slot is marked active / drawn, so the very first rendered
                // frame already shows the correct pose instead of a one-frame T-pose or stale pose.
                animator.Update(0f);
            }

            generation[slot]++;
            activeMask[slot] = true;

            return new CrowdInstanceHandle(slot, generation[slot]);
        }

        public CrowdInstanceHandle Spawn(Vector3 position) => Spawn(position, Quaternion.identity);

        /// <summary>
        /// Deactivates the instance referenced by the handle and returns its slot to the pool.
        /// A stale or already-despawned handle is a warning, not an exception — double-despawn is a
        /// common, non-fatal mistake in caller code.
        /// </summary>
        public void Despawn(CrowdInstanceHandle handle)
        {
            if (!IsValid(handle))
            {
                Debug.LogWarning(
                    $"[Simple Animator Batching] Despawn called with a stale/invalid handle {handle}; ignored.", this);
                return;
            }

            int slot = handle.slot;
            activeMask[slot] = false;
            instances[slot].SetActive(false); // stops the Animator updating, freeing CPU
            freeSlots.Push(slot);
        }

        /// <summary>
        /// True only while the instance the handle refers to is still alive: the slot is active and
        /// its generation still matches the handle. Reused slots invalidate older handles.
        /// </summary>
        public bool IsValid(CrowdInstanceHandle handle)
        {
            if (!warmedUp || !handle.IsCreated)
                return false;
            int slot = handle.slot;
            if (slot < 0 || slot >= instances.Length)
                return false;
            return activeMask[slot] && generation[slot] == handle.generation;
        }

        /// <summary>
        /// Gives the package user the live Animator for a spawned instance so they can drive it with
        /// the ordinary Mecanim API (SetFloat, CrossFade, etc.). Returns false for invalid handles.
        /// </summary>
        public bool TryGetAnimator(CrowdInstanceHandle handle, out Animator animator)
        {
            if (IsValid(handle))
            {
                animator = animators[handle.slot];
                return animator != null;
            }
            animator = null;
            return false;
        }

        void LateUpdate()
        {
            if (!warmedUp)
                return;
            CollectBoneMatrices();
        }

        /// <summary>
        /// Schedules and completes the bone-matrix collect job for this frame. Animators have already
        /// run by LateUpdate, so the transforms hold the current poses. Inactive instances are skipped
        /// by Unity's transform job iteration. Exposed internally so tests / the renderer can force a
        /// refresh on demand.
        /// </summary>
        internal void CollectBoneMatrices()
        {
            if (!warmedUp || boneTransforms.length == 0)
                return;

            var job = new BoneMatrixCollectJob
            {
                bindPoses = bindPosesNative,
                boneCount = boneCount,
                skinMatrices = skinMatrices,
            };
            collectHandle = job.Schedule(boneTransforms);
            collectScheduled = true;

            // For v1 we complete within the frame. Once the BatchRendererGroup is wired up, completion
            // can move to just before the GraphicsBuffer upload to overlap with other main-thread work.
            collectHandle.Complete();
            collectScheduled = false;

            // Refresh cull-sphere centers for active instances (anchored at the root position).
            for (int slot = 0; slot < instanceRoots.Length; slot++)
            {
                if (activeMask[slot])
                    instanceCenters[slot] = instanceRoots[slot].position;
            }

            // Push the fresh world-space matrices to the GPU for this frame's draw.
            crowdRenderer?.UploadSkinMatrices(skinMatrices);
        }

        void OnDestroy()
        {
            if (collectScheduled)
                collectHandle.Complete();

            crowdRenderer?.Dispose();
            crowdRenderer = null;

            // GameObjects are children of poolContainer and are torn down by Unity automatically;
            // only the native containers need explicit disposal.
            if (boneTransforms.isCreated)
                boneTransforms.Dispose();
            if (skinMatrices.IsCreated)
                skinMatrices.Dispose();
            if (bindPosesNative.IsCreated)
                bindPosesNative.Dispose();
            if (instanceCenters.IsCreated)
                instanceCenters.Dispose();
        }
    }
}
