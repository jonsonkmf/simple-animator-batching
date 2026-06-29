# Simple Animator Batching

**Render crowds of independently Mecanim-animated characters in a single draw call per archetype — without giving up the full Unity `Animator`.**

GPU skinning + `BatchRendererGroup` (BRG) instancing for URP. Every instance keeps a complete Mecanim `Animator` (state machines, blend trees, IK, arbitrary runtime control via `SetFloat`/`CrossFade`/…), yet all visible instances of an archetype are drawn with **one `BatchDrawCommand`** per render pass.

![400 independently animated characters drawn in one batched draw call](Packages/SimpleAnimatorBatching/Documentation~/crowd-demo.gif)

> 400 characters, each with its own Mecanim `Animator` and animation phase. As the on-screen HUD
> ramps **Instances 1 → 400** and **Triangles → ~58M**, the **batch / SetPass count stays flat** and
> **Visible Skinned Meshes stays 0** — the whole crowd is one batched draw.
> [▶ Full-quality MP4](Packages/SimpleAnimatorBatching/Documentation~/crowd-demo.mp4)

> The package itself lives in [`Packages/SimpleAnimatorBatching`](Packages/SimpleAnimatorBatching) (`com.simpleanimatorbatching.core`); the demo scene is under [`Assets/Demo`](Assets/Demo).

---

## Why this exists

Unity gives you two ends of a spectrum and nothing in the middle:

| Approach | Independent animation | Draw calls | Full Animator |
|---|---|---|---|
| `SkinnedMeshRenderer` per character | ✅ | **N** (one per character) | ✅ |
| Baked vertex-animation textures (VAT) | ❌ fixed clips, no blending | 1 | ❌ |
| **Simple Animator Batching** | ✅ | **1 per archetype** | ✅ |

The trick: keep the real `Animator` driving a live skeleton on the CPU, but **strip the `SkinnedMeshRenderer`** and do the skinning yourself in a custom HLSL shader fed by a per-frame matrix buffer. The CPU still pays for `Animator.Update`; the GPU draws the whole crowd in one batched command.

## Measured results

- **Batch count flat under instance growth.** Ramping the active crowd from 1 → 400 instances (~58M triangles) held the whole-frame batch / SetPass count essentially constant (24 → 26, the small delta being shadow/skybox passes, not the crowd) — the crowd itself is always one `BatchDrawCommand`. See the demo video above.
- **`UnityStats.visibleSkinnedMeshes == 0`** while the package is running — proof the standard skinned-mesh path is fully bypassed and every pixel comes from the BRG batch.
- **Per-split frustum culling verified:** camera facing the crowd → 27/27 instances pass; facing away → 0/27; angled → 3/27.
- Shadows (cast **and** receive) follow the animated pose, not the bind pose.

## How it works

```
Animator (CPU, per instance)
   │  drives a live bone hierarchy each frame
   ▼
BoneMatrixCollectJob  ── Burst + IJobParallelForTransform ──▶  NativeArray<float4x4>
   │  reads bone world matrices, multiplies by bind poses        (slot*boneCount + bone)
   ▼
_SkinMatrices  (StructuredBuffer, uploaded once per frame)
   │
   ▼
CrowdRenderer (BatchRendererGroup)
   │  identity per-instance matrices + _BoneOffset via DOTS instancing
   │  OnPerformCulling → one BatchDrawCommand per visible split
   ▼
CrowdLit / CrowdUnlit shader  ──▶  GPU vertex skinning in SkinningCommon.hlsl
```

Key implementation details:

- **Flat skinning layout.** A single `TransformAccessArray` covers every `(slot, bone)` as `slot * boneCount + bone`, so one Burst job fills the entire crowd's matrices in parallel.
- **DOTS-instanced per-instance data.** `unity_ObjectToWorld` / `unity_WorldToObject` are identity (the skinning shader places vertices directly in world space); a per-instance `_BoneOffset` indexes each instance's slice of `_SkinMatrices`. All packed into a single raw `GraphicsBuffer` with BRG metadata.
- **Custom URP shaders.** `CrowdLit` implements ForwardLit / ShadowCaster / DepthOnly / DepthNormals passes (direct light, ambient SH, shadow casting **and** receiving); `CrowdUnlit` is the minimal variant. Skinning lives in `SkinningCommon.hlsl`.
- **Pooled, generation-checked instances.** `CrowdSpawner` owns a fixed-capacity pool with `Spawn`/`Despawn` and generation-stamped handles (a reused slot invalidates stale handles). Spawn does `Rebind → Play(entryState) → Update(0)` to eliminate one-frame T-pose / stale-pose teleport artifacts.
- **Editor tooling.** `CrowdArchetypeBaker` bakes a source prefab into a runtime archetype (packs `boneIndices`/`boneWeights` into mesh UV2/UV3, strips all `SkinnedMeshRenderer`s, picks the highest-vertex-count mesh). A step-by-step inspector validates Animator / SMR count / URP / `maxInstances` and gates baking on errors.

## Usage

1. **Create an archetype:** `Assets → Create → Simple Animator Batching → Crowd Archetype`.
2. In the inspector set **Source Prefab** (a prefab with `SkinnedMeshRenderer` + `Animator`), follow the validation steps, create a `CrowdLit` material, and press **Bake**.
3. Add a **`CrowdSpawner`** to a scene object, assign the baked archetype, set `MaxInstances` and the default Animator state.
4. From your gameplay code:

```csharp
var handle = spawner.Spawn(position, rotation);   // pulls a pooled instance
if (spawner.TryGetAnimator(handle, out var animator))
    animator.SetFloat("Speed", 3f);               // drive it like any Animator
// ...
spawner.Despawn(handle);                           // returns the slot to the pool
```

A demo scene (`Assets/Demo/CrowdDemo.unity`) with `CrowdWaveDemo.cs` spawns 40 X-Bots with randomized phase/speed and churns the pool in waves.

## Limitations / roadmap

- One archetype per pool (multi-archetype batching is on the roadmap).
- Up to 4 bone influences per vertex (detailed facial rigs would need more).
- Culling runs on the main thread (correct, but a Burst job is the natural next optimization).
- GLES3 constant-buffer size (~16 KB) not yet validated for very high bone counts.

## Requirements

- Unity **2022.3** (developed on 2022.3.22f1)
- **URP 14.0.x** (`com.unity.render-pipelines.universal`)
- Burst + Collections + Mathematics (Unity packages)
